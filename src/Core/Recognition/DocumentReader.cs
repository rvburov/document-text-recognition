using System.Diagnostics;
using OpenCvSharp;
using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Matching;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Pdf;
using DocumentTextRecognition.Core.Rename;
using DocumentTextRecognition.Core.Templates;

namespace DocumentTextRecognition.Core.Recognition;

/// <summary>Пара движков одного рабочего потока. Пороги уверенности — у каждого свои
/// (шкалы несравнимы); подобраны на синтетических образцах, описание в docs/recognition.md.</summary>
internal sealed class OcrPair(IOcrEngine first, IOcrEngine second, Func<OcrPair>? gostFactory = null) : IDisposable
{
    public IOcrEngine First { get; } = first;
    public IOcrEngine Second { get; } = second;

    internal bool BatchFields { get; set; }

    /// <summary>Чтение одиночных строк без детектора с обязательной независимой сверкой;
    /// сомнительные поля возвращаются к полному детектору.</summary>
    internal bool PreferKnownLines { get; set; } = true;

    /// <summary>Видимый текст PDF принимается только после независимой сверки с растром.</summary>
    internal bool UsePdfText { get; set; } = true;

    internal bool UseGostFallback { get; set; } = true;

    /// <summary>Значение обычной пары со спорными для чертежного шрифта буквами дополнительно
    /// читает модель чертежного шрифта (<see cref="DraftingConfusions"/>). Выключено: модель V3
    /// сама путает «в» и «б», и на прогоне алфавита третье мнение останавливало верных значений
    /// больше, чем неверных (31 против 8). Включать вместе с моделью, которая читает эти буквы
    /// лучше обычной пары (tools/GostTraining/RESULTS-V4.md).</summary>
    internal bool UseThirdOpinion { get; set; }
    private Func<OcrPair>? _gostFactory = gostFactory;
    private bool _packagedGostFactory;
    private OcrPair? _gost;
    private bool _disposed;
    // Only a model choice is retained. Every document is still read and independently
    // confirmed; no recognized text, filename or expected designation is cached.
    private readonly Dictionary<DocTemplate, bool> _gostPreferred = new(ReferenceEqualityComparer.Instance);

    internal bool PrefersGost(DocTemplate template) => UseGostFallback && _gostFactory is not null &&
        _gostPreferred.GetValueOrDefault(template);

    internal void PreferGost(DocTemplate template, bool value) => _gostPreferred[template] = value;

