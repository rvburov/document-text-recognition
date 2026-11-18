#!/usr/bin/env python3
"""Render explicit per-image Skia/Pillow choices from the frozen v3 manifest."""

import argparse
import hashlib
import json
import platform
import subprocess
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFont, features

from prepare_manifest import ROOT

MANIFEST = ROOT / "docs/test-samples/gost-type-a/corpus-v3/manifest.json"
FROZEN_SHA256 = "605a04017eb37c09ffc745506b4bf7ff8bb1943c696d6d72e58a42a282f6797b"


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def pillow_line(record: dict, font_path: Path) -> Image.Image:
    reference = ImageFont.truetype(str(font_path), 4096)
    cap = reference.getbbox("Н")
    size = max(1, round(record["CapHeightMm"] * record["Dpi"] / 25.4 * 4096 / (cap[3] - cap[1])))
    font = ImageFont.truetype(str(font_path), size)
    bounds = font.getbbox(record["Text"])
    padding = record["PaddingPixels"]
    image = Image.new("L", (bounds[2] - bounds[0] + 2 * padding, bounds[3] - bounds[1] + 2 * padding), 255)
    ImageDraw.Draw(image).text((padding - bounds[0], padding - bounds[1]), record["Text"], font=font, fill=0)
    return image


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--split", choices=("train", "development", "evaluation"), required=True)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--skia-dll", type=Path,
                        default=ROOT / "artifacts/bin/GostCorpus/release/DocumentTextRecognition.GostCorpus.dll")
    args = parser.parse_args()
    if sha(MANIFEST) != FROZEN_SHA256:
        raise ValueError("V3 manifest changed after freezing.")
    header = json.loads(MANIFEST.read_text())
    split_info = next(row for row in header["Splits"] if row["Split"] == args.split)
    source = MANIFEST.parent / split_info["File"]
    if sha(source) != split_info["Sha256"]:
        raise ValueError("V3 split truth/parameters changed after freezing.")
    plan = json.loads(source.read_text())
    font = ROOT / plan["FontFile"]
    if sha(font) != plan["FontSha256"]:
        raise ValueError("Pinned font changed.")
    root = args.output or ROOT / ("artifacts/gost-v3-holdout" if args.split == "evaluation" else "artifacts/gost-v3")
    output = root.resolve() / args.split
    if output.exists() and any(output.iterdir()):
        raise ValueError(f"Output split must be empty: {output}")
    output.mkdir(parents=True, exist_ok=True)
    request = {"FontFile": str(font), "FontSha256": plan["FontSha256"], "Lines": []}
    for record in plan["Images"]:
        if record["Renderer"] == "Skia":
            request["Lines"].append({"Id": record["Id"], "Text": record["Text"],
                                     "File": str(output / f"{record['Id']}.png"), "Dpi": record["Dpi"],
                                     "CapHeightMm": record["CapHeightMm"], "Padding": record["PaddingPixels"]})
    request_path = output / "render-request.json"
    request_path.write_text(json.dumps(request, ensure_ascii=False, indent=2) + "\n")
    subprocess.run([args.dotnet, str(args.skia_dll.resolve()), str(request_path)], check=True)
    records = []
    for parameters in plan["Images"]:
        record = dict(parameters)
        file = output / f"{record['Id']}.png"
        if record["Renderer"] == "Pillow":
            image = pillow_line(record, font)
        elif record["Renderer"] == "Skia":
            with Image.open(file) as original:
                image = original.convert("L")
        else:
            raise ValueError("Unknown explicit renderer.")
        # The same deterministic postprocessing is applied to both rasterizers of a truth.
        background = record["BackgroundGray"]
        image = image.point([round(value * background / 255) for value in range(256)])
        if record["SkewDegrees"]:
            image = image.rotate(record["SkewDegrees"], resample=Image.Resampling.BICUBIC,
                                 expand=True, fillcolor=background)
        ink = image.point([255 if value < 200 else 0 for value in range(256)]).getbbox()
        if ink is None or min(ink[0], ink[1], image.width - ink[2], image.height - ink[3]) < 4:
            raise ValueError("Missing or clipped ink: " + record["Id"])
        image.save(file, dpi=(record["Dpi"], record["Dpi"]))
        truth = output / f"{record['Id']}.gt.txt"
        truth.write_text(record["Text"] + "\n", encoding="utf-8")
        record.update({"File": file.name, "GroundTruthFile": truth.name, "Width": image.width, "Height": image.height,
                       "InkBoundsUnder200": list(ink), "CropSha256": sha(file), "GroundTruthSha256": sha(truth)})
        image.close()
        records.append(record)
    groups = {}
    for record in records:
        groups.setdefault((record["CaseId"], record["Role"]), {})[record["Renderer"]] = record["CropSha256"]
    identical = [f"{case}/{role}" for (case, role), hashes in groups.items() if hashes["Skia"] == hashes["Pillow"]]
    if len(identical) == len(groups):
        raise ValueError("Every renderer pair is byte-identical; investigate incorrect renderer dispatch.")
    report = {
        "SchemaVersion": 1, "ManifestSha256": FROZEN_SHA256, "SplitManifestSha256": split_info["Sha256"],
        "FontFile": plan["FontFile"], "FontSha256": plan["FontSha256"], "FontHeldOut": False,
        "Split": args.split, "Count": len(records), "UniqueTexts": len({r["Text"] for r in records}),
        "RendererCounts": {name: sum(r["Renderer"] == name for r in records) for name in ("Skia", "Pillow")},
        "DifferentRendererPngPairs": len(groups) - len(identical), "IdenticalRendererPngPairs": identical,
        "SkiaVersion": "2.88.9", "PillowVersion": Image.__version__, "FreeTypeVersion": features.version("freetype2"),
        "Host": platform.platform(), "Postprocess": "Shared grayscale background/skew, no frame/no OCR/no dictionary.",
        "Lines": records,
    }
    index = output / "lines.json"
    index.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n")
    print(json.dumps({"Split": args.split, "Count": len(records), "UniqueTexts": report["UniqueTexts"],
                      "DifferentRendererPngPairs": report["DifferentRendererPngPairs"],
                      "IdenticalRendererPngPairs": len(identical), "MetadataSha256": sha(index), "Output": str(output)}))


if __name__ == "__main__":
    main()
