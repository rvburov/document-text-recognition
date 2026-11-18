// SPDX-License-Identifier: Apache-2.0
// Preprocessing derives from PaddleSharp 3.3.1 (Copyright sdcb), pinned source:
// https://github.com/sdcb/PaddleSharp/tree/139bc184a6d86c0c60bb8b8a90fb641b21c0b0e6/src/Sdcb.PaddleOCR
// Full license: ../../licenses/PaddleSharp-Apache-2.0.txt.
// Diagnostic only: compare every unmasked probability, not just the decoded argmax.
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DocumentTextRecognition.Core.Ocr;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR.Models;

internal static class TensorComparison
{
    internal static object Run(string root, Case[] cases, string modelDirectory, string onnxFile)
    {
        var model = RecognizationModel.FromDirectoryV5(modelDirectory);
        var device = PaddleDevice.OneDnn(cacheCapacity: 10, cpuMathThreadCount: 1);
        using var config = model.CreateConfig();
        model.ConfigureDevice(config, device);
        using var native = config.CreatePredictor();
        using var options = OnnxRuntimeSetup.CreateSessionOptions();
        using var onnx = new InferenceSession(onnxFile, options);
        using var detector = new WorkerPaddleDetector(DetectionModel.FromDirectory(
            Path.Combine(OcrComponents.PaddleDir, OcrComponents.PaddleDetector), ModelVersion.V5), device)
            { UnclipRatio = PaddleEngine.UnclipRatio };
        var results = new List<object>();
        long valuesCompared = 0;
        double largestDelta = 0;
        foreach (var item in cases)
        {
            var path = Path.Combine(root, item.File);
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            if (!hash.Equals(item.CropSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(item.Id);
            using var gray = Cv2.ImRead(path, ImreadModes.Grayscale);
            using var bgr = gray.CvtColor(ColorConversionCodes.GRAY2BGR);
            foreach (var known in new[] { false, true })
            {
                Rect[] boxes;
                if (known && KnownSingleLineRegion.TryGetBounds(gray, out var bounds)) boxes = [bounds];
                else boxes = detector.Run(bgr).Select(r => r.BoundingRect()).ToArray();
                var crops = new List<Mat>();
                try
                {
                    foreach (var box in boxes)
                        crops.Add(new Mat(bgr, Rect.FromLTRB(Math.Clamp(box.Left, 0, bgr.Width), Math.Clamp(box.Top, 0, bgr.Height),
                            Math.Clamp(box.Right, 0, bgr.Width), Math.Clamp(box.Bottom, 0, bgr.Height))));
                    foreach (var batch in crops.OrderBy(c => c.Width).Chunk(Math.Min(8, Environment.ProcessorCount)))
                    {
                        var (inputData, inputShape) = Prepare(batch, model.Shape.Height);
                        using (var input = native.GetInputTensor(native.InputNames[0]))
                        {
                            input.Shape = inputShape;
                            input.SetData(inputData);
                        }
                        if (!native.Run()) throw new InvalidOperationException("Native predictor failed.");
                        using var nativeOutput = native.GetOutputTensor(native.OutputNames[0]);
                        var nativeValues = nativeOutput.GetData<float>();
                        var nativeShape = nativeOutput.Shape;
                        using var ortOutputs = onnx.Run([NamedOnnxValue.CreateFromTensor(onnx.InputMetadata.Keys.First(),
                            new DenseTensor<float>(inputData, inputShape))]);
                        var ortTensor = ortOutputs.First().AsTensor<float>();
                        var ortValues = ortTensor.ToArray();
                        if (!nativeShape.SequenceEqual(ortTensor.Dimensions.ToArray())) throw new InvalidOperationException("Tensor shapes differ.");
                        double maximum = 0, sum = 0;
                        for (var i = 0; i < nativeValues.Length; i++)
                        {
                            if (!float.IsFinite(nativeValues[i]) || !float.IsFinite(ortValues[i])) throw new InvalidDataException("Nonfinite model output.");
                            var difference = Math.Abs((double)nativeValues[i] - ortValues[i]);
                            maximum = Math.Max(maximum, difference);
                            sum += difference;
                        }
                        var decoding = new List<object>();
                        var rowLength = nativeShape[1] * nativeShape[2];
                        for (var row = 0; row < nativeShape[0]; row++)
                            foreach (var profile in new[] { OcrTextProfile.General, OcrTextProfile.CyrillicDesignation })
                            {
                                var a = PaddleCtcDecoder.Decode(nativeValues.AsSpan(row * rowLength, rowLength), nativeShape[1], nativeShape[2], model.GetLabelByIndex, profile);
                                var b = PaddleCtcDecoder.Decode(ortValues.AsSpan(row * rowLength, rowLength), nativeShape[1], nativeShape[2], model.GetLabelByIndex, profile);
                                if (a.Text != b.Text) throw new InvalidOperationException($"CTC text differs for {item.Id}/{known}/{profile}");
                                decoding.Add(new { Row = row, Profile = profile.ToString(), Text = a.Text, NativeScore = a.Score, OnnxScore = b.Score });
                            }
                        valuesCompared += nativeValues.Length;
                        largestDelta = Math.Max(largestDelta, maximum);
                        results.Add(new { item.Id, KnownLineRequested = known, PixelSha256 = hash, InputShape = inputShape,
                            InputSha256 = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(inputData.AsSpan()))),
                            OutputShape = nativeShape, ValuesCompared = nativeValues.Length, MaximumAbsoluteDifference = maximum,
                            MeanAbsoluteDifference = sum / nativeValues.Length, IdenticalCtcText = true, Decoding = decoding });
                    }
                }
                finally { foreach (var crop in crops) crop.Dispose(); }
            }
        }
        // Numeric tolerance is only an FP32 backend comparison, never an OCR acceptance threshold.
        if (largestDelta > 1e-4) throw new InvalidOperationException($"Unexpected FP32 output delta: {largestDelta}");
        return new { Scope = "Full unmasked post-softmax tensors, identical NCHW input, native Paddle vs bundled ORT1.11.1; no quantization",
            SourceModelDirectory = modelDirectory, OnnxFile = onnxFile, ValuesCompared = valuesCompared,
            MaximumAbsoluteDifference = largestDelta, Results = results };
    }

    private static (float[] Data, int[] Shape) Prepare(Mat[] images, int height)
    {
        var width = (int)Math.Ceiling(images.Max(image => 1.0 * image.Width / image.Height * height));
        var data = new float[images.Length * 3 * height * width];
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            for (var i = 0; i < images.Length; i++)
            {
                var resizedWidth = (int)Math.Ceiling(height * (1.0f * images[i].Width / images[i].Height));
                using var resized = images[i].Resize(new Size(resizedWidth, height));
                using var padded = resizedWidth == width ? resized.Clone() : resized.CopyMakeBorder(0, 0, 0, width - resizedWidth, BorderTypes.Constant, Scalar.Gray);
                using var normalized = new Mat();
                padded.ConvertTo(normalized, MatType.CV_32FC3, 1.0 / 255);
                var channels = normalized.Split();
                try
                {
                    for (var c = 0; c < 3; c++)
                    {
                        channels[c].ConvertTo(channels[c], MatType.CV_32FC1, 2, -1);
                        using var plane = Mat.FromPixelData(height, width, MatType.CV_32FC1,
                            handle.AddrOfPinnedObject() + (i * 3 + c) * height * width * sizeof(float));
                        channels[c].CopyTo(plane);
                    }
                }
                finally { foreach (var channel in channels) channel.Dispose(); }
            }
        }
        finally { handle.Free(); }
        return (data, [images.Length, 3, height, width]);
    }
}
