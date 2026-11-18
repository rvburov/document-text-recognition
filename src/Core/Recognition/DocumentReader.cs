using System.Diagnostics;
using OpenCvSharp;
using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Matching;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Pdf;
using DocumentTextRecognition.Core.Templates;

namespace DocumentTextRecognition.Core.Recognition;

/// <summary>Пара движков одного рабочего потока. Пороги уверенности — у каждого свои
/// (шкалы несравнимы); подобраны на синтетических образцах, описание в docs/recognition.md.</summary>
internal sealed class OcrPair(IOcrEngine first, IOcrEngine second) : IDisposable
{
    public IOcrEngine First { get; } = first;
    public IOcrEngine Second { get; } = second;

    internal bool BatchFields { get; set; } = true;

    /// <summary>Экспериментальное чтение заведомо одиночных строк без детектора Paddle.
    /// По умолчанию выключено до проверки на более широком наборе сканов.</summary>
    internal bool PreferKnownLines { get; set; }

    public static double MinConfidence(IOcrEngine engine) => engine switch
    {
        TesseractEngine => 0.30,
        PaddleEngine => 0.5,
        _ => 0.5,
    };

    /// <summary>Настоящая пара: Tesseract и PaddleOCR из папки программы.</summary>
    public static OcrPair CreateReal(string tessdata, bool useOneDnn = true, bool reusePredictors = true)
    {
        var first = new TesseractEngine(tessdata);
        try { return new OcrPair(first, new PaddleEngine(OcrComponents.PaddleDir, useOneDnn, reusePredictors)); }
        catch { first.Dispose(); throw; }
    }

    public void Dispose()
    {
        First.Dispose();
        Second.Dispose();
    }
}

/// <summary>Итог чтения документа: подтвержденные поля или причина пропуска.</summary>
internal sealed record DocumentResult(FieldValues? Values, string? Problem, string? Template = null, string? Pages = null)
{
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
internal static class DocumentReader
{
    public const int MaxPages = 10;

