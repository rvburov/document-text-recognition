using System.Security.Cryptography;
using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Pdf;
using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Core.Templates;
using DocumentTextRecognition.Tests.Samples;
using OpenCvSharp;
using Xunit.Abstractions;

namespace DocumentTextRecognition.Tests.Ocr;

/// <summary>
/// Сохраненные области и настоящий векторный PDF проходят весь DocumentReader.
/// Положительные OCR-примеры используют Golos Text из репозитория и реальный Paddle; это не
/// утверждение о точности GOST. Управляемые движки отдельно проверяют маршрутизацию.
/// Истина задана константами до генерации; имена PDF и текст шаблона другие.
/// </summary>
[Collection("OCR")]
public sealed class PdfTextReaderTests(OcrFixture ocr, ITestOutputHelper output)
{
    private const string Designation = "ЮИЛГ.461411.123";
    private const string Name = "Корпус";
    private static readonly FieldValues Expected = new(Designation, Name, DocTypes.WhenMissing);

    [OcrTheory]
    [InlineData(0, false)]
    [InlineData(90, true)]
    [InlineData(180, false)]
    [InlineData(270, true)]
    public void Saved_regions_read_visible_vector_text_confirmed_by_real_Paddle_after_rotation_and_cropbox(
        int rotation, bool cropOffset)
    {
        using var wb = new Workbench();
        var crop = Crop(cropOffset);
        using var template = Template(wb, crop);
        var path = Create(wb.Source, [Page(Designation, Name, rotation, crop)]);
        var before = Hash(path);
        var unusedFirst = new NeverRead();
        // The shared fixture owns the real Paddle engine, not this lightweight pair.
        var pair = new OcrPair(unusedFirst, ocr.Pair.Second) { UsePdfText = true };
        var diagnostics = new List<RecognitionDiagnostic>();

        Cv2.SetTheRNG(1729UL);
        var result = DocumentReader.Read(path, [template], pair, CancellationToken.None, diagnostics.Add);

        output.WriteLine($"rotation={rotation}, crop={cropOffset}: {result.Values?.ToString() ?? result.Problem}");
        Assert.Equal(Expected, result.Values);
        Assert.Null(result.Problem);
        Assert.Equal(0, unusedFirst.Calls);
        var trace = Assert.IsType<RecognitionProvenance>(result.Trace);
        Assert.Equal("pdf-text-confirmed", trace.SourcePath);
        Assert.Equal(1, trace.PdfTextAttempts);
        Assert.Equal(1, trace.PdfTextAccepted);
        Assert.Equal(1, trace.MatchedPages);
        Assert.Equal(0, trace.TesseractReads);
        Assert.True(trace.PaddleReads >= 2);
        var fields = ConfirmedFields(diagnostics);
        Assert.Equal(2, fields.Length);
        foreach (var field in fields)
        {
            Assert.Equal(template.Frames.Single(f => f.Role == field.Role).Inner, field.Bounds);
            Assert.Equal("Текст PDF", field.First.Engine);
            Assert.Null(field.First.MinConfidence); // Text metadata is not a fabricated OCR confidence.
            Assert.Equal(ocr.Pair.Second.Name, field.Second.Engine);
            Assert.True(field.Second.MinConfidence >= OcrPair.MinConfidence(ocr.Pair.Second));
            Assert.Equal(field.First.ParsedText, field.Second.ParsedText);
            using var raster = Cv2.ImDecode(field.CropPng, ImreadModes.Grayscale);
            Assert.False(raster.Empty());
        }
        Assert.Equal(before, Hash(path));
    }

    [OcrFact]
    public void Controlled_peer_confirms_pdf_candidate_without_calling_the_first_engine()
    {
        using var wb = new Workbench();
        using var template = Template(wb);
        var path = Create(wb.Source, [Page(Designation, Name)]);
        var first = new NeverRead();
        var peer = new Answers(Designation, Name);
        using var pair = new OcrPair(first, peer) { UsePdfText = true };

        var result = DocumentReader.Read(path, [template], pair, CancellationToken.None);

        Assert.Equal(Expected, result.Values);
        Assert.Equal(0, first.Calls);
        Assert.Equal(2, peer.Calls);
        Assert.Equal(1, result.Trace!.PdfTextAccepted);
        Assert.Equal("pdf-text-confirmed", result.Trace.SourcePath);
        Assert.Equal(0, result.Trace.PaddleReads); // This case is control flow, not real OCR evidence.
    }

