using System.Diagnostics;
using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Matching;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Pdf;
using OpenCvSharp;

namespace DocumentTextRecognition.Core.Recognition;

internal static partial class DocumentReader
{
    private const string PdfTextSource = "Текст PDF";

    private static bool TryReadPdfTextFields(PdfDocument pdf, int pageIndex, PageFeatures page,
        PageMatch match, OcrPair ocr, CancellationToken ct, Action<RecognitionDiagnostic>? diagnostic,
        Action<string, TimeSpan>? timing, RecognitionTraceBuilder? trace, out FieldValues? values)
    {
        values = null;
        if (trace is not null) trace.PdfTextAttempts++;
        ct.ThrowIfCancellationRequested();
        var started = timing is null ? 0 : Stopwatch.GetTimestamp();
        var text = pdf.ReadTextPage(pageIndex, ct);
        timing?.Invoke("pdf.text", Stopwatch.GetElapsedTime(started));
        if (text.Problem is not null || text.Characters.Count == 0)
        {
            diagnostic?.Invoke(new("Текст PDF", pageIndex + 1,
                text.Problem ?? "Встроенный текст отсутствует; используется распознавание изображения."));
            return false;
        }

        var combined = match.Template.Frames.FirstOrDefault(f => f.Role == FieldRole.Combined);
        var frames = combined is not null ? new[] { combined } : match.Template.Frames
            .Where(f => f.Role != FieldRole.Type || !match.Template.TypeAbsent)
            .OrderBy(f => f.Role).ToArray();
        var selections = frames.Select(f => text.Select(TextRegion(f.Inner, page, match))).ToArray();
        for (var i = 0; i < selections.Length; i++)
        {
            var selection = selections[i];
            if (selection.Status == PdfTextStatus.Untrusted ||
                selection.Status == PdfTextStatus.Empty && frames[i].Role != FieldRole.Type)
            {
                diagnostic?.Invoke(new("Текст PDF", pageIndex + 1,
                    $"{FieldName(frames[i].Role)}: {selection.Problem ?? "в области нет пригодного текста"}; используется OCR."));
                return false;
            }
        }

        EngineFields pdfFields;
        Parsed[] pdfParsed = [];
        if (combined is not null)
        {
            var parsed = FieldParser.ParseCombinedText(selections[0].Lines.Select(l => l.Text).ToArray());
            pdfFields = new(PdfTextSource, parsed.Designation, parsed.Name, parsed.Type);
        }
        else
        {
            pdfParsed = selections.Select((s, i) => ParsePdfText(frames[i].Role, s)).ToArray();
            Parsed Get(FieldRole role) => Array.FindIndex(frames, f => f.Role == role) is var index && index >= 0
                ? pdfParsed[index] : Parsed.Ok("");
            pdfFields = new(PdfTextSource, Get(FieldRole.Designation), Get(FieldRole.Name), Get(FieldRole.Type));
        }
        if (!pdfFields.Designation.IsOk || !pdfFields.Name.IsOk || !pdfFields.Type.IsOk)
        {
            diagnostic?.Invoke(new("Текст PDF", pageIndex + 1,
                "Текст не прошёл проверку полей: " + JoinProblems(pdfFields.Designation.Problem,
                    pdfFields.Name.Problem, pdfFields.Type.Problem) + "; используется OCR."));
            return false;
        }

        // Require independent visual confirmation. Encoded alphabet remains authoritative
        // for visually identical name/type homoglyphs; pixels cannot prove their Unicode.
        var peer = ocr.Second is PaddleEngine ? ocr.Second : ocr.First is PaddleEngine ? ocr.First : ocr.Second;
        var crops = new List<Mat>();
        try
        {
            foreach (var frame in frames)
            {
                ct.ThrowIfCancellationRequested();
                started = timing is null ? 0 : Stopwatch.GetTimestamp();
                crops.Add(FieldCrop.Extract(pdf, pageIndex, text.Size, page, match, frame.Inner, timing));
                timing?.Invoke("crop", Stopwatch.GetElapsedTime(started));
            }
            OcrReading[] readings;
            if (combined is not null)
                readings = [ReadPeer(peer, crops[0], OcrTextProfile.General)];
            else if (peer is PaddleEngine paddle)
            {
                var profiles = frames.Select(f => Profile(f.Role)).ToArray();
                CountRead(peer, crops.Count, trace);
                started = timing is null ? 0 : Stopwatch.GetTimestamp();
                if (ocr.PreferKnownLines)
                {
                    var direct = paddle.ReadKnownLines(crops, timing, ct, profiles);
                    readings = direct.Readings;
                    var failed = Enumerable.Range(0, frames.Length).Where(i => direct.KnownLines[i] &&
                        !IsPdfKnownLineConfirmed(frames[i].Role, readings[i], OcrPair.MinConfidence(peer), pdfParsed[i])).ToArray();
                    if (failed.Length > 0)
                    {
                        CountRead(peer, failed.Length, trace);
                        var fallback = paddle.ReadMany(failed.Select(i => crops[i]).ToArray(), timing, ct,
                            failed.Select(i => profiles[i]).ToArray());
                        for (var i = 0; i < failed.Length; i++) readings[failed[i]] = fallback[i];
                    }
                }
                else readings = paddle.ReadMany(crops, timing, ct, profiles);
                timing?.Invoke("ocr." + peer.Name, Stopwatch.GetElapsedTime(started));
            }
            else readings = crops.Select((crop, i) => ReadPeer(peer, crop, Profile(frames[i].Role))).ToArray();
            ct.ThrowIfCancellationRequested();

            EngineFields rasterFields;
            if (combined is not null)
            {
                rasterFields = ParseCombined(peer, readings[0]);
                diagnostic?.Invoke(new("Подтверждение текста PDF", pageIndex + 1, FieldName(FieldRole.Combined),
                    new(FieldRole.Combined, combined.Inner, crops[0].ToBytes(".png"),
                        new(PdfTextSource, selections[0].Text,
                            $"Обозначение: {pdfFields.Designation.Value}\nНаименование: {pdfFields.Name.Value}\nТип: {pdfFields.Type.Value}", null),
                        DescribeCombined(readings[0], rasterFields, peer))));
            }
            else
            {
                var parsed = readings.Select((r, i) => Parse(frames[i].Role, r, OcrPair.MinConfidence(peer))).ToArray();
                Parsed Get(FieldRole role) => Array.FindIndex(frames, f => f.Role == role) is var index && index >= 0
                    ? parsed[index] : Parsed.Ok("");
                rasterFields = new(peer.Name, Get(FieldRole.Designation), Get(FieldRole.Name), Get(FieldRole.Type));
                for (var i = 0; i < frames.Length; i++)
                    diagnostic?.Invoke(new("Подтверждение текста PDF", pageIndex + 1, FieldName(frames[i].Role),
                        new(frames[i].Role, frames[i].Inner, crops[i].ToBytes(".png"),
                            new(PdfTextSource, selections[i].Text, pdfParsed[i].Value ?? "", pdfParsed[i].Problem),
                            new(peer.Name, readings[i].Text, parsed[i].Value ?? "", parsed[i].Problem,
                                readings[i].MinConfidence, OcrPair.MinConfidence(peer)))));
            }
            var agreement = EngineAgreement.CombinePdfText(pdfFields, rasterFields);
            if (agreement.Values is null)
            {
                // A second independent raster algorithm may confirm the encoded PDF.
                // Its real confidence and exact field agreement are still mandatory.
                var other = ReferenceEquals(peer, ocr.First) ? ocr.Second : ocr.First;
                var otherReadings = crops.Select((crop, i) =>
                {
                    ct.ThrowIfCancellationRequested();
                    return ReadPeer(other, crop, combined is null ? Profile(frames[i].Role) : OcrTextProfile.General);
                }).ToArray();
                ct.ThrowIfCancellationRequested();
                EngineFields otherFields;
                if (combined is not null)
                {
                    otherFields = ParseCombined(other, otherReadings[0]);
                    diagnostic?.Invoke(new("Дополнительная сверка текста PDF", pageIndex + 1, FieldName(FieldRole.Combined),
                        new(FieldRole.Combined, combined.Inner, crops[0].ToBytes(".png"),
                            new(PdfTextSource, selections[0].Text,
                                $"Обозначение: {pdfFields.Designation.Value}\nНаименование: {pdfFields.Name.Value}\nТип: {pdfFields.Type.Value}", null),
                            DescribeCombined(otherReadings[0], otherFields, other))));
                }
                else
                {
                    var parsed = otherReadings.Select((r, i) => Parse(frames[i].Role, r, OcrPair.MinConfidence(other))).ToArray();
                    Parsed Get(FieldRole role) => Array.FindIndex(frames, f => f.Role == role) is var index && index >= 0
                        ? parsed[index] : Parsed.Ok("");
                    otherFields = new(other.Name, Get(FieldRole.Designation), Get(FieldRole.Name), Get(FieldRole.Type));
                    for (var i = 0; i < frames.Length; i++)
                        diagnostic?.Invoke(new("Дополнительная сверка текста PDF", pageIndex + 1, FieldName(frames[i].Role),
                            new(frames[i].Role, frames[i].Inner, crops[i].ToBytes(".png"),
                                new(PdfTextSource, selections[i].Text, pdfParsed[i].Value ?? "", pdfParsed[i].Problem),
                                new(other.Name, otherReadings[i].Text, parsed[i].Value ?? "", parsed[i].Problem,
                                    otherReadings[i].MinConfidence, OcrPair.MinConfidence(other)))));
                }
                if (!EngineAgreement.HasPdfTextConflict(pdfFields, rasterFields))
                    agreement = EngineAgreement.CombinePdfText(pdfFields, otherFields);
                if (agreement.Values is null)
                {
                    // Both OCRs have already examined these exact pixels. When they
                    // agree against a false text layer, use their result as OCR, not PDF.
                    var imageAgreement = ReferenceEquals(peer, ocr.First)
                        ? EngineAgreement.Combine(rasterFields, otherFields) : EngineAgreement.Combine(otherFields, rasterFields);
                    if (imageAgreement.Values is not null)
                    {
                        diagnostic?.Invoke(new("Сверка источников", pageIndex + 1,
                            "Текст PDF не подтверждён; данные подтверждены двумя распознавателями изображения."));
                        values = imageAgreement.Values;
                        return true;
                    }
                }
            }
            diagnostic?.Invoke(new("Сверка источников", pageIndex + 1, agreement.Problem is null
                ? "Текст PDF подтверждён независимым распознаванием изображения."
                : agreement.Problem + "; выполняется обычная проверка OCR."));
            if (agreement.Values is null) return false;
            if (trace is not null) trace.PdfTextAccepted++;
            values = agreement.Values;
            return true;
        }
        finally { foreach (var crop in crops) crop.Dispose(); }

        OcrReading ReadPeer(IOcrEngine engine, Mat crop, OcrTextProfile profile)
        {
            var readStarted = timing is null ? 0 : Stopwatch.GetTimestamp();
            var reading = ReadEngine(engine, crop, timing, profile, trace);
            timing?.Invoke("ocr." + engine.Name, Stopwatch.GetElapsedTime(readStarted));
            return reading;
        }
    }