    internal OcrPair? GostPair()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!UseGostFallback || _gostFactory is null) return null;
        _gost ??= _gostFactory();
        _gost.UsePdfText = UsePdfText;
        _gost.PreferKnownLines = PreferKnownLines;
        _gost.BatchFields = BatchFields;
        return _gost;
    }

    public static double MinConfidence(IOcrEngine engine) => engine switch
    {
        TesseractEngine => 0.30,
        PaddleEngine => 0.5,
        _ => 0.5,
    };

    /// <summary>Настоящая пара: Tesseract и PaddleOCR из папки программы.</summary>
    public static OcrPair CreateReal(string tessdata, bool useOneDnn = true, bool reusePredictors = true,
        bool useOnnx = true, string? onnxModelsDir = null)
    {
        var first = new TesseractEngine(tessdata);
        try
        {
            return new OcrPair(first, new PaddleEngine(OcrComponents.PaddleDir, useOneDnn, reusePredictors, useOnnx, onnxModelsDir))
            {
                _gostFactory = () => CreateGost(first.TessdataDirectory, useOneDnn, reusePredictors, useOnnx, onnxModelsDir),
                _packagedGostFactory = true,
            };
        }
        catch { first.Dispose(); throw; }
    }

    private static OcrPair CreateGost(string tessdata, bool useOneDnn, bool reusePredictors, bool useOnnx, string? onnxModelsDir)
    {
        GostTesseractModel.Validate(tessdata);
        var first = new TesseractEngine(tessdata, GostTesseractModel.Languages, GostTesseractModel.RawDesignation,
            restrictDesignationAlphabet: false);
        try
        {
            return new OcrPair(first, new PaddleEngine(OcrComponents.PaddleDir, useOneDnn, reusePredictors,
                useOnnx, onnxModelsDir, OcrComponents.PaddleCyrillicRecognizer)) { UseGostFallback = false };
        }
        catch { first.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { First.Dispose(); }
        finally
        {
            try { Second.Dispose(); }
            finally
            {
                try { _gost?.Dispose(); }
                finally
                {
                    _gostPreferred.Clear();
                    _gost = null;
                    _gostFactory = null;
                }
            }
        }
    }

    internal OcrWorkerConfiguration ExportWorkerConfiguration(string templatesFolder)
    {
        if (First is not TesseractEngine tesseract || Second is not PaddleEngine paddle ||
            !tesseract.IsStandardConfiguration || paddle.RecognizerName != OcrComponents.PaddleRecognizer ||
            UseGostFallback && _gostFactory is not null && !_packagedGostFactory ||
            !string.Equals(paddle.ModelsDirectory, Path.GetFullPath(OcrComponents.PaddleDir), FileNames.PathComparison))
            throw new NotSupportedException("Процессный режим поддерживает штатную пару Tesseract rus+eng и PaddleOCR из поставки.");
        return new OcrWorkerConfiguration(Path.GetFullPath(templatesFolder), tesseract.TessdataDirectory,
            paddle.UseOneDnn, paddle.ReusePredictors, paddle.UsesOnnx, paddle.OnnxModelsDirectory,
            PreferKnownLines, BatchFields, TemplateMatcher.UseAvx2)
            { UsePdfText = UsePdfText, UseGostFallback = UseGostFallback && _packagedGostFactory };
    }
}

/// <summary>Итог чтения документа: подтвержденные поля или причина пропуска.</summary>
internal sealed record DocumentResult(FieldValues? Values, string? Problem, string? Template = null, string? Pages = null)
{
    public RecognitionProvenance? Trace { get; init; }

    // Provenance describes the work performed, not the document's recognized value.
    public bool Equals(DocumentResult? other) => other is not null &&
        Values == other.Values && Problem == other.Problem && Template == other.Template && Pages == other.Pages;

    public override int GetHashCode() => HashCode.Combine(Values, Problem, Template, Pages);

    public static DocumentResult Fail(string problem) => new(null, problem);
}

/// <summary>
/// Чтение одного PDF.
///
/// <para>Многостраничные документы: просматриваются страницы по порядку, не больше
/// <see cref="MaxPages"/>. Страница, не подошедшая ни к одному шаблону, пропускается (листы
/// продолжения, приложения). Каждая подошедшая страница распознается; все они должны дать
/// одинаковые обозначение и наименование. Если хоть одна подошедшая страница не подтвердила
/// поля, подходит к нескольким шаблонам сразу или дает другие данные — файл пропускается
/// целиком: значит, в PDF может быть несколько документов.</para>
/// </summary>
internal static partial class DocumentReader
{
    public const int MaxPages = 10;

    public static DocumentResult Read(string path, IReadOnlyList<DocTemplate> templates, OcrPair ocr, CancellationToken ct,
        Action<RecognitionDiagnostic>? diagnostic = null, Action<string, TimeSpan>? timing = null)
    {
        var trace = new RecognitionTraceBuilder();
        var result = ReadCore(path, templates, ocr, ct, diagnostic, timing, trace);
        return result with { Trace = trace.Snapshot() };
    }

