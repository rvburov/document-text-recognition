# Isolated GOST OCR model comparison

This tool reads the **unchanged** 19 recorded GOST crops in `input-cases.json`.
There are 13 unique PNG hashes; the six duplicated images remain explicitly marked.
These public regression examples are development evidence, not an unseen evaluation set.
The expected strings come from the fixture declarations and are consulted only after
inference. Input hashes are verified before each read. Original PDFs and product models
are never modified by these tools.

The comparison records original text, line and character confidence, shared field parsing,
exact expected-text equality, cold and warmed times, and process peak RSS. Scores from
different engines are not calibrated against one another. `General` and the product's
`CyrillicDesignation` profile are measured separately. Known-line results are **unconfirmed
candidates**: the product must retain its parser, independent-engine confirmation and
detector fallback.

## C# control and candidate

The harness reuses the production detector, preprocessing, CTC decoder and normalization.
Only the candidate recognition model changes. The source archive is official PaddleOCR:

https://paddle-model-ecology.bj.bcebos.com/paddlex/official_inference_model/paddle3.0.0/cyrillic_PP-OCRv5_mobile_rec_infer.tar

Extract it under `artifacts/ocr-candidates/models/`. Do not overwrite the installed
`eslav_PP_OCRv5_mobile_rec`. The candidate's **own** `inference.yml` contains its vocabulary;
reusing the eslav dictionary would decode incorrect characters.

```sh
dotnet build tools/OcrComparison -c Release
dotnet artifacts/bin/OcrComparison/release/DocumentTextRecognition.Performance.dll \
  --engine cyrillic-native --profile general --repeats 3
dotnet artifacts/bin/OcrComparison/release/DocumentTextRecognition.Performance.dll \
  --engine cyrillic-native --profile field --repeats 3
dotnet artifacts/bin/OcrComparison/release/DocumentTextRecognition.Performance.dll \
  --engine eslav-native --profile field --repeats 3
dotnet artifacts/bin/OcrComparison/release/DocumentTextRecognition.Performance.dll \
  --engine eslav-onnx --profile field --repeats 3
```

Every invocation runs one engine in a separate process. Native dependencies require the
same supported environment as Core.Tests (tested here in the Ubuntu 24 container).
`--cases`, `--candidate-rec`, `--candidate-onnx`, `--root`, `--output` override defaults.
Paths are repo-relative unless absolute. `--known-lines true` exercises the direct-line
candidate before confirmation/fallback. `--engine tesseract` runs the unchanged current
Tesseract language data and profile as another control.

`--engine gost-specialist` instead performs one bounded diagnostic pass using the **actual
product** GOST Tesseract (`rawDesignation:true`, `restrictDesignationAlphabet:false`) and
Cyrillic Paddle pair through `DocumentReader.ReadSeparateFields`. It records the raw-line
geometry gate, both raw readings, parsing, per-field agreement, model SHA and the real
Paddle detector fallback. `--known-lines` defaults to `true` for this mode, matching the
product. It does not repeat reads for timing or report crop agreement as document accuracy.

```sh
dotnet artifacts/bin/OcrComparison/release/DocumentTextRecognition.Performance.dll \
  --engine gost-specialist --output artifacts/ocr-candidates/gost-specialist.json
```

## Candidate ONNX conversion and differential

Use the pinned conversion packages from `../OnnxPerformance/requirements.txt`.
`convert_recognizer.py` uses exactly the existing FP32 conversion recipe: opset 14,
IR 8, no quantization, no optional optimizer, ONNX checker enabled. The exported
manifest records the official source URL, all three source SHA256 hashes, output SHA256,
package versions and vocabulary requirement. The shipping detector is unchanged.

