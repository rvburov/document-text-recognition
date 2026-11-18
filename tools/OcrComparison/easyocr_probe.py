#!/usr/bin/env python3
"""Experimental CPU/FP32 EasyOCR comparison on immutable, independently labelled crops.

Recognition retains all original softmax probabilities: no allowlist, zeroing of
classes, renormalization or language-model correction. Labels are checked only after
inference. This script does not change the application or its models.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import resource
import statistics
import time

os.environ.setdefault("OMP_NUM_THREADS", "1")
os.environ.setdefault("OMP_THREAD_LIMIT", "1")
os.environ.setdefault("MKL_NUM_THREADS", "1")
import cv2
import numpy as np
import torch
import easyocr
import easyocr.recognition as recognition


# Derived from EasyOCR 1.7.2 recognizer_predict (Apache-2.0, Jaided AI):
# https://github.com/JaidedAI/EasyOCR/blob/v1.7.2/easyocr/recognition.py
# Modification: retain the full unmasked softmax distribution; greedy decode only.
def recognizer_predict_raw(model, converter, test_loader, batch_max_length,
                           ignore_idx, char_group_idx, decoder="greedy", beamWidth=5, device="cpu"):
    if decoder != "greedy":
        raise ValueError("The comparison only supports uncorrected greedy decoding")
    model.eval()
    results = []
    with torch.no_grad():
        for image_tensors in test_loader:
            batch_size = image_tensors.size(0)
            image = image_tensors.to(device)
            text_for_pred = torch.LongTensor(batch_size, batch_max_length + 1).fill_(0).to(device)
            logits = model(image, text_for_pred)
            sizes = torch.IntTensor([logits.size(1)] * batch_size)
            probability = torch.softmax(logits, dim=2)
            values, indices = probability.max(2)
            text = converter.decode_greedy(indices.reshape(-1).cpu().numpy(), sizes.numpy())
            for value, index, decoded in zip(values.cpu().numpy(), indices.cpu().numpy(), text):
                # Same native EasyOCR aggregate, using ORIGINAL, unmasked probabilities.
                nonblank = value[index != 0]
                confidence = recognition.custom_mean(nonblank) if len(nonblank) else 0.0
                results.append([decoded, float(confidence)])
    return results


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path.cwd())
    parser.add_argument("--cases", type=Path, default=Path("tools/OcrComparison/input-cases.json"))
    parser.add_argument("--output", type=Path, default=Path("artifacts/ocr-candidates/easyocr-raw.json"))
    parser.add_argument("--models", type=Path, default=Path("artifacts/ocr-candidates/models/easyocr"))
    parser.add_argument("--repeats", type=int, default=3)
    args = parser.parse_args()
    root = args.root.resolve()
    manifest_path = root / args.cases
    manifest = json.loads(manifest_path.read_text())
    cv2.setNumThreads(1)
    torch.set_num_threads(1)
    torch.set_num_interop_threads(1)
    recognition.recognizer_predict = recognizer_predict_raw
    initialized = time.perf_counter()
    reader = easyocr.Reader(["ru", "en"], gpu=False, quantize=False,
                            model_storage_directory=str(root / args.models), verbose=False)
    initialization_ms = (time.perf_counter() - initialized) * 1000
    rows = []
    for case in manifest["Cases"]:
        file = root / case["File"]
        digest = hashlib.sha256(file.read_bytes()).hexdigest()
        if digest.lower() != case["CropSha256"].lower():
            raise ValueError(f"Checksum mismatch: {case['File']}")
        crop = cv2.imread(str(file), cv2.IMREAD_GRAYSCALE)
        if crop is None:
            raise ValueError(f"Unable to load {file}")
        started = time.perf_counter()
        result = reader.readtext(crop, decoder="greedy", batch_size=1, workers=0,
                                 detail=1, paragraph=False, allowlist=None, blocklist=None)
        cold_ms = (time.perf_counter() - started) * 1000
        timings = []
        for _ in range(args.repeats):
            started = time.perf_counter()
            repeated = reader.readtext(crop, decoder="greedy", batch_size=1, workers=0,
                                      detail=1, paragraph=False, allowlist=None, blocklist=None)
            timings.append((time.perf_counter() - started) * 1000)
            if [x[1] for x in repeated] != [x[1] for x in result]:
                raise RuntimeError("Repeated input changed output: " + case["Id"])
        lines = []
        for box, text, confidence in result:
            points = np.asarray(box)
            lines.append(dict(Text=text, Confidence=float(confidence), Left=int(points[:, 0].min()),
                              Top=int(points[:, 1].min()), Right=int(points[:, 0].max()), Bottom=int(points[:, 1].max())))
        lines.sort(key=lambda x: (x["Top"], x["Left"]))
        raw = "\n".join(line["Text"] for line in lines)
        row = dict(case, Engine="EasyOCR1.7.2", Raw=raw, Reading=dict(Engine="EasyOCR", Lines=lines),
                   RawExact=" ".join(raw.split()) == case["Expected"],
                   MinConfidence=min((line["Confidence"] for line in lines), default=0),
                   ColdMilliseconds=cold_ms, WarmMilliseconds=timings,
                   MedianMilliseconds=statistics.median(timings),
                   PeakWorkingSetBytes=resource.getrusage(resource.RUSAGE_SELF).ru_maxrss * 1024)
        rows.append(row)
        print(f"{case['Id']}: {raw!r} confidence={row['MinConfidence']:.4f} exact={row['RawExact']} {cold_ms:.1f}ms", flush=True)
    model_hashes = {str(path.relative_to(root)): hashlib.sha256(path.read_bytes()).hexdigest()
                    for path in (root / args.models).glob("*.pth")}
    output = dict(SchemaVersion=1, Engine="EasyOCR1.7.2", Device="cpu", Quantization=False,
                  Languages=["ru", "en"], Decoder="greedy, unmasked original softmax", Threads=1,
                  TorchVersion=torch.__version__, OpenCVVersion=cv2.__version__, NumpyVersion=np.__version__,
                  InitializationMilliseconds=initialization_ms,
                  InputManifestSha256=hashlib.sha256(manifest_path.read_bytes()).hexdigest(),
                  ModelSha256=model_hashes, Results=rows,
                  PeakWorkingSetBytes=resource.getrusage(resource.RUSAGE_SELF).ru_maxrss * 1024)
    destination = root / args.output
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(json.dumps(output, ensure_ascii=False, indent=2) + "\n")


if __name__ == "__main__":
    main()