    private static DocumentResult ReadCore(string path, IReadOnlyList<DocTemplate> templates, OcrPair ocr, CancellationToken ct,
        Action<RecognitionDiagnostic>? diagnostic, Action<string, TimeSpan>? timing, RecognitionTraceBuilder trace)
    {
        var started = timing is null ? 0 : Stopwatch.GetTimestamp();
        PdfDocument pdf;
        try { pdf = PdfDocument.Open(path); }
        catch (PdfOpenException ex)
        {
            diagnostic?.Invoke(new("Открытие PDF", 0, ex.Message));
            return DocumentResult.Fail(ex.Message);
        }

        Measure("pdf.open", ref started, timing);
        using (pdf)
        {
            var checkedPages = Math.Min(pdf.PageCount, MaxPages);
            diagnostic?.Invoke(new("Открытие PDF", 0, $"Страниц: {pdf.PageCount}; проверяются первые {checkedPages}."));
            var confirmed = new List<(int Page, FieldValues Values, string Template)>();
            string? firstMiss = null;

            for (var i = 0; i < checkedPages; i++)
            {
                ct.ThrowIfCancellationRequested();
                PageSize size;
                PageFeatures features;
                try
                {
                    started = timing is null ? 0 : Stopwatch.GetTimestamp();
                    size = pdf.GetPageSize(i);
                    using var render = pdf.RenderPage(i, PageFeatures.LongSide);
                    Measure("pdf.render", ref started, timing);
                    var gray = PageImage.ToGray(render);
                    features = PageFeatures.Build(gray, gray.Width / size.Width);
                    Measure("features", ref started, timing);
                }
                catch (PdfOpenException ex) { return DocumentResult.Fail($"стр. {i + 1}: {ex.Message}"); }

                using (features)
                {
                    started = timing is null ? 0 : Stopwatch.GetTimestamp();
                    trace.MatchingCalls++;
                    var outcome = TemplateMatcher.Match(features, size, templates);
                    Measure("match", ref started, timing);
                    if (outcome.Ambiguous)
                    {
                        diagnostic?.Invoke(new("Совмещение", i + 1, outcome.Problem ?? "Подходят несколько шаблонов."));
                        return DocumentResult.Fail($"стр. {i + 1}: {outcome.Problem}");
                    }
                    if (outcome.Match is null)
                    {
                        firstMiss ??= $"стр. {i + 1}: {outcome.Problem}";
                        diagnostic?.Invoke(new("Совмещение", i + 1, outcome.Problem ?? "Подходящий шаблон не найден."));
                        continue;
                    }

                    var match = outcome.Match;
                    trace.MatchedPages++;
                    diagnostic?.Invoke(new("Совмещение", i + 1, $"Шаблон «{match.Template.Name}»: совпадение формы {match.Score:P0}."));
                    var (values, problem) = ReadFields(pdf, i, size, features, match, ocr, ct, diagnostic, timing, trace);
                    if (values is null)
                        return DocumentResult.Fail($"стр. {i + 1} (шаблон «{match.Template.Name}»): {problem}");
                    confirmed.Add((i, values, match.Template.Name));
                }
            }

            if (confirmed.Count == 0)
            {
                if (pdf.PageCount == 1) return DocumentResult.Fail(firstMiss?["стр. 1: ".Length..] ?? "подходящий шаблон не найден");
                var scope = pdf.PageCount > checkedPages ? $"первых {checkedPages} из {pdf.PageCount} страниц" : $"{checkedPages} страниц";
                return DocumentResult.Fail($"ни одна из {scope} не подошла к шаблонам ({firstMiss})");
            }

            var first = confirmed[0];
            var other = confirmed.FirstOrDefault(c => c.Values != first.Values);
            if (other.Values is not null)
                return DocumentResult.Fail(
                    $"страницы дают разные данные: стр. {first.Page + 1} «{first.Values.Designation} {first.Values.Name} {first.Values.Type}», " +
                    $"стр. {other.Page + 1} «{other.Values.Designation} {other.Values.Name} {other.Values.Type}»");

            var pages = string.Join(", ", confirmed.Select(c => c.Page + 1));
            var templatesUsed = string.Join(", ", confirmed.Select(c => c.Template).Distinct());
            return new DocumentResult(first.Values, null, templatesUsed, pages);
        }
    }