    [OcrFact]
    public void Real_Tesseract_confirms_pdf_when_the_controlled_primary_peer_is_below_threshold()
    {
        using var wb = new Workbench();
        using var template = Template(wb);
        var path = Create(wb.Source, [Page(Designation, Name)]);
        var before = Hash(path);
        var primary = new Answers("ЮИЛГ.461411.999", "Кронштейн") { Score = .49 };
        Assert.True(primary.Score < OcrPair.MinConfidence(primary));
        // The fixture owns Tesseract. Only the primary peer is controlled in this test.
        var pair = new OcrPair(ocr.Pair.First, primary) { UsePdfText = true };
        var diagnostics = new List<RecognitionDiagnostic>();
        var timings = new List<(string Stage, TimeSpan Duration)>();

        var result = DocumentReader.Read(path, [template], pair, CancellationToken.None, diagnostics.Add,
            (stage, duration) => timings.Add((stage, duration)));

        Assert.Equal(Expected, result.Values);
        Assert.Null(result.Problem);
        Assert.Equal(2, primary.Calls);
        Assert.Equal(1, result.Trace!.PdfTextAccepted);
        Assert.Equal("pdf-text-confirmed", result.Trace.SourcePath);
        Assert.Equal(2, result.Trace.TesseractReads);
        Assert.Equal(0, result.Trace.PaddleReads); // The controlled primary is not a real Paddle read.
        var additionalTimings = timings.Where(t => t.Stage == "ocr." + ocr.Pair.First.Name).ToArray();
        Assert.Equal(2, additionalTimings.Length);
        Assert.All(additionalTimings, t => Assert.True(t.Duration > TimeSpan.Zero));
        var additional = diagnostics.Where(d => d.Stage == "Дополнительная сверка текста PDF")
            .Select(d => Assert.IsType<FieldDiagnostic>(d.Field)).ToArray();
        Assert.Equal(2, additional.Length);
        foreach (var field in additional)
        {
            Assert.Equal(ocr.Pair.First.Name, field.Second.Engine);
            Assert.Null(field.First.MinConfidence);
            Assert.Null(field.Second.Problem);
            Assert.True(field.Second.MinConfidence >= OcrPair.MinConfidence(ocr.Pair.First));
            Assert.Equal(field.First.ParsedText, field.Second.ParsedText);
        }
        Assert.DoesNotContain(diagnostics, d => d.Stage == "Распознавание");
        Assert.Equal(before, Hash(path));
    }

    [OcrFact]
    public void Combined_pdf_confirmation_exposes_the_additional_peers_raw_text_confidence_and_timing()
    {
        using var wb = new Workbench();
        var bounds = new NormRect(220d / 595, (842d - 175) / 842, 345d / 595, 90d / 842);
        using var template = Template(wb, typeAbsent: false, regions: [new(FieldRole.Combined, bounds)]);
        var path = Create(wb.Source, [Page(Designation, Name)]);
        var exact = Designation + "\n" + Name;
        var additionalPeer = new Answers(exact) { Label = "Контроль дополнительного движка" };
        var primary = new Answers("ЮИЛГ.461411.999\nКронштейн")
            { Label = "Контроль основного движка", Score = .49 };
        using var pair = new OcrPair(additionalPeer, primary) { UsePdfText = true };
        var diagnostics = new List<RecognitionDiagnostic>();
        var timings = new List<(string Stage, TimeSpan Duration)>();

        var result = DocumentReader.Read(path, [template], pair, CancellationToken.None, diagnostics.Add,
            (stage, duration) => timings.Add((stage, duration)));

        Assert.Equal(Expected, result.Values);
        Assert.Equal(1, result.Trace!.PdfTextAccepted);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(1, additionalPeer.Calls);
        Assert.Equal(primary.CropHashes, additionalPeer.CropHashes);
        var additional = Assert.Single(diagnostics, d => d.Stage == "Дополнительная сверка текста PDF").Field!;
        Assert.Equal(FieldRole.Combined, additional.Role);
        Assert.Equal(bounds, additional.Bounds);
        Assert.Equal(exact, additional.First.RawText);
        Assert.Null(additional.First.MinConfidence);
        Assert.Equal(additionalPeer.Name, additional.Second.Engine);
        Assert.Equal(exact, additional.Second.RawText);
        Assert.Equal(additionalPeer.Score, additional.Second.MinConfidence);
        Assert.Equal(OcrPair.MinConfidence(additionalPeer), additional.Second.RequiredConfidence);
        Assert.Null(additional.Second.Problem);
        Assert.Equal(primary.CropHashes[0], Convert.ToHexString(SHA256.HashData(additional.CropPng)));
        Assert.Contains(timings, t => t.Stage == "ocr." + additionalPeer.Name && t.Duration > TimeSpan.Zero);
        Assert.DoesNotContain(diagnostics, d => d.Stage == "Распознавание");
    }

