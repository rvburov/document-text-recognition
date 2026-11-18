# Converted PaddleOCR V5 models

`det.onnx` and `rec.onnx` are the original FP32 Paddle V5 detector/recognizer converted to ONNX opset14/IR8. They are included as local build assets; installed OCR does not download models and does not require Python.

`model-manifest.json` records the original NuGet package, input/output hashes, converter versions and the Windows differential run using these exact output bytes. Original Paddle metadata and character dictionaries remain in `ocr/paddle` in the built application. Model licensing remains Apache-2.0 (PaddlePaddle Authors); see the distributed third-party notices and license texts.

To reproduce the assets, extract models by building Core, install `tools/OnnxPerformance/requirements.txt` into an isolated Python3.12 environment, and run:

```sh
python tools/OnnxPerformance/convert_models.py artifacts/ocr-models/paddle-v5 artifacts/onnx-models
```

Compare output SHA-256 hashes with this manifest before replacing assets. A model update must update the manifest and Core's expected hashes together, then rerun native/ONNX differential tests. The model files are copied to build/publish outputs and used by default in version 1.3. The native backend remains available for controlled comparisons.

For a Windows published-folder check, including a Cyrillic installation path and the payload's own Core/native DLLs:

```powershell
./scripts/check-published-ocr.ps1 -PublishedDirectory ./artifacts/setup/app
```

The script needs the build machine's .NET SDK, but no Python. It does not modify the original publish folder and saves its reports under `artifacts/published-ocr-smoke`.
