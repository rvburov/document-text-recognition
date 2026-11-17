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

    public static double MinConfidence(IOcrEngine engine) => engine switch
    {
        TesseractEngine => 0.30,
        PaddleEngine => 0.5,
        _ => 0.5,
    };

    /// <summary>Настоящая пара: Tesseract и PaddleOCR из папки программы.</summary>
    public static OcrPair CreateReal(string tessdata) =>
        new(new TesseractEngine(tessdata), new PaddleEngine(OcrComponents.PaddleDir));

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
        Action<RecognitionDiagnostic>? diagnostic = null)
    {
        PdfDocument pdf;
        try { pdf = PdfDocument.Open(path); }
        catch (PdfOpenException ex)
        {
            diagnostic?.Invoke(new("Открытие PDF", 0, ex.Message));
            return DocumentResult.Fail(ex.Message);
        }

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
                    size = pdf.GetPageSize(i);
                    using var render = pdf.RenderPage(i, PageFeatures.LongSide);
                    var gray = PageImage.ToGray(render);
                    features = PageFeatures.Build(gray, gray.Width / size.Width);
                }
                catch (PdfOpenException ex) { return DocumentResult.Fail($"стр. {i + 1}: {ex.Message}"); }

                using (features)
                {
                    var outcome = TemplateMatcher.Match(features, size, templates);
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
                    var (values, problem) = ReadFields(pdf, i, size, features, match, ocr, ct, diagnostic);
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
        Action<RecognitionDiagnostic>? diagnostic = null)
    {
        var frames = match.Template.Frames;
        var combined = frames.FirstOrDefault(f => f.Role == FieldRole.Combined);
        EngineFields first, second;
        if (combined is not null)
        {
            using var crop = FieldCrop.Extract(pdf, pageIndex, size, features, match, combined.Inner);
            ct.ThrowIfCancellationRequested();
            var a = ocr.First.Read(crop);
            ct.ThrowIfCancellationRequested();
            var b = ocr.Second.Read(crop);
            first = ParseCombined(ocr.First, a);
            second = ParseCombined(ocr.Second, b);
            if (diagnostic is not null)
                diagnostic(new("Распознавание", pageIndex + 1, "Все поля вместе", new FieldDiagnostic(
                    combined.Role, combined.Inner, crop.ToBytes(".png"),
                    DescribeCombined(a, first), DescribeCombined(b, second))));
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
            var frame = frames.First(f => f.Role == role);
            using var crop = FieldCrop.Extract(pdf, pageIndex, size, features, match, frame.Inner);
            ct.ThrowIfCancellationRequested();
            var a = ocr.First.Read(crop);
            var parsedA = parse(a, OcrPair.MinConfidence(ocr.First));
            ct.ThrowIfCancellationRequested();
            var b = ocr.Second.Read(crop);
            var parsedB = parse(b, OcrPair.MinConfidence(ocr.Second));
            if (diagnostic is not null)
                diagnostic(new("Распознавание", pageIndex + 1, FieldName(role), new FieldDiagnostic(
                    role, frame.Inner, crop.ToBytes(".png"),
                    new EngineDiagnostic(a.Engine, a.Text, parsedA.Value ?? "", parsedA.Problem),
                    new EngineDiagnostic(b.Engine, b.Text, parsedB.Value ?? "", parsedB.Problem))));
            return (parsedA, parsedB);
        }
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
