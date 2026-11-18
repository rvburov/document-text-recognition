#!/usr/bin/env python3
"""Record strict Unicode exact-match/CER on a frozen split; no correction or alphabet guessing."""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import statistics
import subprocess
import tempfile
import time

from native_tesseract import Reader


def distance(a, b):
    previous = list(range(len(b) + 1))
    for i, ca in enumerate(a, 1):
        current = [i]
        for j, cb in enumerate(b, 1):
            current.append(min(current[-1] + 1, previous[j] + 1, previous[j-1] + (ca != cb)))
        previous = current
    return previous[-1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--corpus", type=Path, required=True)
    parser.add_argument("--split", choices=("development", "evaluation"), default="development")
    parser.add_argument("--tessdata", type=Path, required=True)
    parser.add_argument("--languages", default="rus_gost+eng")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--backend", choices=("cli", "capi"), default="capi")
    parser.add_argument("--psm", type=int, default=6)
    parser.add_argument("--designation-ink8-psm13", action="store_true",
                        help="Fixed role policy: tight ink bbox (<200) plus 8px white border, PSM13 for designation only.")
    parser.add_argument("--designation-preserve8-psm13", action="store_true",
                        help="Retain original pixels in 8px ink margin; fall back to full PSM6 if weak ink (<245) extends beyond it.")
    parser.add_argument("--final-evaluation", action="store_true",
                        help="Explicitly unlock held-out evaluation after development model selection.")
    args = parser.parse_args()
    if args.designation_ink8_psm13 and args.designation_preserve8_psm13:
        raise ValueError("Choose exactly one role policy")
    if (args.designation_ink8_psm13 or args.designation_preserve8_psm13) and args.backend != "capi":
        raise ValueError("The fixed designation geometry policy requires the C API backend")
    if args.split == "evaluation" and not args.final_evaluation:
        raise ValueError("Choose the model on development first; final evaluation requires --final-evaluation.")
    if args.output.exists():
        raise ValueError(f"Refusing to overwrite recorded results: {args.output}")
    split = args.corpus.resolve() / args.split
    metadata_path = split / "lines.json"
    metadata = json.loads(metadata_path.read_text())
    records = {Path(line["File"]).stem: line for line in metadata["Lines"]}
    rows = []
    os.environ["OMP_THREAD_LIMIT"] = "1"
    initialize_started = time.perf_counter()
    reader = Reader(args.tessdata.resolve(), args.languages, args.psm, capture_symbols=True) if args.backend == "capi" else None
    designation_reader = Reader(args.tessdata.resolve(), args.languages, 13, capture_symbols=True) if (args.designation_ink8_psm13 or args.designation_preserve8_psm13) else None
    temporary = tempfile.TemporaryDirectory(prefix="gost-eval-") if designation_reader else None
    initialization_ms = (time.perf_counter() - initialize_started) * 1000
    for gt in sorted(split.rglob("*.gt.txt")):
        image = gt.with_name(gt.name[:-7] + ".png")
        truth = " ".join(gt.read_text(encoding="utf-8").split())
        meta = records[image.stem]
        if hashlib.sha256(image.read_bytes()).hexdigest() != meta["CropSha256"]:
            raise ValueError(f"Image differs from frozen metadata: {image}")
        if hashlib.sha256(gt.read_bytes()).hexdigest() != meta["GroundTruthSha256"]:
            raise ValueError(f"Ground truth differs from frozen metadata: {gt}")
        source_image = image
        current_reader = reader
        geometry, actual_psm = "full", args.psm
        if designation_reader and meta["Role"] == "Designation":
            from PIL import Image, ImageOps
            with Image.open(source_image) as source:
                source = source.convert("L")
                bbox = source.point(lambda value: 255 if value < 200 else 0).getbbox()
                if bbox is None: raise ValueError(f"No ink: {source_image}")
                if args.designation_preserve8_psm13:
                    weak = source.point(lambda value: 255 if value < 245 else 0).getbbox()
                    requested = (bbox[0]-8, bbox[1]-8, bbox[2]+8, bbox[3]+8)
                    if weak and (weak[0] < requested[0] or weak[1] < requested[1] or weak[2] > requested[2] or weak[3] > requested[3]):
                        derived = None
                    else:
                        derived = Image.new("L", (requested[2]-requested[0], requested[3]-requested[1]), 255)
                        overlap = (max(0,requested[0]), max(0,requested[1]), min(source.width,requested[2]), min(source.height,requested[3]))
                        derived.paste(source.crop(overlap), (overlap[0]-requested[0], overlap[1]-requested[1]))
                else:
                    derived = ImageOps.expand(source.crop(bbox), border=8, fill=255)
                if derived is not None:
                    image = Path(temporary.name) / source_image.name
                    derived.save(image, dpi=(meta["Dpi"], meta["Dpi"]))
                    current_reader, geometry, actual_psm = designation_reader, "preserve+8" if args.designation_preserve8_psm13 else "ink+8", 13
        started = time.perf_counter()
        command = ["tesseract", str(image), "stdout", "--tessdata-dir", str(args.tessdata.resolve()),
                   "-l", args.languages, "--psm", str(args.psm), "--dpi", str(meta["Dpi"])]
        if reader:
            raw, confidences = current_reader.read(image, meta["Dpi"])
        else:
            process = subprocess.run(command, text=True, encoding="utf-8", stdout=subprocess.PIPE,
                                     stderr=subprocess.PIPE, env={**os.environ, "OMP_THREAD_LIMIT": "1"})
            if process.returncode:
                raise RuntimeError(f"{command!r}\n{process.stderr}")
            raw, confidences = process.stdout, []
        elapsed = (time.perf_counter() - started) * 1000
        actual = " ".join(raw.split())
        edit_distance = distance(truth, actual)
        rows.append({"id": meta["Id"], "case_id": meta["CaseId"], "role": meta["Role"],
                     "renderer": meta.get("Renderer", metadata.get("Renderer", "unspecified")),
                     "geometry": geometry, "psm": actual_psm,
                     "truth": truth, "actual": actual, "exact": truth == actual,
                     "edit_distance": edit_distance, "truth_characters": len(truth),
                     "line_confidences": confidences, "min_line_confidence": min(confidences) if confidences else None,
                     "symbols": current_reader.symbols if current_reader else [],
                     "elapsed_ms": elapsed, "image_sha256": hashlib.sha256(source_image.read_bytes()).hexdigest(),
                     "recognition_image_sha256": hashlib.sha256(image.read_bytes()).hexdigest()})
    if reader: reader.close()
    if designation_reader: designation_reader.close()
    if temporary: temporary.cleanup()
    if not rows: raise ValueError("No evaluation rows")
    def aggregate(group):
        return {"lines": len(group), "exact": sum(row["exact"] for row in group),
                "exact_fraction": sum(row["exact"] for row in group) / len(group),
                "cer": sum(row["edit_distance"] for row in group) / sum(row["truth_characters"] for row in group)}
    latencies = sorted(row["elapsed_ms"] for row in rows)
    report = {"schema_version": 1, "split": args.split, "languages": args.languages,
              "model_sha256": {lang: hashlib.sha256((args.tessdata / f"{lang}.traineddata").read_bytes()).hexdigest()
                               for lang in args.languages.split("+")},
              "manifest_sha256": hashlib.sha256(metadata_path.read_bytes()).hexdigest(),
              "backend": args.backend, "psm": args.psm,
              "designation_ink8_psm13": args.designation_ink8_psm13,
              "designation_preserve8_psm13": args.designation_preserve8_psm13,
              "application_geometry_guards": "Not applied: synthetic line files are known one-line crops; the production KnownSingleLineRegion gates need separate end-to-end qualification.",
              "normalization": "collapse whitespace only; preserve case, punctuation, Cyrillic/Latin and ё",
              "post_ocr_corrections": False, "summary": aggregate(rows),
              "confidence": {"level": "Tesseract text line, 0..1" if reader else "not measured",
                             "below_product_threshold_0_30": sum(r["min_line_confidence"] is not None and r["min_line_confidence"] < .30 for r in rows),
                             "wrong_but_confident_0_30": sum(not r["exact"] and r["min_line_confidence"] is not None and r["min_line_confidence"] >= .30 for r in rows)},
              "by_role": {role: aggregate([r for r in rows if r["role"] == role]) for role in sorted({r["role"] for r in rows})},
              "by_renderer": {renderer: aggregate([r for r in rows if r["renderer"] == renderer]) for renderer in sorted({r["renderer"] for r in rows})},
              "timing": {"scope": "C API read per line including PNG decode, excludes model initialization" if reader else "fresh CLI process per line including model loading",
                         "initialization_ms": initialization_ms,
                         "median_ms": statistics.median(latencies), "p95_ms": latencies[min(len(latencies)-1, math.ceil(.95*len(latencies))-1)]},
              "results": rows}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n")
    print(json.dumps({k:v for k,v in report.items() if k != "results"}, ensure_ascii=False, indent=2))

if __name__ == "__main__":
    main()