    /// <summary>Вырезать поля страницы и прочитать их обоими движками; сверить.</summary>
    public static (FieldValues? Values, string? Problem) ReadFields(PdfDocument pdf, int pageIndex, PageSize size,
        PageFeatures features, PageMatch match, OcrPair ocr, CancellationToken ct,
        Action<RecognitionDiagnostic>? diagnostic = null, Action<string, TimeSpan>? timing = null,
        RecognitionTraceBuilder? trace = null)
    {
        ct.ThrowIfCancellationRequested();
        var clipped = ClippedField(pdf, pageIndex, size, features, match);
        if (clipped is not null)
        {
            diagnostic?.Invoke(new("Области", pageIndex + 1, clipped));
            return (null, clipped);
        }
        var preferGost = ocr.PrefersGost(match.Template);
        string? preferredProblem = null;
        PairResult? gostResult = null;
        if (preferGost)
        {
            var gost = ocr.GostPair()!;
            if (trace is not null) trace.GostAttempts++;
            diagnostic?.Invoke(new("Шрифт", pageIndex + 1, "Проверяется модель чертёжного шрифта."));
            gostResult = ReadFieldsCore(pdf, pageIndex, size, features, match, gost, ct, diagnostic, timing, trace);
            ct.ThrowIfCancellationRequested();
            if (gostResult.Values is not null)
            {
                var checkedGost = ThirdOpinion(pdf, pageIndex, size, features, match, gostResult, ocr, standard: null,
                    acceptedByGost: true, ct, diagnostic, timing, trace);
                if (checkedGost.Values is not null && trace is not null) trace.GostAccepted++;
                return checkedGost;
            }
            preferredProblem = gostResult.Problem;
        }

        var standard = ReadFieldsCore(pdf, pageIndex, size, features, match, ocr, ct, diagnostic, timing, trace);
        ct.ThrowIfCancellationRequested();
        if (standard.Values is not null)
        {
            if (preferGost) ocr.PreferGost(match.Template, false);
            return ThirdOpinion(pdf, pageIndex, size, features, match, standard, ocr, standard: null,
                acceptedByGost: false, ct, diagnostic, timing, trace);
        }
        if (preferGost)
            return (null, $"обычная модель: {standard.Problem}; чертёжный шрифт: {preferredProblem}");
        ct.ThrowIfCancellationRequested();
        var fallback = ocr.GostPair();
        if (fallback is null) return (null, standard.Problem);
        ct.ThrowIfCancellationRequested();
        if (trace is not null) trace.GostAttempts++;
        diagnostic?.Invoke(new("Шрифт", pageIndex + 1,
            "Обычные модели не подтвердили поля; проверяется модель чертёжного шрифта."));
        var alternative = ReadFieldsCore(pdf, pageIndex, size, features, match, fallback, ct, diagnostic, timing, trace);
        ct.ThrowIfCancellationRequested();
        if (alternative.Values is not null)
        {
            var checkedAlternative = ThirdOpinion(pdf, pageIndex, size, features, match, alternative, ocr, standard,
                acceptedByGost: true, ct, diagnostic, timing, trace);
            if (checkedAlternative.Values is null) return checkedAlternative;
            if (trace is not null) trace.GostAccepted++;
            ocr.PreferGost(match.Template, true);
            return checkedAlternative;
        }
        return (null, $"обычная модель: {standard.Problem}; чертёжный шрифт: {alternative.Problem}");
    }

    /// <summary>
    /// Текст поля не должен выходить за сохраненную область: иначе оба движка согласованно
    /// прочитают обрезанное значение. Проверяется до чтения, одинаково для текста PDF и OCR.
    /// </summary>
    private static string? ClippedField(PdfDocument pdf, int pageIndex, PageSize size, PageFeatures features, PageMatch match)
    {
        var frames = match.Template.Frames;
        foreach (var frame in frames)
        {
            var problem = FieldCrop.ClippedText(pdf, pageIndex, size, features, match, frame.Inner,
                frames.Where(f => !ReferenceEquals(f, frame)).Select(f => f.Inner));
            if (problem is not null)
                return $"{FieldName(frame.Role)}: {problem}; проверьте область в редакторе шаблона «{match.Template.Name}»";
        }
        return null;
    }