    [OcrFact]
    public void Different_fields_confirmed_by_different_controlled_peers_do_not_confirm_the_whole_pdf()
    {
        using var wb = new Workbench();
        using var template = Template(wb);
        var path = Create(wb.Source, [Page(Designation, Name)]);
        var first = new Answers(Designation, "Кронштейн", Designation, "Кронштейн");
        var second = new Answers("ЮИЛГ.461411.999", Name, "ЮИЛГ.461411.999", Name);
        using var pair = new OcrPair(first, second) { UsePdfText = true };
        var diagnostics = new List<RecognitionDiagnostic>();

        var result = DocumentReader.Read(path, [template], pair, CancellationToken.None, diagnostics.Add);

        Assert.Null(result.Values);
        Assert.False(string.IsNullOrWhiteSpace(result.Problem));
        Assert.Equal(1, result.Trace!.PdfTextAttempts);
        Assert.Equal(0, result.Trace.PdfTextAccepted);
        Assert.NotEqual("pdf-text-confirmed", result.Trace.SourcePath);
        Assert.Equal(4, first.Calls);
        Assert.Equal(4, second.Calls);
        Assert.Equal(2, diagnostics.Count(d => d.Stage == "Дополнительная сверка текста PDF"));
        Assert.Contains(diagnostics, d => d.Stage == "Сверка источников" && d.Message.Contains("обычная проверка OCR"));
    }

