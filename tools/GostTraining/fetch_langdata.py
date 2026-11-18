#!/usr/bin/env python3
"""Fetch pinned character properties needed to build a Cyrillic+Latin Tesseract recoder."""
import argparse
import hashlib
import json
from pathlib import Path
from urllib.request import urlopen

COMMIT = "07930fd9f246622c26eb5de794d9212ceac432d3"
FILES = {
    "Cyrillic.unicharset": "75804950557227223e142862d8006df66d61d248ceacf46d4669641516b26876",
    "Latin.unicharset": "2c5344876ec1edfc3187530949c76b93aab97c6031fa94a48bf85f40a3c7e128",
    "radical-stroke.txt": "9b441331169924e640233ae3abff9b4ac7019e33978a074c9d322f89afab1b89",
    "LICENSE": "cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30",
}

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    records = []
    for name in FILES:
        url = f"https://raw.githubusercontent.com/tesseract-ocr/langdata_lstm/{COMMIT}/{name}"
        target = args.output / name
        if not target.exists():
            with urlopen(url, timeout=60) as source:
                payload = source.read()
            target.write_bytes(payload)
        payload = target.read_bytes()
        if hashlib.sha256(payload).hexdigest() != FILES[name]:
            raise ValueError(f"Source checksum mismatch: {name}")
        records.append({"file": name, "url": url, "bytes": len(payload),
                        "sha256": hashlib.sha256(payload).hexdigest()})
    (args.output / "source.json").write_text(json.dumps({"repository": "tesseract-ocr/langdata_lstm",
        "commit": COMMIT, "files": records}, indent=2) + "\n")
    print(json.dumps(records, indent=2))

if __name__ == "__main__":
    main()