    /// <summary>Итог одной пары: значения или причина, разобранные ответы обоих движков (для
    /// третьего мнения) и признак, что значения взяты из текста PDF.</summary>
    private sealed record PairResult(FieldValues? Values, string? Problem, EngineFields? First = null,
        EngineFields? Second = null, bool FromPdfText = false);

    private static PairResult ReadFieldsCore(PdfDocument pdf, int pageIndex, PageSize size,
        PageFeatures features, PageMatch match, OcrPair ocr, CancellationToken ct,
        Action<RecognitionDiagnostic>? diagnostic, Action<string, TimeSpan>? timing,
        RecognitionTraceBuilder? trace)
    {
        ct.ThrowIfCancellationRequested();
        var started = timing is null ? 0 : Stopwatch.GetTimestamp();
        if (ocr.UsePdfText && TryReadPdfTextFields(pdf, pageIndex, features, match, ocr, ct,
                diagnostic, timing, trace, out var pdfValues))
            return new(pdfValues, null, FromPdfText: true);
        var frames = match.Template.Frames;
        var combined = frames.FirstOrDefault(f => f.Role == FieldRole.Combined);
        diagnostic?.Invoke(new("Алфавиты", pageIndex + 1,
            "Обозначение: кириллица, цифры и знаки формата. Наименование: русский и английский."));
        EngineFields first, second;
        if (combined is not null)
        {
            using var crop = FieldCrop.Extract(pdf, pageIndex, size, features, match, combined.Inner, timing);
            Measure("crop", ref started, timing);
            ct.ThrowIfCancellationRequested();
            var a = ReadEngine(ocr.First, crop, timing, trace: trace);
            if (timing is not null) Measure("ocr." + ocr.First.Name, ref started, timing);
            ct.ThrowIfCancellationRequested();
            var b = ReadEngine(ocr.Second, crop, timing, trace: trace);
            if (timing is not null) Measure("ocr." + ocr.Second.Name, ref started, timing);
            first = ParseCombined(ocr.First, a);
            second = ParseCombined(ocr.Second, b);
            if (diagnostic is not null)
                diagnostic(new("Распознавание", pageIndex + 1, "Все поля вместе", new FieldDiagnostic(
                    combined.Role, combined.Inner, crop.ToBytes(".png"),
                    DescribeCombined(a, first, ocr.First), DescribeCombined(b, second, ocr.Second))));
        }
        else if (ocr.BatchFields && (ocr.First is PaddleEngine || ocr.Second is PaddleEngine))
        {
            (first, second) = ReadSeparateBatch(pdf, pageIndex, size, features, match, ocr, ct, diagnostic, timing, trace);
        }
        else
        {
            var designation = ReadField(FieldRole.Designation, FieldParser.ParseDesignation);
            var name = ReadField(FieldRole.Name, FieldParser.ParseName);
            (Parsed First, Parsed Second) type;
            if (match.Template.TypeAbsent)
            {
                // Это явная настройка сохраненного шаблона, а не вывод из отсутствия рамки.
                type = (Parsed.Ok(""), Parsed.Ok(""));
                diagnostic?.Invoke(new("Области", pageIndex + 1,
                    $"Тип документа отсутствует по настройке шаблона: «{DocTypes.WhenMissing}»."));
            }
            else type = ReadField(FieldRole.Type, FieldParser.ParseType);
            first = new EngineFields(ocr.First.Name, designation.First, name.First, type.First);
            second = new EngineFields(ocr.Second.Name, designation.Second, name.Second, type.Second);
        }

        var result = EngineAgreement.Combine(first, second);
        diagnostic?.Invoke(new("Сверка движков", pageIndex + 1,
            result.Problem ?? "Все поля подтверждены обоими способами распознавания."));
        return new(result.Values, result.Problem, first, second);

        (Parsed First, Parsed Second) ReadField(FieldRole role, Func<OcrReading, double, Parsed> parse)
        {
            started = timing is null ? 0 : Stopwatch.GetTimestamp();
            var frame = frames.First(f => f.Role == role);
            using var crop = FieldCrop.Extract(pdf, pageIndex, size, features, match, frame.Inner, timing);
            Measure("crop", ref started, timing);
            ct.ThrowIfCancellationRequested();
            OcrReading a, b;
            if (ocr.PreferKnownLines)
            {
                var readings = ReadSeparateFields([crop], [role], ocr, ct, timing, trace);
                a = readings.First[0];
                b = readings.Second[0];
            }
            else
            {
                a = ReadEngine(ocr.First, crop, timing, Profile(role), trace);
                if (timing is not null) Measure("ocr." + ocr.First.Name, ref started, timing);
                ct.ThrowIfCancellationRequested();
                started = timing is null ? 0 : Stopwatch.GetTimestamp();
                b = ReadEngine(ocr.Second, crop, timing, Profile(role), trace);
                if (timing is not null) Measure("ocr." + ocr.Second.Name, ref started, timing);
            }
            var parsedA = parse(a, OcrPair.MinConfidence(ocr.First));
            var parsedB = parse(b, OcrPair.MinConfidence(ocr.Second));
            if (diagnostic is not null)
                diagnostic(new("Распознавание", pageIndex + 1, FieldName(role), new FieldDiagnostic(
                    role, frame.Inner, crop.ToBytes(".png"),
                    new EngineDiagnostic(a.Engine, a.Text, parsedA.Value ?? "", parsedA.Problem,
                        a.MinConfidence, OcrPair.MinConfidence(ocr.First)),
                    new EngineDiagnostic(b.Engine, b.Text, parsedB.Value ?? "", parsedB.Problem,
                        b.MinConfidence, OcrPair.MinConfidence(ocr.Second)))));
            return (parsedA, parsedB);
        }
    }

