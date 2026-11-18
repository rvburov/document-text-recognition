#!/usr/bin/env python3
"""Freeze new mixed-rasterizer truths before model selection; no OCR inputs are read."""

import hashlib
import json
from collections import Counter
from pathlib import Path

from prepare_manifest import FONT, FONT_SHA256, ROOT

SEED = "gost-type-a-mixed-renderers-v3-2026-11-18"
OUTPUT = ROOT / "docs/test-samples/gost-type-a/corpus-v3"
ALPHABET = "АБВГДЕЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ"
SPLITS = {
    "train": (300, ["Кожух", "Шифратор", "Муфта", "Щуп", "Эжектор", "Юстировщик", "Ёмкость", "Объёмный датчик",
                    "Держатель", "Вентиль", "Распределитель", "Кронштейн"],
              ["Wurth", "Samtec", "Omron", "Nexperia", "Rohm", "Fischer"]),
    "development": (60, ["Передатчик", "Защёлка", "Счётчик", "Корректор", "Демпфер", "Термостат"],
                    ["Molex", "Hirose", "Phoenix", "Littelfuse"]),
    "evaluation": (30, ["Нагреватель", "Оптопара", "Пружина", "Захват", "Излучатель", "Балансир"],
                   ["Analogix", "Vishay", "Infineon", "Broadcom"]),
}


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def integer(split: str, index: int, purpose: str, limit: int) -> int:
    return int.from_bytes(hashlib.sha256(f"{SEED}/{split}/{index}/{purpose}".encode()).digest()[:8], "big") % limit


def old_values() -> tuple[set[str], set[str], list[dict]]:
    files = ["docs/test-samples/gost-type-a/corpus-plan/manifest.json",
             "docs/test-samples/gost-type-a/corpus-plan/alphabet-supplement-v2.json",
             "tools/OcrComparison/input-cases.json", "docs/test-samples/verification.json"]
    truths, prefixes, provenance = set(), set(), []
    for file in files:
        raw = (ROOT / file).read_bytes()
        source = json.loads(raw)
        provenance.append({"File": file, "Sha256": digest(raw)})
        if "corpus-plan/manifest" in file:
            entries = [(role, c["Printed"][role]) for c in source["Cases"] for role in ("Designation", "Name")]
        elif "supplement" in file:
            entries = [(c["Role"], c["Text"]) for c in source["Lines"]]
        elif "input-cases" in file:
            entries = [(c["Role"], c.get("ExpectedRaw", c["Expected"])) for c in source["Cases"]]
        else:
            # Read only the predeclared expected filename, never ActualName/Fields/results.
            # Blocking every remaining contiguous phrase also excludes each printed name
            # without needing to infer where that name ends and the document type starts.
            entries = []
            for case in source["Samples"]:
                words = case["ExpectedName"].removesuffix(".pdf").split()
                entries.append(("Designation", words[0]))
                entries.extend(("Name", " ".join(words[start:end]))
                               for start in range(1, len(words)) for end in range(start + 1, len(words) + 1))
        for role, text in entries:
            truths.add(" ".join(text.split()))
            truths.update(text.splitlines())
            if role == "Designation":
                prefixes.add(text.split(".")[0])
    return truths, prefixes, provenance


def write_frozen(path: Path, value: dict) -> str:
    raw = (json.dumps(value, ensure_ascii=False, indent=2) + "\n").encode()
    if path.exists() and path.read_bytes() != raw:
        raise ValueError(f"Frozen file changed; create another explicit corpus version: {path}")
    path.write_bytes(raw)
    return digest(raw)


