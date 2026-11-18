#!/usr/bin/env python3
"""Predeclare an optional training-only alphabet supplement; never inspect OCR results."""

import hashlib
import json
from pathlib import Path

from prepare_manifest import FONT, FONT_SHA256, ROOT

SEED = "gost-type-a-alphabet-v2-2026-11-18"
OUTPUT = ROOT / "docs/test-samples/gost-type-a/corpus-plan/alphabet-supplement-v2.json"
ALPHABET = "АБВГДЕЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ"  # Ё is taught in words, not unsupported designation grammar.
WORDS = ["ЕЖЕВИКА", "ЁМКОСТЬ", "ЗАЖИМ", "ЙОД", "ОБЪЁМ", "СЪЁМНИК", "РЫЧАГ", "ФЛАНЕЦ",
         "ЩЁТКА", "ЦАПФА", "ШТЫРЬ", "ЭКРАН", "ЮСТИРОВКА", "ЯКОРЬ", "ДЕТАЛЬ", "ПРОФИЛЬ"]


def integer(index: int, key: str, limit: int) -> int:
    return int.from_bytes(hashlib.sha256(f"{SEED}/{index}/{key}".encode()).digest()[:8], "big") % limit


def main() -> None:
    base_path = ROOT / "docs/test-samples/gost-type-a/corpus-plan/manifest.json"
    base = json.loads(base_path.read_text())
    historical = json.loads((ROOT / "tools/OcrComparison/input-cases.json").read_text())
    blocked = {c["Expected"]["Designation"].split(".")[0] for c in base["Cases"]}
    blocked.update(c["Expected"].split(".")[0] for c in historical["Cases"] if c["Role"] == "Designation")
    forbidden_truths = {line for c in base["Cases"] for role in ("Designation", "Name")
                        for line in c["Printed"][role].splitlines()}
    forbidden_truths.update(c["Expected"] for c in historical["Cases"])
    lines = []
    for index in range(128):
        # The first position cycles evenly through all 32 grammar-supported Cyrillic capitals.
        attempt = 0
        while True:
            prefix = ALPHABET[index % len(ALPHABET)] + "".join(
                ALPHABET[integer(index, f"prefix-{attempt}-{position}", len(ALPHABET))] for position in range(3))
            if prefix not in blocked:
                blocked.add(prefix)
                break
            attempt += 1
        classifier = integer(index, "classifier", 900000) + 100000
        serial = integer(index, "serial", 1000)
        execution = f"-{integer(index, 'execution', 99) + 1:02d}" if index % 2 else ""
        suffix = ["", "СБ", "Э3", "ПЭ3"][index % 4]
        text = f"{prefix}.{classifier:06d}.{serial:03d}{execution}{suffix}"
        lines.append({"Id": f"alphabet-designation-{index + 1:03d}", "Role": "Designation", "Text": text})
    for index in range(32):
        text = f"{WORDS[index % len(WORDS)]} {integer(index, 'word-number', 9000) + 1000}"
        lines.append({"Id": f"alphabet-name-{index + 1:03d}", "Role": "Name", "Text": text})
    if len({line["Text"] for line in lines}) != len(lines):
        raise ValueError("Duplicate supplement truth.")
    if forbidden_truths & {line["Text"] for line in lines}:
        raise ValueError("Supplement overlaps frozen primary or regression truth.")
    coverage = set("".join(line["Text"] for line in lines))
    required = "АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ0123456789.-"
    if set(required) - coverage:
        raise ValueError("Missing alphabet characters: " + "".join(sorted(set(required) - coverage)))
    result = {
        "SchemaVersion": 1, "CorpusId": SEED, "Split": "train-supplement",
        "Purpose": "Optional alphabet coverage declared before held-out evaluation; not a replacement for frozen v1.",
        "BaseManifestSha256": hashlib.sha256(base_path.read_bytes()).hexdigest(),
        "FontFile": FONT, "FontSha256": FONT_SHA256,
        "FontHeldOut": False, "Count": len(lines), "UniqueTexts": len(lines),
        "RequiredCharacters": required, "MissingRequiredCharacters": [],
        "PrimaryAndRegressionTruthOverlap": [], "PrimaryAndRegressionDesignationPrefixOverlap": [],
        "Notes": ["Ё is present in ordinary words; designation grammar does not currently accept Ё.",
                  "No OCR predictions, confidence scores or evaluation result files are read by this generator.",
                  "If used, this supplement belongs only to training; development and evaluation stay unchanged."],
        "Profiles": [{"Dpi": 300, "SkewDegrees": 0, "DesignationCapHeightMm": 5, "NameCapHeightMm": 3.5}],
        "Lines": lines,
    }
    payload = (json.dumps(result, ensure_ascii=False, indent=2) + "\n").encode()
    if OUTPUT.exists() and OUTPUT.read_bytes() != payload:
        raise ValueError("Frozen supplement differs; create a new explicit version.")
    OUTPUT.write_bytes(payload)
    print(f"{OUTPUT.relative_to(ROOT)}: {len(lines)} distinct truths; SHA256 {hashlib.sha256(payload).hexdigest()}")


if __name__ == "__main__":
    main()
