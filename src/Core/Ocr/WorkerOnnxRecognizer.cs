// SPDX-License-Identifier: Apache-2.0
// Copyright (c) sdcb. Derived from PaddleSharp 3.3.1:
// https://github.com/sdcb/PaddleSharp/tree/139bc184a6d86c0c60bb8b8a90fb641b21c0b0e6/src/Sdcb.PaddleOCR
// Modified for ONNX Runtime: execute converted FP32 models with ONNX Runtime;
// retain Paddle preprocessing, detector geometry, CTC decoding, scores and thresholds.
// Trim unused API and dispose temporary resources on failure.
// Full license is distributed with the application (licenses/PaddleSharp-Apache-2.0.txt).
using Sdcb.PaddleOCR;
using OpenCvSharp;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Sdcb.PaddleOCR.Models;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;

namespace DocumentTextRecognition.Core.Ocr;

/// <summary>
/// ONNX recognizer. Resize, normalization and CTC decode match WorkerPaddleRecognizer.
/// </summary>
internal sealed class WorkerOnnxRecognizer : IDisposable
{
    private readonly InferenceSession _p;

    /// <summary>
    /// Recognization model being used for OCR.
    /// </summary>
    public RecognizationModel Model { get; init; }

    /// <summary>
    /// Constructor for creating a new instance of the <see cref="WorkerOnnxRecognizer"/> class using a specified model and its converted ONNX graph.
    /// </summary>
    /// <param name="model">The RecognizationModel object.</param>
    /// <param name="path">Converted FP32 ONNX model.</param>
    public WorkerOnnxRecognizer(RecognizationModel model, string path)
    {
        Model = model;
        using var options = OnnxRuntimeSetup.CreateSessionOptions();
        _p = new InferenceSession(path, options);
    }

    /// <summary>
    /// Releases all resources used by the current instance of the <see cref="WorkerOnnxRecognizer"/> class.
    /// </summary>
    public void Dispose() => _p.Dispose();

    /// <summary>
    /// Run OCR recognition on multiple images in batches.
    /// </summary>
    /// <param name="srcs">Array of images for OCR recognition.</param>
    /// <param name="batchSize">Size of the batch to run OCR recognition on.</param>
    /// <returns>Array of <see cref="PaddleOcrRecognizerResult"/> instances corresponding to OCR recognition results of the images.</returns>
    public PaddleOcrRecognizerResult[] Run(Mat[] srcs, int batchSize = 0, IReadOnlyList<OcrTextProfile>? profiles = null)
    {
        if (profiles is not null && profiles.Count != srcs.Length)
            throw new ArgumentException("Each OCR image needs its own text profile.", nameof(profiles));
        if (srcs.Length == 0)
        {
            return new PaddleOcrRecognizerResult[0];
        }

        int chooseBatchSize = batchSize != 0 ? batchSize : Math.Min(8, Environment.ProcessorCount);

        return srcs
            .Select((x, i) => (mat: x, i, profile: profiles?[i] ?? OcrTextProfile.General))
            .OrderBy(x => x.mat.Width)
            .Chunk(chooseBatchSize)
            .Select(x => (result: RunMulti(x.Select(x => x.mat).ToArray(), profiles is null ? null : x.Select(x => x.profile).ToArray()), ids: x.Select(x => x.i).ToArray()))
            .SelectMany(x => x.result.Zip(x.ids, (result, i) => (result, i)))
            .OrderBy(x => x.i)
            .Select(x => x.result)
            .ToArray();
    }

