#!/usr/bin/env python3
"""Render the frozen pilot as line PNG + UTF-8 gt.txt pairs, never reading OCR output."""

import argparse
import hashlib
import json
import platform
import subprocess
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFont, features

ROOT = Path(__file__).resolve().parents[2]
MANIFEST = ROOT / "docs/test-samples/gost-type-a/corpus-plan/manifest.json"
FROZEN_SHA256 = "74470e3b885cfd5380eb3454168ff82cdf1bf749ce176df4d779c73339f463b4"
PADDING = 16
PROFILES = {
    "train": [(200, 0), (300, 0), (200, -0.4), (300, 0.4)],
    "development": [(225, 0), (325, 0)],
    "evaluation": [(250, 0), (350, 0)],
}


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def sources(manifest: dict, split: str) -> list[dict]:
    records = []
    for case in manifest["Cases"]:
        if case["Split"] != split:
            continue
        for role in ("Designation", "Name"):
            for index, line in enumerate(case["Printed"][role].splitlines()):
                if not line.strip() or line != line.strip():
                    raise ValueError("Unexpected blank/padded source line.")
                records.append({"CaseId": case["Id"], "Role": role, "LineIndex": index,
                                "Text": line, "FullFieldExpected": case["Expected"][role]})
    return records


def pillow_render(text: str, font_path: Path, cap_mm: float, dpi: int) -> Image.Image:
    reference = ImageFont.truetype(str(font_path), 4096)
    box = reference.getbbox("Н")
    size = max(1, round(cap_mm * dpi / 25.4 * 4096 / (box[3] - box[1])))
    font = ImageFont.truetype(str(font_path), size)
    box = font.getbbox(text)
    image = Image.new("L", (box[2] - box[0] + 2 * PADDING, box[3] - box[1] + 2 * PADDING), 255)
    ImageDraw.Draw(image).text((PADDING - box[0], PADDING - box[1]), text, font=font, fill=0)
    return image


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/gost-corpus")
    parser.add_argument("--split", choices=PROFILES, required=True)
    parser.add_argument("--skia-dll", type=Path,
                        default=ROOT / "artifacts/bin/GostCorpus/release/DocumentTextRecognition.GostCorpus.dll")
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    if sha(MANIFEST) != FROZEN_SHA256:
        raise ValueError("Frozen corpus manifest changed; create a new version instead of silently relabeling.")
    manifest = json.loads(MANIFEST.read_text())
    font_path = ROOT / manifest["FontFile"]
    if sha(font_path) != manifest["FontSha256"]:
        raise ValueError("Pinned font changed.")
    all_sources = {split: sources(manifest, split) for split in PROFILES}
    for left, right in (("train", "development"), ("train", "evaluation"), ("development", "evaluation")):
        if {r["Text"] for r in all_sources[left]} & {r["Text"] for r in all_sources[right]}:
            raise ValueError(f"Line truth crosses {left}/{right} split.")
    historical = json.loads((ROOT / "tools/OcrComparison/input-cases.json").read_text())
    historical_lines = {line for c in historical["Cases"] for line in c.get("ExpectedRaw", c["Expected"]).splitlines()}
    if historical_lines & {r["Text"] for r in all_sources["train"]}:
        raise ValueError("A historical regression truth leaked into training.")

    output = args.output.resolve() / args.split
    if output.exists() and any(output.iterdir()):
        raise ValueError(f"Output split must be empty: {output}")
    output.mkdir(parents=True, exist_ok=True)
    records = []
    requests = []
    for source in all_sources[args.split]:
        for variant, (dpi, skew) in enumerate(PROFILES[args.split]):
            identifier = f"{source['CaseId']}-{source['Role'].lower()}-{source['LineIndex'] + 1:02d}-v{variant + 1}"
            cap_mm = 5 if source["Role"] == "Designation" else 3.5
            file = output / f"{identifier}.png"
            records.append({"Id": identifier, **source, "Split": args.split, "File": file.name,
                            "GroundTruthFile": f"{identifier}.gt.txt", "Dpi": dpi,
                            "CapHeightMm": cap_mm, "SkewDegrees": skew, "PaddingPixels": PADDING})
            requests.append({"Id": identifier, "Text": source["Text"], "File": str(file),
                             "Dpi": dpi, "CapHeightMm": cap_mm, "Padding": PADDING})
    if args.split != "evaluation":
        request_path = output / "render-request.json"
        request_path.write_text(json.dumps({"FontFile": str(font_path), "FontSha256": manifest["FontSha256"],
                                           "Lines": requests}, ensure_ascii=False, indent=2) + "\n")
        subprocess.run([args.dotnet, str(args.skia_dll.resolve()), str(request_path)], check=True)
    for record in records:
        file = output / record["File"]
        if args.split == "evaluation":
            image = pillow_render(record["Text"], font_path, record["CapHeightMm"], record["Dpi"])
        else:
            with Image.open(file) as source_image:
                image = source_image.convert("L")
        if record["SkewDegrees"]:
            image = image.rotate(record["SkewDegrees"], resample=Image.Resampling.BICUBIC, expand=True, fillcolor=255)
        ink = ImageChops.invert(image).getbbox()
        if ink is None or min(ink[0], ink[1], image.width - ink[2], image.height - ink[3]) < 8:
            raise ValueError(f"Missing or clipped glyphs: {record['Id']}")
        image.save(file, dpi=(record["Dpi"], record["Dpi"]))
        truth = output / record["GroundTruthFile"]
        truth.write_text(record["Text"] + "\n", encoding="utf-8")
        record.update({"Width": image.width, "Height": image.height, "InkBounds": list(ink),
                       "CropSha256": sha(file), "GroundTruthSha256": sha(truth)})
        image.close()
    report = {
        "SchemaVersion": 1, "Manifest": str(MANIFEST.relative_to(ROOT)), "ManifestSha256": FROZEN_SHA256,
        "Font": manifest["FontFile"], "FontSha256": manifest["FontSha256"], "Split": args.split,
        "Renderer": "Pillow/FreeType" if args.split == "evaluation" else "SkiaSharp 2.88.9",
        "Postprocess": "Pillow grayscale/PNG encoding and explicit skew only",
        "PillowVersion": Image.__version__, "FreeTypeVersion": features.version("freetype2"),
        "Host": platform.platform(), "Count": len(records),
        "UniqueTexts": len({r["Text"] for r in records}),
        "ExcludedRoles": {"Type": "Standard type labels repeat across splits; not part of independent line pilot."},
        "CrossSplitTruthOverlap": [], "HistoricalTrainingTruthOverlap": [], "Lines": records,
    }
    (output / "lines.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n")
    print(f"{args.split}: {len(records)} PNG/gt pairs, {report['UniqueTexts']} unique strings; {output}")


if __name__ == "__main__":
    main()
