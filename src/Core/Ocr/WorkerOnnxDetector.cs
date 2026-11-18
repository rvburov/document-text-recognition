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

namespace DocumentTextRecognition.Core.Ocr;

/// <summary>
/// ONNX detector. Preprocessing and geometry match WorkerPaddleDetector.
/// </summary>
internal sealed class WorkerOnnxDetector : IDisposable
{
    /// <summary>One sequential ONNX session for this document worker.</summary>
    readonly InferenceSession _p;

    /// <summary>Gets or sets the maximum size for resizing the input image.</summary>
    public int? MaxSize { get; set; } = 960;

    /// <summary>Gets or sets the score threshold for filtering out possible text boxes.</summary>
    public float? BoxScoreThreahold { get; set; } = 0.7f;

    /// <summary>Gets or sets the threshold to binarize the text region.</summary>
    public float? BoxThreshold { get; set; } = 0.3f;

    /// <summary>Gets or sets the minimum size of the text boxes to be considered as valid.</summary>
    public int MinSize { get; set; } = 3;

    /// <summary>Gets or sets the ratio for enlarging text boxes during post-processing.</summary>
    public float UnclipRatio { get; set; } = 1.5f;

    /// <summary>
    /// Initializes a new instance of the WorkerOnnxDetector class with the converted ONNX graph.
    /// </summary>
    /// <param name="path">Converted FP32 ONNX model.</param>
    public WorkerOnnxDetector(string path)
    {
        using var options = OnnxRuntimeSetup.CreateSessionOptions();
        _p = new InferenceSession(path, options);
    }

    /// <summary>
    /// Disposes the ONNX session.
    /// </summary>
    public void Dispose()
    {
        _p.Dispose();
    }

