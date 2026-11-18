# Isolated ONNX backend comparison

This tool tests the same PaddleOCR V5 FP32 models through ONNX Runtime. It does not change the production OCR backend. Verified converted graphs are bundled in `ocr/onnx` with SHA-256 provenance; conversion below reproduces them and is unnecessary for a normal build.

The first proof used Ubuntu 24.04, Python 3.12.3, `paddlepaddle==3.3.1`, `paddle2onnx==2.1.0`, `onnx==1.17.0`, .NET 10, managed ONNX Runtime 1.11.0 and **native ONNX Runtime 1.11.1 already bundled with Paddle 3.3.1.70**. Only the managed ONNX package is added, preventing a second native runtime from overwriting Paddle's DLLs. Paddle preprocessing, detector thresholds and geometry, recognition resizing, CTC decoding and confidence calculation are retained under the upstream Apache-2.0 license. Both backends use one CPU math thread.

## Convert on Linux

Build Core first to extract the existing models. Install Python dependencies in a virtual environment (or use an isolated disposable container):

```sh
dotnet build src/Core/Core.csproj -c Release
python3 -m venv artifacts/onnx-venv
artifacts/onnx-venv/bin/python -m pip install --no-cache-dir -r tools/OnnxPerformance/requirements.txt
artifacts/onnx-venv/bin/python tools/OnnxPerformance/convert_models.py artifacts/ocr-models/paddle-v5 artifacts/onnx-models
```

The converter explicitly disables optional graph optimization, exports opset14, checks the graph, writes IR8 format metadata and records SHA-256 hashes of input and output files in `conversion-manifest.json`. The same converted files must be used for the Linux and Windows comparisons. Python is only needed for conversion.

## Compare on Linux or Windows

```sh
dotnet build tools/OnnxPerformance/OnnxPerformance.csproj -c Release
dotnet artifacts/bin/OnnxPerformance/release/DocumentTextRecognition.Performance.dll artifacts/ocr-models/paddle-v5 docs/test-samples/crops artifacts/onnx-models artifacts/onnx-performance.json 5
```

On Windows, the same arguments work in PowerShell. The probe applies the production runtime thread settings and sets OpenCV to one thread. Keep `runtimes/win-x64` and the managed dependencies beside the executable; do not replace its native ONNX DLL. The final argument is the number of timed repetitions (default five).

The JSON contains all 11 tracked crop comparisons, then five document batches grouped by filename prefixes `01`–`05`. Each input warms both backends once; timed order alternates. Field checks compare text and exact detection boxes. Batch checks call production `PaddleEngine.ReadMany` and the isolated equivalent, preserving field identity, line grouping and parsing. Every repetition must match text, boxes and parsed outcomes, with confidence delta at most `1e-5` as a numerical comparison tolerance. Production confidence thresholds remain unchanged. The process exits nonzero when comparison fails.

The screenshot-like negative sample remains included: agreement with the reference is not proof of correct transcription. These are synthetic crops, not production PDFs. Timings exclude PDF loading, rendering, matching, Tesseract, filesystem operations and renaming; they cannot establish an 8000-document completion time. A Windows comparison and full-pipeline accuracy/throughput tests are required before any backend decision.