    private PaddleOcrRecognizerResult[] RunMulti(Mat[] srcs, IReadOnlyList<OcrTextProfile>? profiles)
    {
        if (srcs.Length == 0)
        {
            return new PaddleOcrRecognizerResult[0];
        }

        for (int i = 0; i < srcs.Length; ++i)
        {
            Mat src = srcs[i];
            if (src.Empty())
            {
                throw new ArgumentException($"src[{i}] size should not be 0, wrong input picture provided?");
            }
        }

        int modelHeight = Model.Shape.Height;
        int maxWidth = (int)Math.Ceiling(srcs.Max(src =>
        {
            Size size = src.Size();
            return 1.0 * size.Width / size.Height * modelHeight;
        }));

        InferenceSession predictor = _p;
        var normalizeds = new List<Mat>();
        float[] inputData;
        int[] inputShape;
        try
        {
            foreach (var src in srcs)
            {
                using Mat channel3 = src.Channels() switch
                {
                    4 => src.CvtColor(ColorConversionCodes.RGBA2BGR),
                    1 => src.CvtColor(ColorConversionCodes.GRAY2RGB),
                    3 => src.Clone(),
                    var x => throw new Exception($"Unexpect src channel: {x}, allow: (1/3/4)")
                };
                using Mat resized = ResizePadding(channel3, modelHeight, maxWidth);
                normalizeds.Add(Normalize(resized));
            }

            int channel = normalizeds[0].Channels();
            inputShape = new[] { normalizeds.Count, channel, modelHeight, maxWidth };
            inputData = ExtractMat(normalizeds.ToArray(), channel, modelHeight, maxWidth);
        }
        finally
        {
            foreach (Mat normalized in normalizeds)
            {
                normalized.Dispose();
            }
        }

        var input = NamedOnnxValue.CreateFromTensor(predictor.InputMetadata.Keys.First(), new DenseTensor<float>(inputData, inputShape));
        using (var results = predictor.Run([input]))
        {
            var output = results.First().AsTensor<float>();
            float[] data = output.ToArray();
            int[] shape = output.Dimensions.ToArray();

            var labelCount = shape[2];
            var characterCount = shape[1];
            return Enumerable.Range(0, shape[0]).Select(i => PaddleCtcDecoder.Decode(
                data.AsSpan(i * characterCount * labelCount, characterCount * labelCount),
                characterCount, labelCount, Model.GetLabelByIndex, profiles?[i] ?? OcrTextProfile.General)).ToArray();
        }
    }

    private static Mat ResizePadding(Mat src, int height, int targetWidth)
    {
        Size size = src.Size();
        float whRatio = 1.0f * size.Width / size.Height;
        int width = (int)Math.Ceiling(height * whRatio);

        if (width == targetWidth)
        {
            return src.Resize(new Size(width, height));
        }
        else
        {
            using Mat resized = src.Resize(new Size(width, height));
            return resized.CopyMakeBorder(0, 0, 0, targetWidth - width, BorderTypes.Constant, Scalar.Gray);
        }
    }

    private static Mat Normalize(Mat src)
    {
        using Mat normalized = new();
        src.ConvertTo(normalized, MatType.CV_32FC3, 1.0 / 255);
        Mat[] bgr = normalized.Split();
        float[] scales = new[] { 2.0f, 2.0f, 2.0f };
        float[] means = new[] { 0.5f, 0.5f, 0.5f };
        try
        {
            for (int i = 0; i < bgr.Length; ++i)
            {
                bgr[i].ConvertTo(bgr[i], MatType.CV_32FC1, 1.0 * scales[i], (0.0 - means[i]) * scales[i]);
            }
            Mat dest = new();
            try
            {
                Cv2.Merge(bgr, dest);
                return dest;
            }
            catch { dest.Dispose(); throw; }
        }
        finally { foreach (Mat channel in bgr) channel.Dispose(); }
    }

    private static float[] ExtractMat(Mat[] srcs, int channel, int height, int width)
    {
        float[] result = new float[srcs.Length * channel * width * height];
        GCHandle resultHandle = GCHandle.Alloc(result, GCHandleType.Pinned);
        IntPtr resultPtr = resultHandle.AddrOfPinnedObject();
        try
        {
            for (int i = 0; i < srcs.Length; ++i)
            {
                Mat src = srcs[i];
                if (src.Channels() != channel)
                {
                    throw new Exception($"src[{i}] channel={src.Channels()}, expected {channel}");
                }
                for (int c = 0; c < channel; ++c)
                {
                    using Mat dest = Mat.FromPixelData(height, width, MatType.CV_32FC1, resultPtr + (c + i * channel) * height * width * sizeof(float));
                    Cv2.ExtractChannel(src, dest, c);
                }
            }
            return result;
        }
        finally
        {
            resultHandle.Free();
        }
    }
}