    private static (EngineFields First, EngineFields Second) ReadSeparateBatch(PdfDocument pdf, int pageIndex,
        PageSize size, PageFeatures features, PageMatch match, OcrPair ocr, CancellationToken ct,
        Action<RecognitionDiagnostic>? diagnostic, Action<string, TimeSpan>? timing, RecognitionTraceBuilder? trace)
    {
        var roles = match.Template.TypeAbsent
            ? new[] { FieldRole.Designation, FieldRole.Name }
            : new[] { FieldRole.Designation, FieldRole.Name, FieldRole.Type };
        var frames = roles.Select(role => match.Template.Frames.First(f => f.Role == role)).ToArray();
        var crops = new List<Mat>();
        try
        {
            foreach (var frame in frames)
            {
                ct.ThrowIfCancellationRequested();
                var started = timing is null ? 0 : Stopwatch.GetTimestamp();
                crops.Add(FieldCrop.Extract(pdf, pageIndex, size, features, match, frame.Inner, timing));
                Measure("crop", ref started, timing);
            }
            var (a, b) = ReadSeparateFields(crops, roles, ocr, ct, timing, trace);
            var parsedA = new Parsed[3];
            var parsedB = new Parsed[3];
            for (var i = 0; i < roles.Length; i++)
            {
                parsedA[i] = Parse(roles[i], a[i], OcrPair.MinConfidence(ocr.First));
                parsedB[i] = Parse(roles[i], b[i], OcrPair.MinConfidence(ocr.Second));
                if (diagnostic is not null)
                    diagnostic(new("Распознавание", pageIndex + 1, FieldName(roles[i]), new FieldDiagnostic(
                        roles[i], frames[i].Inner, crops[i].ToBytes(".png"),
                        new EngineDiagnostic(a[i].Engine, a[i].Text, parsedA[i].Value ?? "", parsedA[i].Problem,
                            a[i].MinConfidence, OcrPair.MinConfidence(ocr.First)),
                        new EngineDiagnostic(b[i].Engine, b[i].Text, parsedB[i].Value ?? "", parsedB[i].Problem,
                            b[i].MinConfidence, OcrPair.MinConfidence(ocr.Second)))));
            }
            if (match.Template.TypeAbsent)
            {
                parsedA[2] = parsedB[2] = Parsed.Ok("");
                diagnostic?.Invoke(new("Области", pageIndex + 1,
                    $"Тип документа отсутствует по настройке шаблона: «{DocTypes.WhenMissing}»."));
            }
            return (new EngineFields(ocr.First.Name, parsedA[0], parsedA[1], parsedA[2]),
                new EngineFields(ocr.Second.Name, parsedB[0], parsedB[1], parsedB[2]));

        }
        finally { foreach (var crop in crops) crop.Dispose(); }
    }

