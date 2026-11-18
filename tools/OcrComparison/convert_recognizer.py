#!/usr/bin/env python3
"""Convert one candidate V5 recognizer using the same FP32 opset14/IR8 recipe as shipping OCR."""
import argparse
import hashlib
import importlib.metadata
import json
from pathlib import Path
import subprocess
import sys

import onnx

PINS = {"paddlepaddle": "3.3.1", "paddle2onnx": "2.1.0", "onnx": "1.17.0"}
SOURCE_URL = "https://paddle-model-ecology.bj.bcebos.com/paddlex/official_inference_model/paddle3.0.0/cyrillic_PP-OCRv5_mobile_rec_infer.tar"


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    versions = {name: importlib.metadata.version(name) for name in PINS}
    if versions != PINS:
        raise RuntimeError(f"Expected {PINS}, found {versions}")
    inputs = [args.source / name for name in ("inference.json", "inference.pdiparams", "inference.yml")]
    for path in inputs:
        if not path.is_file():
            raise FileNotFoundError(path)
    args.output.mkdir(parents=True, exist_ok=True)
    target = args.output / "rec.onnx"
    subprocess.run([sys.executable, "-m", "paddle2onnx.command", "--model_dir", str(args.source),
                    "--model_filename", "inference.json", "--params_filename", "inference.pdiparams",
                    "--save_file", str(target), "--opset_version", "14", "--enable_auto_update_opset", "False",
                    "--enable_onnx_checker", "True", "--optimize_tool", "None"], check=True)
    model = onnx.load(target)
    if [(entry.domain, entry.version) for entry in model.opset_import] != [("", 14)]:
        raise RuntimeError("Unexpected ONNX opset")
    model.ir_version = 8
    onnx.checker.check_model(model)
    onnx.save(model, target)
    manifest = {"SourceModel": "cyrillic_PP-OCRv5_mobile_rec", "SourceUrl": SOURCE_URL,
                "SourceDirectory": str(args.source), "Python": sys.version, "Packages": versions,
                "Inputs": [{"File": p.name, "Sha256": sha(p)} for p in inputs],
                "Output": {"File": "rec.onnx", "Sha256": sha(target), "Bytes": target.stat().st_size},
                "Conversion": "FP32; no quantization; no optional optimizer; checked ONNX opset14/IR8",
                "Dictionary": "Use inference.yml character_dict from the same source model; do not reuse eslav vocabulary"}
    (args.output / "conversion-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print(json.dumps(manifest, indent=2))


if __name__ == "__main__":
    main()