    /// <summary>
    /// Runs the text box detection process on the input image.
    /// </summary>
    /// <param name="src">Input image.</param>
    /// <returns>An array of detected rotated rectangles representing text boxes.</returns>
    /// <exception cref="ArgumentException">Thrown when input image is empty.</exception>
    /// <exception cref="NotSupportedException">Thrown when input image channels are not 3 or 1.</exception>
    /// <exception cref="Exception">Thrown when ONNX inference fails.</exception>
    public RotatedRect[] Run(Mat src)
    {
        (int height, int width, float[] data) = RunRawCore(src, out Size resizedSize);
        GCHandle dataGcHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            using Mat pred = Mat.FromPixelData(height, width, MatType.CV_32FC1, dataGcHandle.AddrOfPinnedObject());
            using Mat cbuf = new();
            {
                using Mat roi = pred[0, resizedSize.Height, 0, resizedSize.Width];
                roi.ConvertTo(cbuf, MatType.CV_8UC1, 255);
            }
            using Mat binary = BoxThreshold != null ?
                cbuf.Threshold((int)(BoxThreshold * 255), 255, ThresholdTypes.Binary) :
                cbuf;

            Point[][] contours = binary.FindContoursAsArray(RetrievalModes.List, ContourApproximationModes.ApproxSimple);
            Size size = src.Size();
            double scaleRate = 1.0 * src.Width / resizedSize.Width;

            RotatedRect[] rects = contours
                .Where(x => BoxScoreThreahold == null || GetScore(x, pred) > BoxScoreThreahold)
                .Select(x =>
                {
                    RotatedRect rect = Cv2.MinAreaRect(x);

                    float area = (float)Math.Abs(Cv2.ContourArea(x));
                    float perimeter = (float)Cv2.ArcLength(x, true);
                    if (perimeter < 1e-6f)
                        return null;

                    float d = UnclipRatio * area / perimeter;

                    Size2f newSize = new(
                        (rect.Size.Width + 2 * d) * (float)scaleRate,
                        (rect.Size.Height + 2 * d) * (float)scaleRate);

                    RotatedRect enlarged = new(rect.Center * (float)scaleRate, newSize, rect.Angle);

                    return (RotatedRect?)enlarged;
                })
                .Where(r => r.HasValue &&
                            r.Value.Size.Width > MinSize &&
                            r.Value.Size.Height > MinSize)
                .Select(v => v!.Value)
                .OrderBy(v => v.Center.Y)
                .ThenBy(v => v.Center.X)
                .ToArray();
            return rects;
        }
        finally
        {
            dataGcHandle.Free();
        }
    }

    private (int height, int width, float[] data) RunRawCore(Mat src, out Size resizedSize)
    {
        if (src.Empty())
        {
            throw new ArgumentException("src size should not be 0, wrong input picture provided?");
        }

        if (!(src.Channels() switch { 3 or 1 => true, _ => false }))
        {
            throw new NotSupportedException($"{nameof(src)} channel must be 3 or 1, provided {src.Channels()}.");
        }
        Mat padded;
        using (Mat resized = MatResize(src, MaxSize))
        {
            resizedSize = new Size(resized.Width, resized.Height);
            padded = MatPadding32(resized);
        }

        Mat normalized;
        using (Mat _ = padded)
        {
            normalized = Normalize(padded);
        }

        InferenceSession predictor = _p;
        using (Mat _ = normalized)
        {
            int[] inputShape = [1, 3, normalized.Rows, normalized.Cols];
            float[] inputData = ExtractMat(normalized);
            var input = NamedOnnxValue.CreateFromTensor(predictor.InputMetadata.Keys.First(), new DenseTensor<float>(inputData, inputShape));
            using var results = predictor.Run([input]);
            var output = results.First().AsTensor<float>();
            int[] shape = output.Dimensions.ToArray();
            return (shape[2], shape[3], output.ToArray());
        }
    }

    private static float GetScore(Point[] contour, Mat pred)
    {
        int width = pred.Width;
        int height = pred.Height;
        int[] boxX = contour.Select(v => v.X).ToArray();
        int[] boxY = contour.Select(v => v.Y).ToArray();

        int xmin = Math.Clamp(boxX.Min(), 0, width - 1);
        int xmax = Math.Clamp(boxX.Max(), 0, width - 1);
        int ymin = Math.Clamp(boxY.Min(), 0, height - 1);
        int ymax = Math.Clamp(boxY.Max(), 0, height - 1);

        Point[] rootPoints = contour
            .Select(v => new Point(v.X - xmin, v.Y - ymin))
            .ToArray();
        using Mat mask = new(ymax - ymin + 1, xmax - xmin + 1, MatType.CV_8UC1, Scalar.Black);
        mask.FillPoly(new[] { rootPoints }, new Scalar(1));

        using Mat croppedMat = pred[ymin, ymax + 1, xmin, xmax + 1];
        float score = (float)croppedMat.Mean(mask).Val0;

        // Debug
        //{
        //	using Mat cu = new Mat();
        //	croppedMat.ConvertTo(cu, MatType.CV_8UC1, 255);
        //	Util.HorizontalRun(true, Image(cu), Image(mask), score).Dump();
        //}

        return score;
    }

    private static Mat MatResize(Mat src, int? maxSize)
    {
        if (maxSize == null) return src.Clone();

        Size size = src.Size();
        int longEdge = Math.Max(size.Width, size.Height);
        double scaleRate = 1.0 * maxSize.Value / longEdge;

        return scaleRate < 1.0 ?
            src.Resize(default, scaleRate, scaleRate) :
            src.Clone();
    }

    internal static float[] ExtractMat(Mat src)
    {
        int rows = src.Rows;
        int cols = src.Cols;
        float[] result = new float[rows * cols * 3];
        GCHandle resultHandle = default;
        try
        {
            resultHandle = GCHandle.Alloc(result, GCHandleType.Pinned);
            IntPtr resultPtr = resultHandle.AddrOfPinnedObject();
            for (int i = 0; i < src.Channels(); ++i)
            {
                using Mat dest = Mat.FromPixelData(rows, cols, MatType.CV_32FC1, resultPtr + i * rows * cols * sizeof(float));
                Cv2.ExtractChannel(src, dest, i);
            }
        }
        finally
        {
            resultHandle.Free();
        }
        return result;
    }

    private static Mat MatPadding32(Mat src)
    {
        Size size = src.Size();
        Size newSize = new(
            32 * Math.Ceiling(1.0 * size.Width / 32),
            32 * Math.Ceiling(1.0 * size.Height / 32));
        return src.CopyMakeBorder(0, newSize.Height - size.Height, 0, newSize.Width - size.Width, BorderTypes.Constant, Scalar.Black);
    }

    private static Mat Normalize(Mat src)
    {
        using Mat normalized = new();
        src.ConvertTo(normalized, MatType.CV_32FC3, 1.0 / 255);
        Mat[] bgr = normalized.Split();
        float[] scales = new[] { 1 / 0.229f, 1 / 0.224f, 1 / 0.225f };
        float[] means = new[] { 0.485f, 0.456f, 0.406f };
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
}