    public static DocumentResult Read(string path, IReadOnlyList<DocTemplate> templates, OcrPair ocr, CancellationToken ct,
        Action<RecognitionDiagnostic>? diagnostic = null, Action<string, TimeSpan>? timing = null)
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
                    diagnostic?.Invoke(new("Совмещение", i + 1, $"Шаблон «{match.Template.Name}»: совпадение формы {match.Score:P0}."));
                    var (values, problem) = ReadFields(pdf, i, size, features, match, ocr, ct, diagnostic, timing);
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
        Action<RecognitionDiagnostic>? diagnostic = null, Action<string, TimeSpan>? timing = null)
    {
        var started = timing is null ? 0 : Stopwatch.GetTimestamp();
        var frames = match.Template.Frames;
        var combined = frames.FirstOrDefault(f => f.Role == FieldRole.Combined);
        EngineFields first, second;
        if (combined is not null)
        {
            using var crop = FieldCrop.Extract(pdf, pageIndex, size, features, match, combined.Inner, timing);
            Measure("crop", ref started, timing);
            ct.ThrowIfCancellationRequested();
            var a = ReadEngine(ocr.First, crop, timing);
            if (timing is not null) Measure("ocr." + ocr.First.Name, ref started, timing);
            ct.ThrowIfCancellationRequested();
            var b = ReadEngine(ocr.Second, crop, timing);
            if (timing is not null) Measure("ocr." + ocr.Second.Name, ref started, timing);
            first = ParseCombined(ocr.First, a);
            second = ParseCombined(ocr.Second, b);
            if (diagnostic is not null)
                diagnostic(new("Распознавание", pageIndex + 1, "Все поля вместе", new FieldDiagnostic(
                    combined.Role, combined.Inner, crop.ToBytes(".png"),
                    DescribeCombined(a, first), DescribeCombined(b, second))));
        }
        else if (ocr.BatchFields && (ocr.First is PaddleEngine || ocr.Second is PaddleEngine))
        {
            (first, second) = ReadSeparateBatch(pdf, pageIndex, size, features, match, ocr, ct, diagnostic, timing);
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
        return result;

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
                var readings = ReadSeparateFields([crop], [role], ocr, ct, timing);
                a = readings.First[0];
                b = readings.Second[0];
            }
            else
            {
                a = ReadEngine(ocr.First, crop, timing);
                if (timing is not null) Measure("ocr." + ocr.First.Name, ref started, timing);
                ct.ThrowIfCancellationRequested();
                started = timing is null ? 0 : Stopwatch.GetTimestamp();
                b = ReadEngine(ocr.Second, crop, timing);
                if (timing is not null) Measure("ocr." + ocr.Second.Name, ref started, timing);
            }
            var parsedA = parse(a, OcrPair.MinConfidence(ocr.First));
            var parsedB = parse(b, OcrPair.MinConfidence(ocr.Second));
            if (diagnostic is not null)
                diagnostic(new("Распознавание", pageIndex + 1, FieldName(role), new FieldDiagnostic(
                    role, frame.Inner, crop.ToBytes(".png"),
                    new EngineDiagnostic(a.Engine, a.Text, parsedA.Value ?? "", parsedA.Problem),
                    new EngineDiagnostic(b.Engine, b.Text, parsedB.Value ?? "", parsedB.Problem))));
            return (parsedA, parsedB);
        }
    }

    private static (EngineFields First, EngineFields Second) ReadSeparateBatch(PdfDocument pdf, int pageIndex,
        PageSize size, PageFeatures features, PageMatch match, OcrPair ocr, CancellationToken ct,
        Action<RecognitionDiagnostic>? diagnostic, Action<string, TimeSpan>? timing)
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
            var (a, b) = ReadSeparateFields(crops, roles, ocr, ct, timing);
            var parsedA = new Parsed[3];
            var parsedB = new Parsed[3];
            for (var i = 0; i < roles.Length; i++)
            {
                parsedA[i] = Parse(roles[i], a[i], OcrPair.MinConfidence(ocr.First));
                parsedB[i] = Parse(roles[i], b[i], OcrPair.MinConfidence(ocr.Second));
                if (diagnostic is not null)
                    diagnostic(new("Распознавание", pageIndex + 1, FieldName(roles[i]), new FieldDiagnostic(
                        roles[i], frames[i].Inner, crops[i].ToBytes(".png"),
                        new EngineDiagnostic(a[i].Engine, a[i].Text, parsedA[i].Value ?? "", parsedA[i].Problem),
                        new EngineDiagnostic(b[i].Engine, b[i].Text, parsedB[i].Value ?? "", parsedB[i].Problem))));
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
        Action<string, TimeSpan>? timing = null)
    {
        if (crops.Count != roles.Count || roles.Any(role => role is not
            (FieldRole.Designation or FieldRole.Name or FieldRole.Type)))
            throw new ArgumentException("Ожидаются отдельные области обозначения, наименования и типа.", nameof(roles));

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
                result = preferKnown ? paddle.ReadKnownLines(crops, timing, ct) :
                    (paddle.ReadMany(crops, timing, ct), null);
            else
                result = (crops.Select(crop =>
                {
                    ct.ThrowIfCancellationRequested();
                    return engine.Read(crop);
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
            var fallback = paddle.ReadMany(failed.Select(i => crops[i]).ToArray(), timing, ct);
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
        OcrReading peer, double peerMinConfidence) =>
        // Пустой прямой ответ не подтверждает отсутствие типа, даже если Tesseract тоже пуст.
        !candidate.IsEmpty && double.IsFinite(candidate.MinConfidence) &&
        candidate.MinConfidence >= candidateMinConfidence &&
        EngineAgreement.AreConfirmed(Parse(role, candidate, candidateMinConfidence),
            Parse(role, peer, peerMinConfidence));

    private static Parsed Parse(FieldRole role, OcrReading reading, double confidence) => role switch
    {
        FieldRole.Designation => FieldParser.ParseDesignation(reading, confidence),
        FieldRole.Name => FieldParser.ParseName(reading, confidence),
        FieldRole.Type => FieldParser.ParseType(reading, confidence),
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    private static OcrReading ReadEngine(IOcrEngine engine, Mat crop, Action<string, TimeSpan>? timing) =>
        engine is PaddleEngine paddle ? paddle.Read(crop, timing) : engine.Read(crop);

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

    private static EngineDiagnostic DescribeCombined(OcrReading reading, EngineFields fields) => new(
        reading.Engine, reading.Text,
        $"Обозначение: {fields.Designation.Value}\nНаименование: {fields.Name.Value}\nТип: {fields.Type.Value}",
        JoinProblems(fields.Designation.Problem, fields.Name.Problem, fields.Type.Problem));

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
