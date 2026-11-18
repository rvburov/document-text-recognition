#!/usr/bin/env python3
"""Frozen-model, development-only inference grid. Never reads evaluation images or answers."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import statistics
import time
from PIL import Image, ImageOps
from native_tesseract import Reader
from evaluate import distance


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--corpus", type=Path, required=True)
    parser.add_argument("--tessdata", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--languages", default="rus_gost+eng")
    args = parser.parse_args()
    if args.output.exists(): raise ValueError("Refusing to overwrite a recorded grid")
    args.output.mkdir(parents=True)
    split = args.corpus.resolve() / "development"
    manifest_file = split / "lines.json"
    manifest = json.loads(manifest_file.read_text())
    request = {"split": "development", "evaluation_read": False,
        "manifest_sha256": hashlib.sha256(manifest_file.read_bytes()).hexdigest(),
        "model_sha256": {lang: hashlib.sha256((args.tessdata/f"{lang}.traineddata").read_bytes()).hexdigest() for lang in args.languages.split("+")},
        "psm": [6, 7, 13], "geometry": ["full", "ink", "ink+8"], "ink_threshold": 200,
        "selection_criterion": "whole-line exact, then designation exact, then CER"}
    (args.output / "request.json").write_text(json.dumps(request, indent=2)+"\n")
    prepared = []
    for entry in manifest["Lines"]:
        original = split / entry["File"]
        gt = split / entry["GroundTruthFile"]
        if hashlib.sha256(original.read_bytes()).hexdigest() != entry["CropSha256"]:
            raise ValueError(f"Unfrozen image: {original}")
        if hashlib.sha256(gt.read_bytes()).hexdigest() != entry["GroundTruthSha256"]:
            raise ValueError(f"Unfrozen truth: {gt}")
        truth = " ".join(gt.read_text().split())
        paths = {"full": original}
        with Image.open(original) as image:
            image = image.convert("L")
            box = image.point(lambda value: 255 if value < 200 else 0).getbbox()
            if box is None: raise ValueError(f"No ink: {original}")
            tight = image.crop(box)
            for kind, derived in (("ink", tight), ("ink+8", ImageOps.expand(tight, border=8, fill=255))):
                path = args.output.resolve() / "images" / kind / original.name
                path.parent.mkdir(parents=True, exist_ok=True)
                derived.save(path, dpi=(entry["Dpi"], entry["Dpi"]))
                paths[kind] = path
        prepared.append({"entry": entry, "truth": truth, "paths": paths, "ink_box": box})
    os.environ["OMP_THREAD_LIMIT"] = "1"
    variants = []
    for psm in (6, 7, 13):
        for geometry in ("full", "ink", "ink+8"):
            reader = Reader(args.tessdata.resolve(), args.languages, psm, capture_symbols=True)
            rows = []
            try:
                for case in prepared:
                    entry, truth = case["entry"], case["truth"]
                    started = time.perf_counter()
                    raw, confidence = reader.read(case["paths"][geometry], entry["Dpi"])
                    elapsed = (time.perf_counter() - started) * 1000
                    actual = " ".join(raw.split())
                    rows.append({"id": entry["Id"], "role": entry["Role"], "truth": truth,
                        "actual": actual, "exact": truth == actual, "character_errors": distance(truth, actual),
                        "word_errors": distance(truth.split(), actual.split()),
                        "line_confidences": confidence, "symbols": reader.symbols,
                        "read_ms": elapsed})
            finally:
                reader.close()
            def summary(group):
                return {"lines": len(group), "exact": sum(r["exact"] for r in group),
                    "cer": sum(r["character_errors"] for r in group) / sum(len(r["truth"]) for r in group),
                    "wer": sum(r["word_errors"] for r in group) / sum(len(r["truth"].split()) for r in group),
                    "wrong_but_line_confident_0_30": sum(not r["exact"] and bool(r["line_confidences"]) and min(r["line_confidences"]) >= .30 for r in group)}
            variants.append({"psm": psm, "geometry": geometry, "summary": summary(rows),
                "by_role": {role: summary([r for r in rows if r["role"] == role]) for role in sorted({r["role"] for r in rows})},
                "median_read_ms": statistics.median(r["read_ms"] for r in rows), "rows": rows})
            print(json.dumps({k:v for k,v in variants[-1].items() if k != "rows"}), flush=True)
    ranked = sorted(variants, key=lambda v: (-v["summary"]["exact"], -v["by_role"]["Designation"]["exact"], v["summary"]["cer"]))
    result = {"schema_version": 1, "split": "development", "evaluation_read": False,
        "model_sha256": {lang: hashlib.sha256((args.tessdata/f"{lang}.traineddata").read_bytes()).hexdigest() for lang in args.languages.split("+")},
        "manifest_sha256": hashlib.sha256(manifest_file.read_bytes()).hexdigest(),
        "grid_frozen": {"psm": [6,7,13], "geometry": ["full", "ink", "ink+8"], "ink_threshold": 200},
        "correction": "none; only collapse output whitespace", "criterion": "whole-line exact, then designation exact, then CER",
        "winner": {"psm": ranked[0]["psm"], "geometry": ranked[0]["geometry"], "summary": ranked[0]["summary"]},
        "timing_scope": "single line C API plus PNG decode and confidence extraction; not an application latency benchmark",
        "variants": variants}
    (args.output / "grid.json").write_text(json.dumps(result, ensure_ascii=False, indent=2)+"\n")

if __name__ == "__main__": main()
