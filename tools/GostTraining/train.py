#!/usr/bin/env python3
"""Fine-tune a pinned Tesseract network; output candidates only, never application tessdata."""
import argparse
import hashlib
import json
import os
import re
import shutil
from pathlib import Path
import subprocess
import time


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def logged(command, log, env):
    start = time.perf_counter()
    with log.open("w") as output:
        output.write(json.dumps(command) + "\n")
        output.flush()
        process = subprocess.Popen(command, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, env=env)
        for line in process.stdout:
            output.write(line)
            output.flush()
            print(line, end="", flush=True)
        code = process.wait()
    if code:
        raise RuntimeError(f"Command failed ({code}); see {log}")
    return time.perf_counter() - start


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prepared", required=True, type=Path)
    parser.add_argument("--base-data", required=True, type=Path)
    parser.add_argument("--run", required=True, help="Separate output directory name, e.g. pilot-500")
    parser.add_argument("--iterations", type=int, default=500)
    parser.add_argument("--learning-rate", type=float, default=0.0001)
    parser.add_argument("--target-error-rate", type=float, default=0.01,
                        help="Internal training CER stopping threshold; -1 disables it for a bounded full pass.")
    parser.add_argument("--continue-from", type=Path)
    parser.add_argument("--inference-eng", type=Path, default=Path(__file__).resolve().parents[2] / "ocr/tessdata/eng.traineddata")
    args = parser.parse_args()
    prepared = args.prepared.resolve()
    preparation = json.loads((prepared / "preparation.json").read_text())
    if preparation.get("evaluation_prepared_for_training") is not False:
        raise ValueError("Missing evaluation isolation proof")
    if args.iterations <= 0 or args.learning_rate <= 0 or Path(args.run).name != args.run:
        raise ValueError("Invalid run name or training parameters")
    output = prepared / "runs" / args.run
    if output.exists():
        raise ValueError(f"Refusing to overwrite a recorded run: {output}")
    output.mkdir(parents=True)
    shutil.copyfile(args.base_data.resolve() / "LICENSE", output / "LICENSE.tesseract")
    shutil.copyfile(args.base_data.resolve() / "source.json", output / "base-source.json")
    starter = prepared / "base/rus_gost/rus_gost.traineddata"
    source = args.continue_from.resolve() if args.continue_from else prepared / "base/rus.lstm"
    prefix = output / "rus_gost"
    environment = {**os.environ, "OMP_THREAD_LIMIT": "1", "OPENBLAS_NUM_THREADS": "1"}
    command = ["lstmtraining", "--continue_from", str(source), "--traineddata", str(starter),
        "--model_output", str(prefix), "--train_listfile", str(prepared / "train.list"),
        "--eval_listfile", str(prepared / "development.list"), "--max_iterations", str(args.iterations),
        "--learning_rate", str(args.learning_rate), "--max_image_MB", "512", "--target_error_rate", str(args.target_error_rate)]
    if not args.continue_from and preparation.get("charset") == "cyrillic-latin":
        command += ["--old_traineddata", str(args.base_data.resolve() / "rus.traineddata")]
    elapsed = logged(command, output / "training.log", environment)
    checkpoint = Path(str(prefix) + "_checkpoint")
    if not checkpoint.exists():
        raise FileNotFoundError(f"Training produced no checkpoint: {checkpoint}")
    # Export both forms so exact recognition and latency changes from quantization can be measured.
    for mode in ("float", "int"):
        destination = output / mode
        destination.mkdir()
        shutil.copyfile(args.inference_eng, destination / "eng.traineddata")
        export = ["lstmtraining", "--stop_training", "--continue_from", str(checkpoint),
                  "--traineddata", str(starter), "--model_output", str(destination / "rus_gost.traineddata")]
        if mode == "int": export.append("--convert_to_int")
        logged(export, output / f"export-{mode}.log", environment)
    elapsed_eval = logged(["lstmeval", "--model", str(checkpoint), "--traineddata", str(starter),
        "--eval_listfile", str(prepared / "development.list")], output / "development.log", environment)
    counters = re.findall(r"At iteration (\d+)/(\d+)/(\d+)", (output / "training.log").read_text())
    report = {"schema_version": 1, "run": args.run, "command": command,
        "preparation_sha256": sha256(prepared / "preparation.json"), "continue_from_sha256": sha256(source),
        "training_seconds": elapsed, "development_seconds": elapsed_eval,
        "checkpoint_sha256": sha256(checkpoint), "iterations_limit": args.iterations,
        "last_reported_counters": dict(zip(("learning", "training", "sample"), map(int, counters[-1]))) if counters else None,
        "target_error_rate": args.target_error_rate,
        "evaluation_used": False, "inference_eng_sha256": sha256(args.inference_eng), "status": "candidate only; no production replacement",
        "models": {mode: {"path": str(output / mode / "rus_gost.traineddata"),
            "sha256": sha256(output / mode / "rus_gost.traineddata"),
            "bytes": (output / mode / "rus_gost.traineddata").stat().st_size} for mode in ("float", "int")}}
    (output / "run.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report, indent=2))

if __name__ == "__main__":
    main()