def main() -> None:
    truths, prefixes, exclusions = old_values()
    if digest((ROOT / FONT).read_bytes()) != FONT_SHA256:
        raise ValueError("Pinned font changed.")
    OUTPUT.mkdir(parents=True, exist_ok=True)
    reports = []
    all_new_truths = set()
    for split, (count, names, brands) in SPLITS.items():
        documents, images = [], []
        for index in range(count):
            attempt = 0
            while True:
                prefix = ALPHABET[index % len(ALPHABET)] + "".join(
                    ALPHABET[integer(split, index, f"prefix-{attempt}-{part}", len(ALPHABET))] for part in range(3))
                if prefix not in prefixes:
                    prefixes.add(prefix)
                    break
                attempt += 1
            classifier = integer(split, index, "classifier", 900000) + 100000
            serial = integer(split, index, "serial", 1000)
            execution = f"-{integer(split, index, 'execution', 99) + 1:02d}" if index % 2 else ""
            designation = f"{prefix}.{classifier:06d}.{serial:03d}{execution}{['', 'СБ', 'Э3', 'ПЭ3'][index % 4]}"
            stem = names[index % len(names)]
            if index % 4 == 0:
                stem = stem.upper()
            elif index % 4 == 1:
                stem += " " + brands[(index // 4) % len(brands)]
            if split == "train" and index % 29 == 0:
                stem = "ЁМКОСТЬ"  # predeclared uppercase coverage; no observed error string is used
            attempt = 0
            while True:
                name = f"{stem} {integer(split, index, f'name-number-{attempt}', 90000) + 10000}"
                if name not in truths and name not in all_new_truths:
                    break
                attempt += 1
            case_id = f"{split}-{index + 1:03d}"
            fields = {"Designation": designation, "Name": name}
            for value in fields.values():
                if value in truths or value in all_new_truths:
                    raise ValueError("GT overlap.")
                all_new_truths.add(value)
            documents.append({"Id": case_id, "Designation": designation, "Name": name, "Type": None})
            for role_index, (role, text) in enumerate(fields.items()):
                selector = index * 2 + role_index
                dpi = [250, 300, 325][selector % 3]
                cap = [3.5, 5][(selector // 3) % 2]
                angle = [0, -0.8, 0.8, 0][selector % 4]
                padding = [8, 16][(selector // 2) % 2]
                background = [255, 250, 245][(selector // 5) % 3]
                for renderer in ("Skia", "Pillow"):
                    images.append({"Id": f"{case_id}-{role.lower()}-{renderer.lower()}", "CaseId": case_id,
                                   "Split": split, "Role": role, "Text": text, "Renderer": renderer,
                                   "Dpi": dpi, "CapHeightMm": cap, "SkewDegrees": angle,
                                   "PaddingPixels": padding, "BackgroundGray": background,
                                   "Frame": False})
        alphabet = set("".join(image["Text"] for image in images))
        required = "АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ0123456789.-" if split == "train" else ""
        if set(required) - alphabet:
            raise ValueError("Training character coverage incomplete.")
        report = {
            "SchemaVersion": 1, "CorpusId": SEED, "Split": split, "FontFile": FONT, "FontSha256": FONT_SHA256,
            "FontHeldOut": False, "DocumentCount": count, "UniqueTexts": count * 2, "ImageCount": len(images),
            "RendererCounts": dict(Counter(image["Renderer"] for image in images)),
            "ExpectedNormalization": "Raw Unicode is preserved. Only join line whitespace for separate field-level reports.",
            "Purpose": "Training" if split == "train" else "Model selection only" if split == "development" else "Held-out final evaluation only; do not use for model selection",
            "NoTruthOrPrefixOverlapWithPriorCorpora": True,
            "BorderPolicy": "No frame in training line crops: pixel-based ink boxes must contain text only. Borders belong to separate full-PDF tests.",
            "RequiredTrainingCharacters": required, "MissingRequiredTrainingCharacters": [],
            "Documents": documents, "Images": images,
        }
        path = OUTPUT / f"{split}.json"
        checksum = write_frozen(path, report)
        reports.append({"Split": split, "File": path.name, "Sha256": checksum, "DocumentCount": count,
                        "UniqueTexts": count * 2, "ImageCount": len(images), "RendererCounts": report["RendererCounts"]})
    header = {"SchemaVersion": 1, "CorpusId": SEED, "Scope": "Fresh synthetic mixed-rasterizer corpus fixed before v3 training.",
              "FontFile": FONT, "FontSha256": FONT_SHA256, "FontHeldOut": False,
              "HistoricalEvaluation": "The 150 v1 evaluation images are historical evidence, not the blind evaluation of this corpus.",
              "OldValueExclusions": exclusions, "CrossSplitTruthOverlap": [], "CrossSplitDesignationPrefixOverlap": [],
              "Splits": reports}
    checksum = write_frozen(OUTPUT / "manifest.json", header)
    print(json.dumps({"Manifest": str((OUTPUT / 'manifest.json').relative_to(ROOT)), "Sha256": checksum,
                      "Counts": [{k: row[k] for k in ('Split', 'UniqueTexts', 'ImageCount', 'RendererCounts')} for row in reports]}, indent=2))


if __name__ == "__main__":
    main()
