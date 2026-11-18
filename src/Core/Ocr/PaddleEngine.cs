using System.Diagnostics;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;

namespace DocumentTextRecognition.Core.Ocr;

/// <summary>
/// PaddleOCR v5 на ONNX Runtime (CPU, FP32): детектор строк <c>mobile_zh_det</c>
/// (многоязычный) и распознавание <c>eslav_PP_OCRv5_mobile_rec</c> (русский, латиница, цифры)
/// либо явно выбранная <c>cyrillic_PP_OCRv5_mobile_rec</c> со своим словарем и графом.
/// Графы лежат в <c>ocr/onnx</c>, словарь и метаданные в <c>ocr/paddle</c>.
/// Альтернативный native Paddle используется для сравнений. Найденные фрагменты собираются в строки
/// по вертикальному перекрытию.
/// </summary>
internal sealed class PaddleEngine : IOcrEngine
{
    private PaddleOcrAll? _ocr;
    private WorkerPaddleDetector? _detector;
    private WorkerPaddleRecognizer? _recognizer;
    private WorkerOnnxDetector? _onnxDetector;
    private WorkerOnnxRecognizer? _onnxRecognizer;

    /// <summary>Выбранный backend; native Paddle можно выбрать явно для сравнения.</summary>
    internal bool UsesOnnx { get; }
    internal bool UseOneDnn { get; }
    internal bool ReusePredictors { get; }
    internal string ModelsDirectory { get; }
    internal string? OnnxModelsDirectory { get; }
    internal string RecognizerName { get; }

    /// <summary>Во сколько раз расширять найденную рамку строки перед распознаванием. 2.0 —
    /// значение PaddleOCR по умолчанию; 2.5 и 3.0 на синтетических сканах читали наименования
    /// хуже (замер на синтетических сканах).</summary>
    public const float UnclipRatio = 2.0f;

    private const string EngineName = "PaddleOCR";
    public string Name => EngineName;

