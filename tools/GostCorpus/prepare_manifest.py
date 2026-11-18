#!/usr/bin/env python3
"""Freeze pilot ground truth without rendering images or running an OCR engine."""

import argparse
import hashlib
import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / "docs/test-samples/gost-type-a/corpus-plan/manifest.json"
FONT = "tests/Core.Tests/Fonts/GOST2.304-81TypeA-Slanted.ttf"
FONT_SHA256 = "e83689021a988a15736a70799aa166345499de548664079c58435d54d7cd973e"
SEED = "gost-type-a-pilot-v1-2026-11-18"

# The values are authored before recognition; changing them creates another corpus version.
# All later renderings/augmentations of a truth stay in that truth's split.
SPLITS = {
    "train": {
        "count": 64,
        "prefixes": ["АБВГ", "ДЖЛМ", "КПРТ", "НОФХ", "ЦЧШЩ", "ЭЮЯС", "БГДЖ", "ЛРФЦ"],
        "names": ["Плата усилителя", "Блок питания USB", "Модуль Epcos", "Узел стабилизации",
                  "Датчик температуры", "Фильтр частоты", "Разъём UART", "Преобразователь PLL"],
    },
    "development": {
        "count": 16,
        "prefixes": ["МНПР", "СФЦЧ", "ЧШЭЮ", "ЖКЛФ"],
        "names": ["Контроллер SPI", "Разветвитель сигналов", "Адаптер Ethernet", "Ограничитель тока"],
    },
    "evaluation": {
        "count": 32,
        "prefixes": ["МЖЦФ", "ЯЖНФ", "ШЛРД", "ФЖЮК", "ЩЦЛГ", "ДЖЭФ", "КЛЩЯ", "ЮЖШФ"],
        "names": ["Приёмник радиоканала", "Коммутатор RS-485", "Синтезатор частоты", "Антенна GNSS",
                  "Измеритель напряжения", "Кабель DisplayPort", "Генератор импульсов", "Резонатор Murata"],
    },
}


def number(split: str, index: int, purpose: str, modulus: int) -> int:
    digest = hashlib.sha256(f"{SEED}/{split}/{index}/{purpose}".encode()).digest()
    return int.from_bytes(digest[:8], "big") % modulus


def build_manifest() -> dict:
    font_hash = hashlib.sha256((ROOT / FONT).read_bytes()).hexdigest()
    if font_hash != FONT_SHA256:
        raise ValueError("The pinned GOST font changed; do not silently change corpus glyphs.")
    cases = []
    for split, specification in SPLITS.items():
        for index in range(specification["count"]):
            prefix = specification["prefixes"][index % len(specification["prefixes"])]
            classifier = number(split, index, "classifier", 900000) + 100000
            serial = number(split, index, "serial", 1000)
            suffix = ["", "СБ", "Э3", "ПЭ3"][(index // 2) % 4]
            execution = f"-{number(split, index, 'execution', 99) + 1:02d}" if index % 2 else ""
            designation = f"{prefix}.{classifier:06d}.{serial:03d}{execution}{suffix}"
            name = specification["names"][index % len(specification["names"]) ]
            # A separate line provides line-order coverage without encoding the expected value in a filename.
            variant = number(split, index, "name-variant", 9000) + 1000
            printed_name = f"{name}\nИсполнение {variant}" if index % 3 == 0 else f"{name} {variant}"
            document_type = [None, "Сборочный чертёж", "Схема электрическая принципиальная"][index % 3]
            cases.append({
                "Id": f"{split}-{index + 1:03d}",
                "Split": split,
                "Layout": "specification-a4" if document_type is None else "drawing-a4",
                "Expected": {
                    "Designation": designation,
                    "Name": " ".join(printed_name.split()),
                    "Type": document_type,
                },
                "Printed": {"Designation": designation, "Name": printed_name, "Type": document_type},
                "ProfileGroup": split,
            })
    for field in ("Designation", "Name"):
        values = [case["Expected"][field] for case in cases]
        if len(values) != len(set(values)):
            raise ValueError(f"Duplicate {field}: exact strings must be disjoint even within a split.")
    prefixes = [prefix for spec in SPLITS.values() for prefix in spec["prefixes"]]
    if len(prefixes) != len(set(prefixes)):
        raise ValueError("Organization prefixes must not cross splits.")
    return {
        "SchemaVersion": 1,
        "CorpusId": SEED,
        "Status": "planned-ground-truth-only; images and recognition results not generated",
        "Purpose": "Small synthetic adaptation/evaluation pilot; not evidence of recognition accuracy.",
        "FontFile": FONT,
        "FontSha256": FONT_SHA256,
        "FontHeldOut": False,
        "ExpectedNormalization": "Join printed whitespace with one space; retain every letter, digit, punctuation mark and Unicode alphabet.",
        "Counts": {split: spec["count"] for split, spec in SPLITS.items()},
        "SplitUnit": "Entire document truth including every field and all future renderings/augmentations.",
        "Profiles": {
            "train": {
                "Rasterizer": "SkiaSharp with pinned GOST font",
                "Dpi": [200, 300],
                "DesignationCapHeightMm": 5,
                "NameCapHeightMm": 3.5,
                "DocumentTypeCapHeightMm": 2.8,
                "PlannedTrainingSkewDegrees": [0, -0.4, 0.4],
                "Generated": False,
            },
            "development": {
                "Rasterizer": "SkiaSharp with pinned GOST font",
                "Dpi": [225, 325],
                "PlannedSkewDegrees": [0, -0.8, 0.8],
                "Generated": False,
            },
            "evaluation": {
                "Rasterizers": ["Pillow/FreeType with pinned GOST font", "PDFium rendering embedded GOST vector PDF"],
                "Dpi": [250, 350],
                "SharedStandardCapHeightsMm": [5, 3.5, 2.8],
                "ExtraDirectCropStressCapHeightsMm": [2.5, 7],
                "CleanPrimary": True,
                "SeparateStressVariants": {"SkewDegrees": [-1.2, 1.2], "QuarterTurns": [1, 2, 3], "JpegQuality": 85},
                "Generated": False,
            },
        },
        "Cases": cases,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="Check frozen JSON byte-for-byte without rewriting it.")
    args = parser.parse_args()
    payload = (json.dumps(build_manifest(), ensure_ascii=False, indent=2) + "\n").encode()
    if args.check:
        if not OUTPUT.exists() or OUTPUT.read_bytes() != payload:
            raise SystemExit("Manifest differs from its deterministic source.")
    else:
        OUTPUT.parent.mkdir(parents=True, exist_ok=True)
        OUTPUT.write_bytes(payload)
    print(f"{OUTPUT.relative_to(ROOT)}: 112 truths, no images; SHA256 {hashlib.sha256(payload).hexdigest()}")


if __name__ == "__main__":
    main()
