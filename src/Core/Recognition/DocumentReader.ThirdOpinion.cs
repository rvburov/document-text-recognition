using System.Diagnostics;
using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Matching;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Pdf;
using OpenCvSharp;

namespace DocumentTextRecognition.Core.Recognition;

internal static partial class DocumentReader
{
    /// <summary>
    /// Третье мнение для значений, принятых обычной парой. На чертежных шрифтах ГОСТ оба
    /// обычных движка одинаково путают некоторые буквы (рукописные «б», «в», «д», «ъ», «т»),
    /// и их согласие не доказывает верность. Поэтому поле, где есть такие буквы, дополнительно
    /// читает модель чертежного шрифта. Если она уверенно видит на месте спорной буквы другую
    /// букву той же группы, а соседние знаки совпадают, файл пропускается: какая буква на
    /// самом деле, по изображению не определить. Иное несогласие третьей модели (мусор,
    /// пропуски) не учитывается: она только останавливает, но не выбирает значение.
    /// Значения, подтвержденные текстом PDF, и значения пары чертежного шрифта не проверяются.
    /// </summary>
    private static (FieldValues? Values, string? Problem) ThirdOpinion(PdfDocument pdf, int pageIndex, PageSize size,
        PageFeatures features, PageMatch match, PairResult accepted, OcrPair ocr, PairResult? standard,
        bool acceptedByGost, CancellationToken ct, Action<RecognitionDiagnostic>? diagnostic,
        Action<string, TimeSpan>? timing, RecognitionTraceBuilder? trace)
    {
        var values = accepted.Values!;
        if (accepted.FromPdfText || acceptedByGost || !ocr.UseThirdOpinion) return (values, null);
        var third = ocr.GostPair()?.First;
        if (third is null) return (values, null);

        var frames = match.Template.Frames;
        var combined = frames.FirstOrDefault(f => f.Role == FieldRole.Combined);
        var checks = combined is not null
            ? [(combined, FieldRole.Combined)]
            : frames.Where(f => f.Role != FieldRole.Type || !match.Template.TypeAbsent)
                .Where(f => Value(values, f.Role).Any(DraftingConfusions.IsConfusable))
                .OrderBy(f => f.Role).Select(f => (f, f.Role)).ToArray();
        if (combined is not null && !(values.Designation + values.Name + values.Type).Any(DraftingConfusions.IsConfusable))
            return (values, null);

        foreach (var (frame, role) in checks)
        {
            ct.ThrowIfCancellationRequested();
            var started = timing is null ? 0 : Stopwatch.GetTimestamp();
            using var crop = FieldCrop.Extract(pdf, pageIndex, size, features, match, frame.Inner, timing);
            timing?.Invoke("crop", Stopwatch.GetElapsedTime(started));
            started = timing is null ? 0 : Stopwatch.GetTimestamp();
            var reading = ReadEngine(third, crop, timing, Profile(role == FieldRole.Combined ? FieldRole.Name : role), trace);
            timing?.Invoke("ocr.third", Stopwatch.GetElapsedTime(started));
            var minimum = OcrPair.MinConfidence(third);
            var opinions = role == FieldRole.Combined
                ? new[] { (FieldRole.Designation, ParseCombined(third, reading).Designation),
                          (FieldRole.Name, ParseCombined(third, reading).Name),
                          (FieldRole.Type, ParseCombined(third, reading).Type) }
                : new[] { (role, Parse(role, reading, minimum)) };
            foreach (var (field, opinion) in opinions)
            {
                var acceptedValue = Value(values, field);
                if (!opinion.IsOk || string.IsNullOrEmpty(opinion.Value)) continue;
                var conflict = DraftingConfusions.Conflict(acceptedValue, opinion.Value);
                diagnostic?.Invoke(new("Третье мнение", pageIndex + 1, FieldName(field),
                    new(field, frame.Inner, crop.ToBytes(".png"),
                        new("Принято парой", acceptedValue, acceptedValue, null),
                        new(third.Name + " (чертежный шрифт)", reading.Text, opinion.Value,
                            conflict is null ? null : "спорная буква", reading.MinConfidence, minimum))));
                if (conflict is not { } c) continue;
                var problem = $"{FieldName(field).ToLowerInvariant()}: модель чертежного шрифта читает «{c.Other}» " +
                    $"вместо «{c.Accepted}» («{Quote(opinion.Value)}» и «{Quote(acceptedValue)}»), буква не подтверждена";
                diagnostic?.Invoke(new("Третье мнение", pageIndex + 1, problem));
                return (null, problem);
            }
        }
        return (values, null);

        static string Quote(string s) => s.Length <= EngineAgreement.QuoteLength ? s : s[..EngineAgreement.QuoteLength] + "…";
    }

    private static string Value(FieldValues values, FieldRole role) => role switch
    {
        FieldRole.Designation => values.Designation,
        FieldRole.Name => values.Name,
        FieldRole.Type => values.Type,
        _ => values.Designation + " " + values.Name + " " + values.Type,
    };
}