    public PaddleEngine(string modelsDir, bool useOneDnn = true, bool reusePredictors = true,
        bool useOnnx = true, string? onnxModelsDir = null, string recognizerName = OcrComponents.PaddleRecognizer)
    {
        if (recognizerName is not (OcrComponents.PaddleRecognizer or OcrComponents.PaddleCyrillicRecognizer))
            throw new ArgumentException($"неизвестная модель распознавания: {recognizerName}", nameof(recognizerName));
        RecognizerName = recognizerName;
        ModelsDirectory = Path.GetFullPath(modelsDir);
        UseOneDnn = useOneDnn;
        ReusePredictors = reusePredictors;
        var det = Path.Combine(modelsDir, OcrComponents.PaddleDetector);
        var rec = Path.Combine(modelsDir, RecognizerName);
        var onnxDirectory = onnxModelsDir ?? Path.Combine(OcrComponents.ProgramDir, "ocr", "onnx");
        if (RecognizerName == OcrComponents.PaddleCyrillicRecognizer)
            OcrComponents.ValidateCyrillicModelFiles(modelsDir, useOnnx ? onnxDirectory : null);
        foreach (var dir in new[] { det, rec })
            if (!File.Exists(Path.Combine(dir, "inference.pdiparams")))
                throw new FileNotFoundException($"нет модели {Path.GetFileName(dir)} в {modelsDir}");

        var model = new FullOcrModel(
            DetectionModel.FromDirectory(det, ModelVersion.V5),
            RecognizationModel.FromDirectoryV5(rec));
        if (useOnnx)
        {
            var directory = onnxDirectory;
            OnnxModelsDirectory = Path.GetFullPath(directory);
            var detectorFile = Path.Combine(directory, "det.onnx");
            var recognizerFile = Path.Combine(directory, RecognizerName == OcrComponents.PaddleCyrillicRecognizer
                ? OcrComponents.PaddleCyrillicOnnxFile : "rec.onnx");
            foreach (var path in new[] { detectorFile, recognizerFile })
                if (!File.Exists(path)) throw new FileNotFoundException($"нет ONNX-модели {path}", path);
            _onnxDetector = new WorkerOnnxDetector(detectorFile) { UnclipRatio = UnclipRatio };
            try { _onnxRecognizer = new WorkerOnnxRecognizer(model.RecognizationModel, recognizerFile); }
            catch { _onnxDetector.Dispose(); _onnxDetector = null; throw; }
            UsesOnnx = true;
            return;
        }
        // При явно выбранном native Paddle среда и модели ONNX не загружаются.
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

    internal OcrReading Read(Mat gray, Action<string, TimeSpan>? timing, OcrTextProfile profile = OcrTextProfile.General)
    {
        if (profile != OcrTextProfile.General) return ReadMany([gray], timing, profiles: [profile])[0];
        if (timing is not null || _detector is not null || _onnxDetector is not null) return ReadMany([gray], timing)[0];
        ObjectDisposedException.ThrowIf(_ocr is null && _detector is null && _onnxDetector is null, this);
        using var bgr = new Mat();
        Cv2.CvtColor(gray, bgr, ColorConversionCodes.GRAY2BGR);
        var result = _ocr!.Run(bgr);

        return Collect(result.Regions);
    }

    /// <summary>Детектор читает каждое поле отдельно; распознаватель получает строки всех полей одной пачкой.</summary>
    internal OcrReading[] ReadMany(IReadOnlyList<Mat> grays, Action<string, TimeSpan>? timing = null,
        CancellationToken ct = default, IReadOnlyList<OcrTextProfile>? profiles = null) => ReadManyCore(grays, null, timing, ct, profiles);

    /// <summary>Эксперимент: явно известные одиночные строки читаются без нейросетевого детектора.
    /// Флаги принадлежат только этому вызову. Вызывающий код обязан подтвердить такие ответы
    /// вторым движком и повторить неподтвержденные поля через <see cref="ReadMany"/>.</summary>
    internal (OcrReading[] Readings, bool[] KnownLines) ReadKnownLines(IReadOnlyList<Mat> grays,
        Action<string, TimeSpan>? timing = null, CancellationToken ct = default, IReadOnlyList<OcrTextProfile>? profiles = null)
    {
        var knownLines = new bool[grays.Count];
        return (ReadManyCore(grays, knownLines, timing, ct, profiles), knownLines);
    }

    private OcrReading[] ReadManyCore(IReadOnlyList<Mat> grays, bool[]? knownLines,
        Action<string, TimeSpan>? timing, CancellationToken ct, IReadOnlyList<OcrTextProfile>? profiles)
    {
        if (profiles is not null && profiles.Count != grays.Count)
            throw new ArgumentException("Each OCR field needs its own text profile.", nameof(profiles));
        if (profiles is not null && profiles.Any(p => p is not (OcrTextProfile.General or OcrTextProfile.CyrillicDesignation)))
            throw new ArgumentOutOfRangeException(nameof(profiles));
        ObjectDisposedException.ThrowIf(_ocr is null && _detector is null && _onnxDetector is null, this);
        var images = new List<Mat>();
        var lines = new List<Mat>();
        var locations = new List<(int Field, RotatedRect Rect)>();
        try
        {
            for (var field = 0; field < grays.Count; field++)
            {
                ct.ThrowIfCancellationRequested();
                var bgr = new Mat();
                images.Add(bgr);
                Cv2.CvtColor(grays[field], bgr, ColorConversionCodes.GRAY2BGR);
                if (knownLines is not null)
                {
                    var gateStarted = timing is null ? 0 : Stopwatch.GetTimestamp();
                    var eligible = KnownSingleLineRegion.TryGetBounds(grays[field], out var bounds, ct);
                    timing?.Invoke("paddle.known-line", Stopwatch.GetElapsedTime(gateStarted));
                    if (eligible)
                    {
                        lines.Add(new Mat(bgr, bounds));
                        locations.Add((field, new RotatedRect(
                            new Point2f(bounds.X + bounds.Width / 2f, bounds.Y + bounds.Height / 2f),
                            new Size2f(bounds.Width, bounds.Height), 0)));
                        knownLines[field] = true;
                        continue;
                    }
                }
                var started = timing is null ? 0 : Stopwatch.GetTimestamp();
                var rectangles = Detect(bgr);
                timing?.Invoke("paddle.detect", Stopwatch.GetElapsedTime(started));
                ct.ThrowIfCancellationRequested();
                foreach (var rect in rectangles)
                {
                    var box = rect.BoundingRect();
                    var clipped = Rect.FromLTRB(Math.Clamp(box.Left, 0, bgr.Width), Math.Clamp(box.Top, 0, bgr.Height),
                        Math.Clamp(box.Right, 0, bgr.Width), Math.Clamp(box.Bottom, 0, bgr.Height));
                    lines.Add(new Mat(bgr, clipped));
                    locations.Add((field, rect));
                }
            }
            ct.ThrowIfCancellationRequested();
            var recognitionStarted = timing is null ? 0 : Stopwatch.GetTimestamp();
            var recognized = Recognize(lines.ToArray(), profiles is null ? null : locations.Select(p => profiles[p.Field]).ToArray());
            timing?.Invoke("paddle.recognize", Stopwatch.GetElapsedTime(recognitionStarted));
            ct.ThrowIfCancellationRequested();
            var groups = Enumerable.Range(0, grays.Count).Select(_ => new List<PaddleOcrResultRegion>()).ToArray();
            for (var i = 0; i < recognized.Length; i++)
            {
                var answer = recognized[i];
                var field = locations[i].Field;
                var score = knownLines?[field] == true ? KnownLineConfidence(answer) : answer.Score;
                groups[field].Add(new PaddleOcrResultRegion(locations[i].Rect, answer.Text, score, answer.Chars));
            }
            return groups.Select((regions, field) => Collect(regions,
                profiles?[field] ?? OcrTextProfile.General)).ToArray();
        }
        finally
        {
            foreach (var line in lines) line.Dispose();
            foreach (var image in images) image.Dispose();
        }
    }

    private RotatedRect[] Detect(Mat image) => _onnxDetector is not null ? _onnxDetector.Run(image)
        : _detector is not null ? _detector.Run(image) : _ocr!.Detector.Run(image);

    private PaddleOcrRecognizerResult[] Recognize(Mat[] lines, IReadOnlyList<OcrTextProfile>? profiles)
    {
        if (_onnxRecognizer is not null) return _onnxRecognizer.Run(lines, profiles: profiles);
        if (_recognizer is not null) return _recognizer.Run(lines, profiles: profiles);
        var readings = _ocr!.Recognizer.Run(lines);
        // This old comparison backend does not expose logits. Preserve evidence and fail
        // forbidden letters rather than silently removing them or inventing alternatives.
        return profiles is null ? readings : readings.Select((r, i) =>
            PaddleCtcDecoder.ApplyLegacyProfile(r, profiles[i])).ToArray();
    }

    /// <summary>Целая строка не должна скрыть слабое слово высоким средним остальных слов.
    /// Сохраняем минимум штатного score и средних по словам; пробелы не входят в среднее слова.
    /// Неизвестное соответствие символов возвращает NaN и требует полного детектора.</summary>
    internal static float KnownLineConfidence(PaddleOcrRecognizerResult answer)
    {
        if (string.IsNullOrWhiteSpace(answer.Text) || !float.IsFinite(answer.Score) ||
            answer.Score is < 0 or > 1 || answer.Chars is null || answer.Chars.Count == 0 ||
            !string.Equals(string.Concat(answer.Chars.Select(c => c.Character)), answer.Text, StringComparison.Ordinal))
            return float.NaN;
        var minimum = answer.Score;
        double sum = 0;
        var count = 0;
        foreach (var character in answer.Chars)
        {
            if (string.IsNullOrEmpty(character.Character) || character.Character.Length != 1 || !float.IsFinite(character.Score) ||
                character.Score is < 0 or > 1) return float.NaN;
            if (char.IsWhiteSpace(character.Character[0]))
            {
                if (count > 0) minimum = Math.Min(minimum, (float)(sum / count));
                sum = 0;
                count = 0;
            }
            else
            {
                sum += character.Score;
                count++;
            }
        }
        if (count > 0) minimum = Math.Min(minimum, (float)(sum / count));
        return minimum;
    }

    internal static OcrReading Collect(IEnumerable<PaddleOcrResultRegion> regions,
        OcrTextProfile profile = OcrTextProfile.General)
    {
        var pieces = regions
            // A restricted region can become blank after rejecting forbidden letters.
            // Retain that missing evidence so another valid region cannot hide it.
            .Where(r => profile == OcrTextProfile.CyrillicDesignation || !string.IsNullOrWhiteSpace(r.Text))
            .Select(r => (Rect: r.Rect.BoundingRect(), Text: r.Text.Trim(),
                Score: profile == OcrTextProfile.CyrillicDesignation && string.IsNullOrWhiteSpace(r.Text)
                    ? 0d : r.Score))
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

        return new OcrReading(EngineName, lines);
    }

    private static double VerticalOverlap(Rect a, Rect b)
    {
        var overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        var min = Math.Min(a.Height, b.Height);
        return min <= 0 ? 0 : Math.Max(0, overlap) / (double)min;
    }

    public void Dispose()
    {
        _onnxRecognizer?.Dispose();
        _onnxRecognizer = null;
        _onnxDetector?.Dispose();
        _onnxDetector = null;
        _ocr?.Dispose();
        _ocr = null;
        _recognizer?.Dispose();
        _recognizer = null;
        _detector?.Dispose();
        _detector = null;
    }
}
