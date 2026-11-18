#!/usr/bin/env python3
"""Render the separately frozen, optional training alphabet supplement using Skia."""

import argparse
import json
import platform
import subprocess
from pathlib import Path

from PIL import Image, ImageChops

from render_lines import PADDING, ROOT, sha

MANIFEST = ROOT / "docs/test-samples/gost-type-a/corpus-plan/alphabet-supplement-v2.json"
FROZEN_SHA256 = "5b4acbc2762e62058e5572a840ab1f99292267d73a9e79c817c9ec905882cd94"


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/gost-alphabet-v2/train")
    parser.add_argument("--skia-dll", type=Path,
                        default=ROOT / "artifacts/bin/GostCorpus/release/DocumentTextRecognition.GostCorpus.dll")
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    if sha(MANIFEST) != FROZEN_SHA256:
        raise ValueError("Frozen supplement changed.")
    manifest = json.loads(MANIFEST.read_text())
    output = args.output.resolve()
    if output.exists() and any(output.iterdir()):
        raise ValueError(f"Output must be empty: {output}")
    output.mkdir(parents=True, exist_ok=True)
    request = {"FontFile": str(ROOT / manifest["FontFile"]), "FontSha256": manifest["FontSha256"], "Lines": []}
    for line in manifest["Lines"]:
        request["Lines"].append({"Id": line["Id"], "Text": line["Text"], "File": str(output / f"{line['Id']}.png"),
                                 "Dpi": 300, "CapHeightMm": 5 if line["Role"] == "Designation" else 3.5,
                                 "Padding": PADDING})
    request_path = output / "render-request.json"
    request_path.write_text(json.dumps(request, ensure_ascii=False, indent=2) + "\n")
    subprocess.run([args.dotnet, str(args.skia_dll.resolve()), str(request_path)], check=True)
    records = []
    for line, rendering in zip(manifest["Lines"], request["Lines"], strict=True):
        file = Path(rendering["File"])
        with Image.open(file) as original:
            image = original.convert("L")
        ink = ImageChops.invert(image).getbbox()
        if ink is None or min(ink[0], ink[1], image.width - ink[2], image.height - ink[3]) < 8:
            raise ValueError("Missing or clipped source glyph: " + line["Id"])
        image.save(file, dpi=(300, 300))
        truth = output / f"{line['Id']}.gt.txt"
        truth.write_text(line["Text"] + "\n", encoding="utf-8")
        records.append({**line, "Split": "train", "Corpus": "alphabet-v2", "File": file.name,
                        "GroundTruthFile": truth.name, "Dpi": 300, "CapHeightMm": rendering["CapHeightMm"],
                        "PaddingPixels": PADDING, "Width": image.width, "Height": image.height,
                        "InkBounds": list(ink), "CropSha256": sha(file), "GroundTruthSha256": sha(truth)})
        image.close()
    summary = {"SchemaVersion": 1, "Manifest": str(MANIFEST.relative_to(ROOT)), "ManifestSha256": FROZEN_SHA256,
               "Font": manifest["FontFile"], "FontSha256": manifest["FontSha256"], "Split": "train",
               "Renderer": "SkiaSharp 2.88.9", "Postprocess": "Pillow grayscale/PNG encoding only",
               "PillowVersion": Image.__version__, "Host": platform.platform(), "Count": len(records),
               "UniqueTexts": len({line["Text"] for line in records}),
               "PrimaryAndRegressionTruthOverlap": [], "PrimaryAndRegressionDesignationPrefixOverlap": [],
               "MissingRequiredCharacters": [], "Lines": records}
    (output / "lines.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2) + "\n")
    print(f"Supplement: {len(records)} PNG/GT pairs, all distinct truths; {output}")


if __name__ == "__main__":
    main()
