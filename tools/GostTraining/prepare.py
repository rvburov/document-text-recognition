#!/usr/bin/env python3
"""Create line boxes/LSTMF only from frozen train/development ground truth, never OCR text."""
import argparse
import concurrent.futures
import hashlib
import json
import os
from pathlib import Path
import random
import shutil
import struct
import subprocess

BASE_HASHES = {"rus": "b617eb6830ffabaaa795dd87ea7fd251adfe9cf0efe05eb9a2e8128b7728d6b6",
               "eng": "8280aed0782fe27257a68ea10fe7ef324ca0f8d85bd2fd145d1c2b560bcb66ba"}

def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def run(command):
    result = subprocess.run(command, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            env={**os.environ, "OMP_THREAD_LIMIT": "1"})
    if result.returncode:
        raise RuntimeError(f"{command!r}\n{result.stdout}")
    return result.stdout

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--corpus", type=Path, required=True)
    parser.add_argument("--evaluation-isolated", action="store_true", help="Do not read held-out files or truths; they remain in separate custody.")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--base-data", type=Path, required=True)
    parser.add_argument("--langdata", type=Path, required=True)
    parser.add_argument("--jobs", type=int, default=2)
    parser.add_argument("--supplement-train", type=Path, action="append", default=[])
    parser.add_argument("--box-geometry", choices=("full", "ink"), default="full")
    parser.add_argument("--box-padding", type=int, default=2)
    parser.add_argument("--charset", choices=("russian", "cyrillic-latin"), default="russian")
    args = parser.parse_args()
    corpus, output = args.corpus.resolve(), args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    base = output / "base"
    base.mkdir(exist_ok=True)
    for lang, expected in BASE_HASHES.items():
        source = args.base_data.resolve() / f"{lang}.traineddata"
        if digest(source) != expected:
            raise ValueError(f"Unpinned {lang} base model")
        parts = [str(base / f"{lang}.lstm-unicharset")]
        if lang == "rus": parts.append(str(base / "rus.lstm"))
        run(["combine_tessdata", "-e", str(source), *parts])
    if args.charset == "cyrillic-latin":
        run(["merge_unicharsets", str(base / "rus.lstm-unicharset"),
             str(base / "eng.lstm-unicharset"), str(base / "gost.unicharset")])
        # Optional second experiment: map old outputs and initialize new Latin outputs.
        build_log = run(["combine_lang_model", "--input_unicharset", str(base / "gost.unicharset"),
            "--script_dir", str(args.langdata.resolve()), "--output_dir", str(base),
            "--lang", "rus_gost", "--version_str", "gost-pilot-v1-cyrillic-latin"])
        (output / "charset-build.log").write_text(build_log)
    else:
        shutil.copyfile(base / "rus.lstm-unicharset", base / "gost.unicharset")
        (base / "rus_gost").mkdir(exist_ok=True)
        shutil.copyfile(args.base_data.resolve() / "rus.traineddata", base / "rus_gost/rus_gost.traineddata")
    alphabet = {line.split()[0] for line in (base / "gost.unicharset").read_text().splitlines()[1:]}
    alphabet.add(" ")
    entries = []
    truths_by_split = {}
    splits = ("train", "development") if args.evaluation_isolated else ("train", "development", "evaluation")
    for split in splits:
        truths = set()
        files = sorted((corpus / split).rglob("*.gt.txt"))
        if split == "train":
            for supplement in args.supplement_train:
                files.extend(sorted(supplement.resolve().rglob("*.gt.txt")))
        if not files:
            raise ValueError(f"No frozen {split} lines in {corpus}")
        for gt in files:
            text = gt.read_text(encoding="utf-8").rstrip("\r\n")
            if not text or "\n" in text or "\r" in text or "\t" in text:
                raise ValueError(f"Expected one nonempty line: {gt}")
            missing = sorted(set(text) - alphabet)
            if missing and args.charset == "cyrillic-latin":
                raise ValueError(f"GT contains characters outside combined alphabet: {missing!r} in {gt}")
            image = gt.with_name(gt.name[:-7] + ".png")
            header = image.read_bytes()[:26]
            if header[:8] != b"\x89PNG\r\n\x1a\n":
                raise ValueError(f"Not PNG: {image}")
            width, height = struct.unpack(">II", header[16:24])
            if header[24:26] != b"\x08\x00":
                raise ValueError(f"Require 8-bit grayscale PNG: {image}")
            truths.add(text)
            entries.append({"split": split, "id": image.stem, "truth": text,
                "image": str(image), "image_sha256": digest(image), "gt_sha256": digest(gt),
                "width": width, "height": height, "unsupported_characters": missing})
        truths_by_split[split] = truths
    ids = [(entry["split"], entry["id"]) for entry in entries]
    if len(ids) != len(set(ids)):
        raise ValueError("Duplicate image identifiers across source directories")
    for left, right in (("train", "development"), ("train", "evaluation"), ("development", "evaluation")):
        if left not in truths_by_split or right not in truths_by_split: continue
        common = truths_by_split[left] & truths_by_split[right]
        if common:
            raise ValueError(f"Ground-truth leakage between {left}/{right}: {sorted(common)}")
    training = [entry for entry in entries if entry["split"] != "evaluation" and not entry["unsupported_characters"]]
    def prepare(entry):
        image = Path(entry["image"])
        stem = output / "lines" / entry["split"] / entry["id"]
        stem.parent.mkdir(parents=True, exist_ok=True)
        # Line-level ground truth: every glyph has the same line rectangle, then a tab
        # line terminator. This is the tesstrain generate_line_box representation.
        left, bottom, right, top = 0, 0, entry["width"], entry["height"]
        if args.box_geometry == "ink":
            from PIL import Image
            with Image.open(image) as source:
                ink = source.point(lambda value: 255 if value < 200 else 0).getbbox()
            if ink is None:
                raise ValueError(f"No ink in {image}")
            left = max(0, ink[0] - args.box_padding)
            right = min(entry["width"], ink[2] + args.box_padding)
            bottom = max(0, entry["height"] - ink[3] - args.box_padding)
            top = min(entry["height"], entry["height"] - ink[1] + args.box_padding)
        box = "".join(f"{char} {left} {bottom} {right} {top} 0\n" for char in entry["truth"])
        box += f"\t {left} {bottom} {right} {top} 0\n"
        # Tesseract reads the .box adjacent to the image; retain corpus files unchanged.
        target_image = stem.with_suffix(".png")
        if target_image.exists() or target_image.is_symlink(): target_image.unlink()
        target_image.symlink_to(image)
        stem.with_suffix(".box").write_text(box, encoding="utf-8")
        command = ["tesseract", str(target_image), str(stem), "--psm", "13", "lstm.train"]
        log = run(command)
        lstmf = stem.with_suffix(".lstmf")
        if not lstmf.exists() or not lstmf.stat().st_size:
            raise RuntimeError(f"LSTMF not generated: {command!r}\n{log}")
        return {**entry, "lstmf": str(lstmf), "lstmf_sha256": digest(lstmf)}
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.jobs) as pool:
        prepared = list(pool.map(prepare, training))
    for split in ("train", "development"):
        lines = [entry["lstmf"] for entry in prepared if entry["split"] == split]
        random.Random(20261118).shuffle(lines)
        (output / f"{split}.list").write_text("\n".join(lines) + "\n")
    report = {"schema_version": 1, "ground_truth_source": "frozen authored text; no OCR labels",
              "tesseract_version": run(["tesseract", "--version"]).splitlines()[0],
              "base_model_sha256": BASE_HASHES, "charset": args.charset,
              "box_geometry": args.box_geometry, "box_padding": args.box_padding,
              "supplement_train": [str(p.resolve()) for p in args.supplement_train],
              "excluded_from_lstmf": [e for e in entries if e["split"] != "evaluation" and e["unsupported_characters"]],
              "seed": 20261118, "evaluation_prepared_for_training": False,
              "evaluation_isolated_not_read": args.evaluation_isolated,
              "counts": {s: {"images": sum(e["split"] == s for e in entries), "unique_truths": len(t)}
                         for s, t in truths_by_split.items()}, "prepared": prepared,
              "evaluation": [e for e in entries if e["split"] == "evaluation"]}
    (output / "preparation.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n")
    print(json.dumps(report["counts"], indent=2))

if __name__ == "__main__":
    main()