```sh
python3 tools/OcrComparison/convert_recognizer.py \
  artifacts/ocr-candidates/models/cyrillic_PP-OCRv5_mobile_rec_infer \
  artifacts/ocr-candidates/cyrillic-onnx
dotnet artifacts/bin/OcrComparison/release/DocumentTextRecognition.Performance.dll \
  --engine cyrillic-onnx --profile field --known-lines false \
  --output artifacts/ocr-candidates/cyrillic-onnx-detect.json
dotnet artifacts/bin/OcrComparison/release/DocumentTextRecognition.Performance.dll \
  --compare-tensors true --output artifacts/ocr-candidates/cyrillic-tensor-comparison.json
```

The tensor comparison reads identical detected/known line crops, uses the same padded
NCHW input for both backends, and checks **every unmasked post-softmax probability**, not
only selected characters. It also compares General and Cyrillic CTC text. The numeric
FP32 differential tolerance is not an OCR acceptance threshold. This probe does not
measure throughput while loading both backends in one process.

## Independent EasyOCR experiment

Tested Python 3.12, CPU-only torch 2.5.1+cpu, torchvision 0.20.1+cpu and EasyOCR 1.7.2.
The script uses FP32 (`quantize=False`), one compute thread, greedy decode, no word model,
and no alphabet restriction. Its local Apache-licensed prediction adapter deliberately
keeps the **original full softmax** rather than EasyOCR's default ignore-class masking
and renormalization. This prevents an artificial increase in confidence from an allowlist.
The aggregate score otherwise uses EasyOCR's own `custom_mean`.

```sh
python3 -m pip install --index-url https://download.pytorch.org/whl/cpu \
  torch==2.5.1+cpu torchvision==0.20.1+cpu
python3 -m pip install easyocr==1.7.2
python3 tools/OcrComparison/easyocr_probe.py --repeats 3
dotnet artifacts/bin/OcrComparison/release/DocumentTextRecognition.Performance.dll \
  --normalize-external artifacts/ocr-candidates/easyocr-raw.json \
  --output artifacts/ocr-candidates/easyocr-normalized.json
```

Official EasyOCR weights are `craft_mlt_25k.pth` (release `pre-v1.1.6`) and
`cyrillic_g2.pth` (release `v1.6.1`). The script records their SHA256; EasyOCR also validates
its published MD5 before loading. Python, Torch and EasyOCR remain experimental
dependencies and are not included in the product. The adapted prediction loop's license
is `LICENSE.EasyOCR` (Apache 2.0), copied from the installed official package.

## Observed development results, 2026-11-18

| Engine, full detector | Exact normalized fields | Exact designations | Warm median / field | Peak process RSS |
| --- | ---: | ---: | ---: | ---: |
| Current eslav, native | 11/19 | 0/8 | 74.5 ms | 513 MiB |
| Current eslav, ONNX | 11/19 | 0/8 | 50.9 ms | 203 MiB |
| Candidate cyrillic, native | 19/19 | 8/8 | 76.6 ms | 508 MiB |
| Candidate cyrillic, ONNX | 19/19 | 8/8 | 52.4 ms | 203 MiB |
| EasyOCR, raw FP32 | 0/19 | 0/8 | 1692.4 ms | 925 MiB |

These are small Linux crop measurements, **not** a Windows 8000-document throughput claim.
The candidate native/ONNX differential preserved text, field geometry, parsing and
known-line flags across both modes; 611 selected character probabilities differed by at
most `7.6e-6`. Direct known-line mode alone read 12/19 correctly: seven designations lost
punctuation, failed the existing format parser, and need full detector fallback.
The full detector read all 19 correctly.

The separate full-tensor comparison also passed: 1,151,052 unmasked output probabilities,
maximum absolute difference `8.106231689453125e-6`, and 112 identical CTC decode pairs
across General/Cyrillic profiles. Compact raw readings, hashes, conversion provenance and
measurements are preserved in `comparison-summary.json`; detailed reports remain under
`artifacts/ocr-candidates/`.

The current independent Tesseract still rejects this GOST designation, so the improved
Paddle result alone does **not** establish successful two-engine document recognition.
Candidate promotion requires independent confirmation and the separate unseen/broad
regression corpus. Do not turn these 19 examples into training labels and then describe
their score as generalization.
