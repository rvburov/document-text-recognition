using System.Diagnostics;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;

namespace DocumentTextRecognition.Core.Ocr;

/// <summary>
/// PaddleOCR v5 на движке Paddle Inference (CPU, oneDNN): детектор строк <c>mobile_zh_det</c>
/// (многоязычный) и распознавание <c>eslav_PP_OCRv5_mobile_rec</c> (русский, латиница, цифры).
/// Модели лежат в папке программы <c>ocr/paddle</c>. Найденные фрагменты собираются в строки
/// по вертикальному перекрытию.
/// </summary>
internal sealed class PaddleEngine : IOcrEngine
{
    private PaddleOcrAll? _ocr;
    private WorkerPaddleDetector? _detector;
    private WorkerPaddleRecognizer? _recognizer;

    /// <summary>Во сколько раз расширять найденную рамку строки перед распознаванием. 2.0 —
    /// значение PaddleOCR по умолчанию; 2.5 и 3.0 на синтетических сканах читали наименования
    /// хуже (замер на синтетических сканах).</summary>
    public const float UnclipRatio = 2.0f;

    public string Name => "PaddleOCR";

    public PaddleEngine(string modelsDir, bool useOneDnn = true, bool reusePredictors = true)
    {
        var det = Path.Combine(modelsDir, OcrComponents.PaddleDetector);
        var rec = Path.Combine(modelsDir, OcrComponents.PaddleRecognizer);
        foreach (var dir in new[] { det, rec })
            if (!File.Exists(Path.Combine(dir, "inference.pdiparams")))
                throw new FileNotFoundException($"нет модели {Path.GetFileName(dir)} в {modelsDir}");

        var model = new FullOcrModel(
            DetectionModel.FromDirectory(det, ModelVersion.V5),
            RecognizationModel.FromDirectoryV5(rec));
        // Один поток вычислений на движок: параллельность дают рабочие потоки прогона.
        Action<PaddleConfig> device = useOneDnn ? config =>
        {
            PaddleDevice.OneDnn(cacheCapacity: 10, cpuMathThreadCount: 1)(config);
            if (!config.OneDnnEnabled)
                throw new PlatformNotSupportedException("Установленная библиотека Paddle Inference не поддерживает oneDNN");
        } : PaddleDevice.Blas(1);
        if (reusePredictors)
        {
            _detector = new WorkerPaddleDetector(model.DetectionModel, device) { UnclipRatio = UnclipRatio };
            try { _recognizer = new WorkerPaddleRecognizer(model.RecognizationModel, device); }
            catch { _detector.Dispose(); _detector = null; throw; }
        }
        else
        {
            // Этот путь нужен для сравнительных проверок. Предикторы создаются отдельно,
            // чтобы ошибка второй модели сразу освобождала уже созданную первую.
            using var detectorConfig = model.DetectionModel.CreateConfig();
            model.DetectionModel.ConfigureDevice(detectorConfig, device);
            var detector = new PaddleOcrDetector(detectorConfig.CreatePredictor()) { UnclipRatio = UnclipRatio };
            try
            {
                using var recognizerConfig = model.RecognizationModel.CreateConfig();
                model.RecognizationModel.ConfigureDevice(recognizerConfig, device);
                var recognizer = new PaddleOcrRecognizer(model.RecognizationModel, recognizerConfig.CreatePredictor());
                try
                {
                    _ocr = new PaddleOcrAll(detector, null, recognizer)
                    {
                        AllowRotateDetection = false,
                        Enable180Classification = false,
                    };
                }
                catch { recognizer.Dispose(); throw; }
            }
            catch { detector.Dispose(); throw; }
        }
    }

    public OcrReading Read(Mat gray) => Read(gray, null);

    internal OcrReading Read(Mat gray, Action<string, TimeSpan>? timing)
    {
        if (timing is not null || _detector is not null) return ReadMany([gray], timing)[0];
        ObjectDisposedException.ThrowIf(_ocr is null && _detector is null, this);
        using var bgr = new Mat();
        Cv2.CvtColor(gray, bgr, ColorConversionCodes.GRAY2BGR);
        var result = _ocr!.Run(bgr);

        return Collect(result.Regions);
    }

    /// <summary>Детектор читает каждое поле отдельно; распознаватель получает строки всех полей одной пачкой.</summary>
    internal OcrReading[] ReadMany(IReadOnlyList<Mat> grays, Action<string, TimeSpan>? timing = null)
    {
        ObjectDisposedException.ThrowIf(_ocr is null && _detector is null, this);
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
                var rectangles = _detector is not null ? _detector.Run(bgr) : _ocr!.Detector.Run(bgr);
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
            var recognized = _recognizer is not null ? _recognizer.Run(lines.ToArray()) : _ocr!.Recognizer.Run(lines.ToArray());
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
        _ocr?.Dispose();
        _ocr = null;
        _recognizer?.Dispose();
        _recognizer = null;
        _detector?.Dispose();
        _detector = null;
    }
}
