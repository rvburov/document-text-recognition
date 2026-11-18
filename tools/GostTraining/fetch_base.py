#!/usr/bin/env python3
"""Fetch the pinned floating-point training inputs, never application tessdata."""
import argparse
import hashlib
import json
from pathlib import Path
from urllib.request import urlopen

COMMIT = "e12c65a915945e4c28e237a9b52bc4a8f39a0cec"
FILES = {
    "rus.traineddata": "b617eb6830ffabaaa795dd87ea7fd251adfe9cf0efe05eb9a2e8128b7728d6b6",
    "eng.traineddata": "8280aed0782fe27257a68ea10fe7ef324ca0f8d85bd2fd145d1c2b560bcb66ba",
    "LICENSE": "a6cba85bc92e0cff7a450b1d873c0eaa2e9fc96bf472df0247a26bec77bf3ff9",
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    application_data = Path(__file__).resolve().parents[2] / "ocr/tessdata"
    output = args.output.resolve()
    if output == application_data or application_data in output.parents:
        raise ValueError("Training sources must remain outside application tessdata")
    output.mkdir(parents=True, exist_ok=True)
    records = []
    for name, expected in FILES.items():
        url = f"https://raw.githubusercontent.com/tesseract-ocr/tessdata_best/{COMMIT}/{name}"
        target = output / name
        if target.exists():
            payload = target.read_bytes()
        else:
            with urlopen(url, timeout=60) as source:
                payload = source.read()
        checksum = hashlib.sha256(payload).hexdigest()
        if checksum != expected:
            raise ValueError(f"Pinned training source checksum mismatch: {name}")
        if not target.exists():
            target.write_bytes(payload)
        records.append({"file": name, "url": url, "bytes": len(payload), "sha256": checksum})
    receipt = output / "source.json"
    if not receipt.exists():
        receipt.write_text(json.dumps({"upstream": "https://github.com/tesseract-ocr/tessdata_best",
            "commit": COMMIT, "purpose": "Pinned floating-point inputs for separate GOST candidate training",
            "files": records}, indent=2)+"\n")
    print(json.dumps(records, indent=2))


if __name__ == "__main__":
    main()