    [OcrTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Matching_text_below_its_confidence_threshold_cannot_confirm_pdf_on_either_peer(bool matchingFirst)
    {
        using var wb = new Workbench();
        using var template = Template(wb);
        var path = Create(wb.Source, [Page(Designation, Name)]);
        var matching = new Answers(Designation, Name, Designation, Name) { Score = .49 };
        var wrong = new Answers("ЮИЛГ.461411.999", Name, "ЮИЛГ.461411.999", Name);
        using var pair = new OcrPair(matchingFirst ? matching : wrong, matchingFirst ? wrong : matching)
            { UsePdfText = true };
        Assert.True(matching.Score < OcrPair.MinConfidence(matching));
        var diagnostics = new List<RecognitionDiagnostic>();

        var result = DocumentReader.Read(path, [template], pair, CancellationToken.None, diagnostics.Add);

        Assert.Null(result.Values);
        Assert.Contains("низкая уверенность", result.Problem);
        Assert.Equal(1, result.Trace!.PdfTextAttempts);
        Assert.Equal(0, result.Trace.PdfTextAccepted);
        Assert.Equal(4, matching.Calls);
        Assert.Equal(4, wrong.Calls);
        var matchingStage = matchingFirst ? "Дополнительная сверка текста PDF" : "Подтверждение текста PDF";
        var designation = Assert.Single(diagnostics, d => d.Stage == matchingStage && d.Field?.Role == FieldRole.Designation).Field!;
        Assert.Equal(Designation, designation.Second.RawText);
        Assert.Equal(matching.Score, designation.Second.MinConfidence);
        Assert.Contains("низкая уверенность", designation.Second.Problem);
    }

    [OcrFact]
    public void Two_controlled_raster_readings_against_false_cmap_are_reused_without_accepting_pdf_text()
    {
        const string visibleDesignation = "УИЕС.468732.271";
        using var wb = new Workbench();
        using var template = Template(wb);
        var path = Create(wb.Source, [Page(visibleDesignation, Name)], new Dictionary<char, string> { ['У'] = "Ч" });
        var before = Hash(path);
        var first = new Answers(visibleDesignation, Name);
        var second = new Answers(visibleDesignation, Name);
        using var pair = new OcrPair(first, second) { UsePdfText = true };
        var diagnostics = new List<RecognitionDiagnostic>();

        var result = DocumentReader.Read(path, [template], pair, CancellationToken.None, diagnostics.Add);

        Assert.Equal(new FieldValues(visibleDesignation, Name, DocTypes.WhenMissing), result.Values);
        Assert.Equal(0, result.Trace!.PdfTextAccepted);
        Assert.Equal(1, result.Trace.PdfTextAttempts);
        Assert.NotEqual("pdf-text-confirmed", result.Trace.SourcePath);
        Assert.Equal(2, first.Calls); // A second pass would exhaust the controlled readings.
        Assert.Equal(2, second.Calls);
        Assert.Equal(first.CropHashes, second.CropHashes);
        Assert.Contains(diagnostics, d => d.Stage == "Подтверждение текста PDF" &&
            d.Field?.Role == FieldRole.Designation && d.Field.First.RawText == "ЧИЕС.468732.271");
        Assert.Contains(diagnostics, d => d.Stage == "Сверка источников" &&
            d.Message.Contains("Текст PDF не подтверждён") && d.Message.Contains("двумя распознавателями изображения"));
        Assert.DoesNotContain(diagnostics, d => d.Stage == "Распознавание");
        Assert.Equal(before, Hash(path));
    }

    [OcrFact]
    public void Plausible_false_cmap_cannot_override_the_independent_visible_glyphs()
    {
        const string visibleDesignation = "УИЕС.468732.271";
        using var wb = new Workbench();
        using var template = Template(wb);
        var path = Create(wb.Source, [Page(visibleDesignation, Name)],
            new Dictionary<char, string> { ['У'] = "Ч" });
        var before = Hash(path);
        using (var pdf = PdfDocument.Open(path))
        {
            var candidate = pdf.ReadTextPage(0).Select(template.Frames[0].Inner);
            Assert.True(candidate.CanUseCandidate, candidate.Problem);
            Assert.Equal("ЧИЕС.468732.271", candidate.Text);
        }
        var diagnostics = new List<RecognitionDiagnostic>();

        var result = DocumentReader.Read(path, [template], ocr.Pair, CancellationToken.None, diagnostics.Add);

        output.WriteLine("False CMap result: " + (result.Values?.ToString() ?? result.Problem));
        Assert.Equal(0, result.Trace!.PdfTextAccepted);
        Assert.Equal(1 + result.Trace.GostAttempts, result.Trace.PdfTextAttempts);
        Assert.True(result.Trace.PaddleReads > 0);
        Assert.True(result.Trace.TesseractReads > 0);
        if (result.Values is { } values)
        {
            Assert.Equal(new FieldValues(visibleDesignation, Name, DocTypes.WhenMissing), values);
            Assert.Equal("ocr", result.Trace.SourcePath);
            Assert.Contains(diagnostics, d => d.Stage == "Сверка источников" &&
                d.Message.Contains("Текст PDF не подтверждён") && d.Message.Contains("двумя распознавателями изображения"));
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(result.Problem));
            Assert.Contains(diagnostics, d => d.Stage == "Сверка источников" && d.Message.Contains("обычная проверка OCR"));
        }
        Assert.Equal(before, Hash(path));
    }