    private static bool IsPdfKnownLineConfirmed(FieldRole role, OcrReading reading, double minimum, Parsed pdf)
    {
        if (reading.IsEmpty || !double.IsFinite(reading.MinConfidence) || reading.MinConfidence < minimum) return false;
        var parsed = Parse(role, reading, minimum);
        return !string.IsNullOrEmpty(parsed.Value) && EngineAgreement.AreConfirmed(pdf, parsed);
    }

    private static Parsed ParsePdfText(FieldRole role, PdfTextSelection selection)
    {
        var lines = selection.Lines.Select(l => l.Text).ToArray();
        return role switch
        {
            FieldRole.Designation => FieldParser.ParseDesignationText(selection.Text),
            FieldRole.Name => FieldParser.CheckName(lines),
            FieldRole.Type => FieldParser.CheckType(lines),
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };
    }

    internal static PdfTextRegion TextRegion(NormRect field, PageFeatures page, PageMatch match)
    {
        var template = match.Template.Features;
        var rect = field.ToPixels(template.Width, template.Height);
        var m = match.Transform;
        var det = m[0] * m[4] - m[1] * m[3];
        double ia = m[4] / det, ib = -m[1] / det, id = -m[3] / det, ie = m[0] / det;
        double ic = -(ia * m[2] + ib * m[5]), iff = -(id * m[2] + ie * m[5]);
        PdfTextPoint ToPoints(double x, double y) => new((ia * x + ib * y + ic) / page.Scale,
            (id * x + ie * y + iff) / page.Scale);
        return new(ToPoints(rect.X, rect.Y), ToPoints(rect.Right, rect.Y), ToPoints(rect.X, rect.Bottom));
    }
}
