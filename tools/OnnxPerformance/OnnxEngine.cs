using System.Diagnostics;
using DocumentTextRecognition.Core.Ocr;
using OpenCvSharp;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;

namespace DocumentTextRecognition.OnnxPerformance;

// Copied from production PaddleEngine for an isolated backend comparison. Inference calls
// differ; field indexing, clipping, line grouping and confidence aggregation are unchanged.
internal sealed class OnnxEngine : IDisposable
{
    private readonly OnnxDetector _detector;
    private readonly OnnxRecognizer _recognizer;
    public string Name => "PaddleOCR";

    public OnnxEngine(FullOcrModel model, string directory)
    {
        _detector = new OnnxDetector(model.DetectionModel, Path.Combine(directory, "det.onnx")) { UnclipRatio = PaddleEngine.UnclipRatio };
        try { _recognizer = new OnnxRecognizer(model.RecognizationModel, Path.Combine(directory, "rec.onnx")); }
        catch { _detector.Dispose(); throw; }
    }

    /// <summary>Детектор читает каждое поле отдельно; распознаватель получает строки всех полей одной пачкой.</summary>
    internal OcrReading[] ReadMany(IReadOnlyList<Mat> grays, Action<string, TimeSpan>? timing = null)
    {
        var images = new List<Mat>();
        var lines = new List<Mat>();
        var locations = new List<(int Field, RotatedRect Rect)>();
        try
        {
            for (var field = 0; field < grays.Count; field++)
            {
                var bgr = new Mat();
                images.Add(bgr);
                Cv2.CvtColor(grays[field], bgr, ColorConversionCodes.GRAY2BGR);
                var started = timing is null ? 0 : Stopwatch.GetTimestamp();
                var rectangles = _detector.Run(bgr);
                timing?.Invoke("paddle.detect", Stopwatch.GetElapsedTime(started));
                foreach (var rect in rectangles)
                {
                    var box = rect.BoundingRect();
                    var clipped = Rect.FromLTRB(Math.Clamp(box.Left, 0, bgr.Width), Math.Clamp(box.Top, 0, bgr.Height),
                        Math.Clamp(box.Right, 0, bgr.Width), Math.Clamp(box.Bottom, 0, bgr.Height));
                    lines.Add(new Mat(bgr, clipped));
                    locations.Add((field, rect));
                }
            }
            var recognitionStarted = timing is null ? 0 : Stopwatch.GetTimestamp();
            var recognized = _recognizer.Run(lines.ToArray());
            timing?.Invoke("paddle.recognize", Stopwatch.GetElapsedTime(recognitionStarted));
            var groups = Enumerable.Range(0, grays.Count).Select(_ => new List<PaddleOcrResultRegion>()).ToArray();
            for (var i = 0; i < recognized.Length; i++)
            {
                var answer = recognized[i];
                groups[locations[i].Field].Add(new PaddleOcrResultRegion(locations[i].Rect, answer.Text, answer.Score, answer.Chars));
            }
            return groups.Select(Collect).ToArray();
        }
        finally
        {
            foreach (var line in lines) line.Dispose();
            foreach (var image in images) image.Dispose();
        }
    }

    private OcrReading Collect(IEnumerable<PaddleOcrResultRegion> regions)
    {
        var pieces = regions
            .Where(r => !string.IsNullOrWhiteSpace(r.Text))
            .Select(r => (Rect: r.Rect.BoundingRect(), Text: r.Text.Trim(), Score: (double)r.Score))
            .OrderBy(p => p.Rect.Top + p.Rect.Height / 2)
            .ToList();

        // Строка — фрагменты, перекрывающиеся по вертикали больше чем наполовину меньшего.
        var rows = new List<List<(Rect Rect, string Text, double Score)>>();
        foreach (var p in pieces)
        {
            var row = rows.FirstOrDefault(r => r.Any(q => VerticalOverlap(q.Rect, p.Rect) > 0.5));
            if (row is null) rows.Add([p]);
            else row.Add(p);
        }

        var lines = rows.Select(row =>
        {
            var ordered = row.OrderBy(q => q.Rect.Left).ToList();
            return new OcrLine(
                string.Join(" ", ordered.Select(q => q.Text)),
                Math.Clamp(ordered.Min(q => q.Score), 0, 1),
                ordered.Min(q => q.Rect.Left), ordered.Min(q => q.Rect.Top),
                ordered.Max(q => q.Rect.Right), ordered.Max(q => q.Rect.Bottom));
        }).OrderBy(l => l.Top).ToList();

        return new OcrReading(Name, lines);
    }

    private static double VerticalOverlap(Rect a, Rect b)
    {
        var overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        var min = Math.Min(a.Height, b.Height);
        return min <= 0 ? 0 : Math.Max(0, overlap) / (double)min;
    }

    public void Dispose()
    {
        _recognizer.Dispose();
        _detector.Dispose();
    }
}