    [OcrTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Hidden_layer_or_partial_character_falls_back_before_any_pdf_peer_read(bool partialCharacter)
    {
        using var wb = new Workbench();
        var page = Page(Designation, Name);
        if (!partialCharacter)
            page = page with { Lines = [.. page.Lines, new("Ложное", 230, 100, 18, RenderMode: 3)] };
        var path = Create(wb.Source, [page]);
        var regions = Regions(null);
        if (partialCharacter)
        {
            using var pdf = PdfDocument.Open(path);
            var text = pdf.ReadTextPage(0);
            var glyph = text.Select(regions[0].Bounds).Characters.First(c => !c.IsGenerated && !c.IsWhitespace);
            var original = regions[0].Bounds;
            // Граница режет символ текстового слоя на 0,5 мм, а запас вырезки (1 мм) показывает
            // OCR его целиком. Букву, обрезанную больше запаса, проверяет следующий тест.
            var left = (glyph.Bounds.X + Math.Min(glyph.Bounds.Width / 4, 0.5 / 25.4 * 72)) / text.Size.Width;
            regions[0] = new(FieldRole.Designation,
                new(left, original.Y, original.Right - left, original.Height));
        }
        using var template = Template(wb, regions: regions);
        var first = new Answers(Designation, Name);
        var second = new Answers(Designation, Name);
        using var pair = new OcrPair(first, second) { UsePdfText = true };
        var diagnostics = new List<RecognitionDiagnostic>();

        var result = DocumentReader.Read(path, [template], pair, CancellationToken.None, diagnostics.Add);

        Assert.True(result.Values is not null, result.Problem);
        Assert.Equal(Expected, result.Values);
        Assert.Equal(1, result.Trace!.PdfTextAttempts);
        Assert.Equal(0, result.Trace.PdfTextAccepted);
        Assert.Equal(2, first.Calls);
        Assert.Equal(2, second.Calls); // No extra peer read before normal two-engine OCR.
        Assert.Empty(ConfirmedFields(diagnostics));
        Assert.Contains(diagnostics, d => d.Stage == "Текст PDF" && d.Message.Contains("OCR"));
        Assert.Equal(2, diagnostics.Count(d => d.Stage == "Распознавание" && d.Field is not null));
    }

    [OcrFact]
    public void Region_cutting_a_letter_beyond_the_crop_margin_is_refused_before_any_engine_read()
    {
        using var wb = new Workbench();
        var path = Create(wb.Source, [Page(Designation, Name)]);
        var regions = Regions(null);
        using (var pdf = PdfDocument.Open(path))
        {
            var text = pdf.ReadTextPage(0);
            var glyph = text.Select(regions[0].Bounds).Characters.Last(c => !c.IsGenerated && !c.IsWhitespace);
            var original = regions[0].Bounds;
            // Правая граница проходит по середине последней буквы обозначения: за запасом
            // вырезки остается ее половина, и оба движка прочитали бы обрубок одинаково.
            var right = (glyph.Bounds.X + glyph.Bounds.Width / 2) / text.Size.Width;
            regions[0] = new(FieldRole.Designation, new(original.X, original.Y, right - original.X, original.Height));
        }
        using var template = Template(wb, regions: regions);
        var first = new Answers(Designation, Name);
        var second = new Answers(Designation, Name);
        using var pair = new OcrPair(first, second) { UsePdfText = true };

        var result = DocumentReader.Read(path, [template], pair, CancellationToken.None);

        Assert.Null(result.Values);
        Assert.Contains("Обозначение: текст выходит за область поля", result.Problem);
        Assert.Equal(0, first.Calls);
        Assert.Equal(0, second.Calls);
    }

    [OcrFact]
    public void Independently_confirmed_but_different_pages_refuse_the_whole_document()
    {
        using var wb = new Workbench();
        using var template = Template(wb);
        var path = Create(wb.Source, [Page(Designation, Name), Page("ЮИЛГ.461411.124", Name)]);
        var unusedFirst = new NeverRead();
        var pair = new OcrPair(unusedFirst, ocr.Pair.Second) { UsePdfText = true };

        var result = DocumentReader.Read(path, [template], pair, CancellationToken.None);

        Assert.Null(result.Values);
        Assert.Contains("страницы дают разные данные", result.Problem);
        Assert.Equal(0, unusedFirst.Calls);
        Assert.Equal(2, result.Trace!.MatchedPages);
        Assert.Equal(2, result.Trace.PdfTextAccepted);
        Assert.True(result.Trace.PaddleReads >= 4);
    }

    [OcrFact]
    public void Empty_type_is_confirmed_by_full_Paddle_detector_before_using_specification()
    {
        using var wb = new Workbench();
        using var template = Template(wb, typeAbsent: false);
        var path = Create(wb.Source, [Page(Designation, Name)]);
        var unusedFirst = new NeverRead();
        var pair = new OcrPair(unusedFirst, ocr.Pair.Second) { UsePdfText = true, PreferKnownLines = true };
        var diagnostics = new List<RecognitionDiagnostic>();
        var timings = new List<string>();

        var result = DocumentReader.Read(path, [template], pair, CancellationToken.None,
            diagnostics.Add, (stage, _) => timings.Add(stage));

        Assert.Equal(Expected, result.Values);
        Assert.Equal(1, result.Trace!.PdfTextAccepted);
        Assert.Equal(0, unusedFirst.Calls);
        Assert.True(result.Trace.PaddleReads >= 3);
        var type = Assert.Single(ConfirmedFields(diagnostics), d => d.Role == FieldRole.Type);
        Assert.Empty(type.First.RawText);
        Assert.Empty(type.Second.RawText);
        using var blank = Cv2.ImDecode(type.CropPng, ImreadModes.Grayscale);
        Assert.False(KnownSingleLineRegion.TryGetBounds(blank, out _));
        Assert.Contains("paddle.detect", timings);
    }

    private static FieldDiagnostic[] ConfirmedFields(IEnumerable<RecognitionDiagnostic> diagnostics) => diagnostics
        .Where(d => d.Stage == "Подтверждение текста PDF" && d.Field is not null).Select(d => d.Field!).ToArray();

    private static PdfTextFixtures.Box? Crop(bool enabled) => enabled ? new(10, 15, 585, 827) : null;

    private static PdfTextFixtures.Page Page(string designation, string name, int rotation = 0,
        PdfTextFixtures.Box? crop = null) => new(
        [new(designation, 230, 150, 18), new(name, 230, 100, 18)],
        Width: 595, Height: 842, Rotation: rotation, Crop: crop, RegistrationGrid: true);

    private static string Create(string folder, IReadOnlyList<PdfTextFixtures.Page> pages,
        IReadOnlyDictionary<char, string>? overrides = null, string stem = "scan") =>
        PdfTextFixtures.Create(folder, stem, pages, unicodeOverrides: overrides, fontFile: StandardFont());

    private static string StandardFont()
    {
        const string fontName = "GolosText-Regular.ttf";
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Ui", "Assets", "Fonts", fontName);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Для векторного образца нужен Golos Text из репозитория; подмена шрифта запрещена.", fontName);
    }

