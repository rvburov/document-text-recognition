#!/usr/bin/env python3
"""Convert the two existing FP32 Paddle V5 models; record source/output hashes."""
import argparse
import hashlib
import importlib.metadata
import json
import pathlib
import subprocess
import sys

import onnx

PINNED = {"paddlepaddle": "3.3.1", "paddle2onnx": "2.1.0", "onnx": "1.17.0"}


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("paddle_models", type=pathlib.Path)
    parser.add_argument("output", type=pathlib.Path)
    args = parser.parse_args()
    versions = {name: importlib.metadata.version(name) for name in PINNED}
    if versions != PINNED:
        raise RuntimeError(f"Expected pinned conversion versions {PINNED}, found {versions}")
    args.output.mkdir(parents=True, exist_ok=True)
    rows = []
    for source_name, output_name in [("mobile_zh_det", "det.onnx"), ("eslav_PP_OCRv5_mobile_rec", "rec.onnx")]:
        source = args.paddle_models / source_name
        inputs = [source / "inference.json", source / "inference.pdiparams"]
        for path in inputs:
            if not path.is_file():
                raise FileNotFoundError(path)
        target = args.output / output_name
        subprocess.run([
            sys.executable, "-m", "paddle2onnx.command",
            "--model_dir", str(source), "--model_filename", "inference.json",
            "--params_filename", "inference.pdiparams", "--save_file", str(target),
            "--opset_version", "14", "--enable_auto_update_opset", "False",
            "--enable_onnx_checker", "True", "--optimize_tool", "None",
        ], check=True)
        model = onnx.load(target)
        if [(entry.domain, entry.version) for entry in model.opset_import] != [("", 14)]:
            raise RuntimeError(f"Unexpected operator set in {target}")
        # The converter emits IR7. IR8 is accepted by the bundled ORT1.11.1 and was
        # used in the initial measured proof; only format metadata changes here.
        model.ir_version = 8
        onnx.checker.check_model(model)
        onnx.save(model, target)
        rows.append({"SourceModel": source_name,
                     "Inputs": [{"File": p.name, "Sha256": sha(p)} for p in inputs],
                     "Output": output_name, "Sha256": sha(target), "IrVersion": 8, "Opset": 14})
    manifest = {"Python": sys.version, "Packages": versions,
                "Conversion": "FP32; no quantization; no optional optimizer; checked ONNX opset14/IR8",
                "Models": rows}
    (args.output / "conversion-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(args.output / "conversion-manifest.json")


if __name__ == "__main__":
    main()