    /// <summary>Читает неизмененные вырезки полей. Прямой ответ Paddle принимается только по
    /// обычной политике сверки; иначе повторяется полный Paddle для этого поля, без повторного
    /// Tesseract. Исключения движков распространяются без повторов.</summary>
    internal static (OcrReading[] First, OcrReading[] Second) ReadSeparateFields(IReadOnlyList<Mat> crops,
        IReadOnlyList<FieldRole> roles, OcrPair ocr, CancellationToken ct,
        Action<string, TimeSpan>? timing = null, RecognitionTraceBuilder? trace = null)
    {
        if (crops.Count != roles.Count || roles.Any(role => role is not
            (FieldRole.Designation or FieldRole.Name or FieldRole.Type)))
            throw new ArgumentException("Ожидаются отдельные области обозначения, наименования и типа.", nameof(roles));
        var profiles = roles.Select(Profile).ToArray();

        // Быстрое чтение проверяет независимый движок, а не другая копия Paddle.
        var preferKnown = ocr.PreferKnownLines &&
            ((ocr.First is PaddleEngine) != (ocr.Second is PaddleEngine));
        var a = Read(ocr.First);
        var b = Read(ocr.Second);
        Complete(ocr.First, a, ocr.Second, b.Readings);
        Complete(ocr.Second, b, ocr.First, a.Readings);
        return (a.Readings, b.Readings);

        (OcrReading[] Readings, bool[]? KnownLines) Read(IOcrEngine engine)
        {
            ct.ThrowIfCancellationRequested();
            var started = timing is null ? 0 : Stopwatch.GetTimestamp();
            (OcrReading[] Readings, bool[]? KnownLines) result;
            if (engine is PaddleEngine paddle)
            {
                CountRead(engine, crops.Count, trace);
                result = preferKnown ? paddle.ReadKnownLines(crops, timing, ct, profiles) :
                    (paddle.ReadMany(crops, timing, ct, profiles), null);
            }
            else
                result = (crops.Select((crop, i) =>
                {
                    ct.ThrowIfCancellationRequested();
                    return ReadEngine(engine, crop, null, profiles[i], trace);
                }).ToArray(), null);
            ct.ThrowIfCancellationRequested();
            if (timing is not null) Measure("ocr." + engine.Name, ref started, timing);
            return result;
        }

        void Complete(IOcrEngine engine, (OcrReading[] Readings, bool[]? KnownLines) candidate,
            IOcrEngine peerEngine, OcrReading[] peer)
        {
            if (candidate.KnownLines is null || engine is not PaddleEngine paddle) return;
            var failed = Enumerable.Range(0, crops.Count).Where(i => candidate.KnownLines[i] &&
                !IsKnownLineConfirmed(roles[i], candidate.Readings[i], OcrPair.MinConfidence(engine),
                    peer[i], OcrPair.MinConfidence(peerEngine))).ToArray();
            if (failed.Length == 0) return;
            ct.ThrowIfCancellationRequested();
            var started = timing is null ? 0 : Stopwatch.GetTimestamp();
            CountRead(paddle, failed.Length, trace);
            var fallback = paddle.ReadMany(failed.Select(i => crops[i]).ToArray(), timing, ct,
                failed.Select(i => profiles[i]).ToArray());
            if (timing is not null)
            {
                var elapsed = Stopwatch.GetElapsedTime(started);
                timing("paddle.fallback", elapsed);
                timing("ocr." + engine.Name, elapsed);
            }
            for (var i = 0; i < failed.Length; i++) candidate.Readings[failed[i]] = fallback[i];
        }
    }