    private static TemplateRegion[] Regions(PdfTextFixtures.Box? crop, bool typeAbsent = true)
    {
        var box = crop ?? new PdfTextFixtures.Box(0, 0, 595, 842);
        var width = box.Right - box.Left;
        var height = box.Top - box.Bottom;
        NormRect Rect(double nativeBottom) => new((220 - box.Left) / width,
            (box.Top - nativeBottom - 40) / height, 345 / width, 40 / height);
        var regions = new List<TemplateRegion>
        {
            new(FieldRole.Designation, Rect(135)), new(FieldRole.Name, Rect(85)),
        };
        if (!typeAbsent) regions.Add(new(FieldRole.Type, Rect(35)));
        return regions.ToArray();
    }

    private static DocTemplate Template(Workbench wb, PdfTextFixtures.Box? crop = null,
        bool typeAbsent = true, TemplateRegion[]? regions = null)
    {
        var path = Create(wb.Templates, [Page("ОБРЗ.000000.000", "Образец", crop: crop)], stem: "template");
        var before = Hash(path);
        regions ??= Regions(crop, typeAbsent);
        Assert.Null(TemplateMarkupStore.Save(path, 0, regions, typeAbsent));
        var saved = TemplateMarkupStore.Load(path);
        Assert.Null(saved.Problem);
        Assert.Equal(regions, saved.Markup!.Regions);
        Assert.Equal(before, Hash(path));
        var loaded = TemplateLibrary.Load(path);
        Assert.NotNull(loaded.Template);
        Assert.True(loaded.Template.IsManual, loaded.Problem);
        return loaded.Template;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class NeverRead : IOcrEngine
    {
        public string Name => "Первый движок (контроль маршрута)";
        public int Calls { get; private set; }
        public OcrReading Read(Mat gray)
        {
            Calls++;
            throw new InvalidOperationException("После подтверждения текста PDF первый OCR не должен вызываться.");
        }
        public void Dispose() { }
    }

    private sealed class Answers(params string[] answers) : IOcrEngine
    {
        public string Label { get; init; } = "Управляемый ответ (не оценка OCR)";
        public string Name => Label;
        public double Score { get; init; } = .99;
        public int Calls { get; private set; }
        public List<string> CropHashes { get; } = [];
        public OcrReading Read(Mat gray)
        {
            Assert.False(gray.Empty());
            Assert.InRange(Calls, 0, answers.Length - 1);
            CropHashes.Add(Convert.ToHexString(SHA256.HashData(gray.ToBytes(".png"))));
            var text = answers[Calls++];
            var lines = text.Split('\n');
            return new(Name, lines.Select((line, index) =>
                new OcrLine(line, Score, 0, index * gray.Height / lines.Length, gray.Width,
                    (index + 1) * gray.Height / lines.Length)).ToArray());
        }
        public void Dispose() { }
    }
}