    internal static bool IsKnownLineConfirmed(FieldRole role, OcrReading candidate, double candidateMinConfidence,
        OcrReading peer, double peerMinConfidence)
    {
        if (candidate.IsEmpty || !double.IsFinite(candidate.MinConfidence) ||
            candidate.MinConfidence < candidateMinConfidence) return false;
        var parsed = Parse(role, candidate, candidateMinConfidence);
        // Даже непустой OCR-ответ (например, черточки) может разбираться как пустой тип.
        // Отсутствие поля подтверждается только полным детектором, не прямым чтением строки.
        return !string.IsNullOrEmpty(parsed.Value) &&
            EngineAgreement.AreConfirmed(parsed, Parse(role, peer, peerMinConfidence));
    }

    private static Parsed Parse(FieldRole role, OcrReading reading, double confidence) => role switch
    {
        FieldRole.Designation => FieldParser.ParseDesignation(reading, confidence),
        FieldRole.Name => FieldParser.ParseName(reading, confidence),
        FieldRole.Type => FieldParser.ParseType(reading, confidence),
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    private static OcrTextProfile Profile(FieldRole role) => role == FieldRole.Designation
        ? OcrTextProfile.CyrillicDesignation : OcrTextProfile.General;

    private static OcrReading ReadEngine(IOcrEngine engine, Mat crop, Action<string, TimeSpan>? timing,
        OcrTextProfile profile = OcrTextProfile.General, RecognitionTraceBuilder? trace = null)
    {
        CountRead(engine, 1, trace);
        return engine switch
        {
            PaddleEngine paddle => paddle.Read(crop, timing, profile),
            TesseractEngine tess => tess.Read(crop, profile),
            _ => engine.Read(crop),
        };
    }

    private static void CountRead(IOcrEngine engine, int count, RecognitionTraceBuilder? trace)
    {
        if (trace is null) return;
        if (engine is TesseractEngine) trace.TesseractReads += count;
        else if (engine is PaddleEngine) trace.PaddleReads += count;
        else if (engine.Name == "EasyOCR") trace.EasyReads += count;
    }

    private static void Measure(string phase, ref long started, Action<string, TimeSpan>? timing)
    {
        if (timing is null) return;
        var finished = Stopwatch.GetTimestamp();
        timing(phase, Stopwatch.GetElapsedTime(started, finished));
        started = Stopwatch.GetTimestamp();
    }

    private static EngineFields ParseCombined(IOcrEngine engine, OcrReading reading)
    {
        var (designation, name, type) = FieldParser.ParseCombined(reading, OcrPair.MinConfidence(engine));
        return new EngineFields(engine.Name, designation, name, type);
    }

    private static EngineDiagnostic DescribeCombined(OcrReading reading, EngineFields fields, IOcrEngine engine) => new(
        reading.Engine, reading.Text,
        $"Обозначение: {fields.Designation.Value}\nНаименование: {fields.Name.Value}\nТип: {fields.Type.Value}",
        JoinProblems(fields.Designation.Problem, fields.Name.Problem, fields.Type.Problem),
        reading.MinConfidence, OcrPair.MinConfidence(engine));

    private static string? JoinProblems(params string?[] problems)
    {
        var distinct = problems.Where(p => p is not null).Distinct().ToArray();
        return distinct.Length == 0 ? null : string.Join("; ", distinct);
    }

    internal static string FieldName(FieldRole role) => role switch
    {
        FieldRole.Designation => "Обозначение",
        FieldRole.Name => "Наименование",
        FieldRole.Type => "Тип документа",
        _ => "Все поля вместе",
    };
}
