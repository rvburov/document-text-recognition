using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Core.Templates;
using DocumentTextRecognition.Tests.Samples;
using OpenCvSharp;
using Sdcb.PaddleOCR;
using Xunit.Abstractions;

namespace DocumentTextRecognition.Tests.Ocr;

/// <summary>Путь известной строки: геометрия, строгая сверка и возврат
/// к полному Paddle только для неподтвержденных полей. Эталонные значения заданы до OCR.</summary>
[Collection("OCR")]
public sealed class KnownLineTests(OcrFixture ocr, ITestOutputHelper output)
{
    private static readonly (string Sample, FieldRole Role, string Expected)[] Crops =
    [
        ("01-specification-a4", FieldRole.Designation, "ВКЕМ.461411.200"),
        ("01-specification-a4", FieldRole.Name, "Модуль связи"),
        ("02-assembly-a4", FieldRole.Designation, "ЮИЛГ.461411.123"),
        ("02-assembly-a4", FieldRole.Name, "Корпус"),
        ("02-assembly-a4", FieldRole.Type, "Сборочный чертеж"),
        ("03-schematic-a4", FieldRole.Designation, "ЮИЛГ.461411.124"),
        ("03-schematic-a4", FieldRole.Name, "Блок питания"),
        ("03-schematic-a4", FieldRole.Type, "Схема электрическая"),
        ("05-screenshot-like-specification", FieldRole.Designation, "АЦИЕ.202132.004"),
        ("05-screenshot-like-specification", FieldRole.Name, "Э(Т)О МОЭА"),
    ];

    public static IEnumerable<object[]> SingleLineCrops => Crops.Select(c => new object[] { c.Sample, c.Role.ToString() });

    [Fact]
    public void Confirmed_single_line_path_is_enabled_by_default()
    {
        using var pair = new OcrPair(new Answers(), new Answers());
        Assert.True(pair.PreferKnownLines);
    }

    [OcrTheory]
    [MemberData(nameof(SingleLineCrops))]
    public void Tracked_single_lines_including_detached_accent_preserve_all_ink(string sample, string roleName)
    {
        var role = Enum.Parse<FieldRole>(roleName);
        using var crop = LoadCrop(sample, role);
        var before = crop.ToBytes(".png");

        Assert.True(KnownSingleLineRegion.TryGetBounds(crop, out var box));
        Assert.Equal(before, crop.ToBytes(".png"));

        Assert.True(box.Width > 0 && box.Height > 0);
        Assert.InRange(box.Left, 0, crop.Width);
        Assert.InRange(box.Top, 0, crop.Height);
        Assert.InRange(box.Right, 1, crop.Width);
        Assert.InRange(box.Bottom, 1, crop.Height);
        using var ink = new Mat();
        Cv2.Threshold(crop, ink, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
        using (var preserved = new Mat(ink, box)) preserved.SetTo(Scalar.All(0));
        Assert.Equal(0, Cv2.CountNonZero(ink)); // Including punctuation, never a selected subset of glyphs.
    }

    [OcrTheory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(10)]
    public void Two_near_touching_bands_are_not_treated_as_one_line(int gap)
    {
        using var crop = LoadCrop("02-assembly-a4", FieldRole.Name);
        using var ink = new Mat();
        Cv2.Threshold(crop, ink, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
        var bounds = Cv2.BoundingRect(ink);
        using var tight = new Mat(crop, bounds);
        using var two = new Mat(tight.Height * 2 + gap + 16, tight.Width + 16, MatType.CV_8UC1, Scalar.All(255));
        using (var top = new Mat(two, new Rect(8, 8, tight.Width, tight.Height))) tight.CopyTo(top);
        using (var bottom = new Mat(two, new Rect(8, 8 + tight.Height + gap, tight.Width, tight.Height))) tight.CopyTo(bottom);

        Assert.False(KnownSingleLineRegion.TryGetBounds(two, out _));
    }

    [OcrTheory]
    [InlineData(16, false, 2)]
    [InlineData(20, false, 2)]
    [InlineData(4, false, 2)]
    [InlineData(5, false, 2)]
    [InlineData(4, true, 2)]
    [InlineData(5, true, 2)]
    [InlineData(4, false, 0)]
    [InlineData(5, false, 0)]
    [InlineData(4, true, 0)]
    [InlineData(5, true, 0)]
    public void Small_secondary_band_cannot_hide_below_main_glyph_height_cutoff(int smallHeight, bool above, int gap)
    {
        const int mainHeight = 40;
        using var image = new Mat(80, 290, MatType.CV_8UC1, Scalar.All(255));
        void Glyph(int x, int y, int width, int height, int stroke)
        {
            Cv2.Rectangle(image, new Rect(x, y, stroke, height), Scalar.All(0), -1);
            Cv2.Rectangle(image, new Rect(x + width - stroke, y, stroke, height), Scalar.All(0), -1);
            Cv2.Rectangle(image, new Rect(x, y + height / 2, width, stroke), Scalar.All(0), -1);
        }
        var mainY = above ? 8 + smallHeight + gap : 8;
        var smallY = above ? 8 : 8 + mainHeight + gap;
        for (var i = 0; i < 12; i++) Glyph(8 + i * 22, mainY, 14, mainHeight, 2);
        // Keep components distinct even with no empty row between the two bands.
        for (var i = 0; i < 4; i++) Glyph(89 + i * 22, smallY, 6, smallHeight, 1);
        // Twelve main components keep the old 75th-percentile height at 40px. The four
        // small components are below its 0.55 cutoff (the 4/5px cases also below 6px).
        // The full envelope is <=1.7H and gap <=0.25H, so the old gate accepted this.
        Assert.True(mainHeight + gap + smallHeight <= 1.7 * mainHeight);
        Assert.False(KnownSingleLineRegion.TryGetBounds(image, out _));
    }

    [OcrTheory]
    [InlineData(.4, false)]
    [InlineData(.5, false)]
    [InlineData(.4, true)]
    [InlineData(.5, true)]
    public void Mixed_font_name_in_actual_pdf_is_complete_or_refused_with_both_modes(double smallScale, bool above)
    {
        const string designation = "ЮИЛГ.461411.123", type = "Сборочный чертеж";
        var fullName = above ? "Блок питания Корпус" : "Корпус Блок питания";
        using var wb = new Workbench();
        var sample = new ManualTemplateSamples.Sample("mixed.pdf", "Разный размер шрифта", FormKind.Drawing,
            designation, fullName, type);
        var templateFile = ManualTemplateSamples.AddTemplate(wb, sample);
        using var template = TemplateLibrary.Load(templateFile).Template!;
        Assert.NotNull(template);
        using var name = MixedFontName(smallScale, above);
        Assert.False(KnownSingleLineRegion.TryGetBounds(name, out _));
        const int dpi = 300;
        using var page = SyntheticForms.Render(FormKind.Drawing, designation, "", dpi, docType: type);
        using var colorName = new Mat();
        Cv2.CvtColor(name, colorName, ColorConversionCodes.GRAY2BGR);
        var nameField = SyntheticForms.Fields(FormKind.Drawing).Name;
        var x = (int)Math.Round((nameField.X + nameField.Width / 2) * dpi / 25.4) - name.Width / 2;
        var y = (int)Math.Round((nameField.Y + nameField.Height / 2) * dpi / 25.4) - name.Height / 2;
        using (var target = new Mat(page, new Rect(x, y, name.Width, name.Height))) colorName.CopyTo(target);
        var file = Path.Combine(wb.Source, sample.FileName);
        SyntheticForms.SavePdf(file, [page], dpi, jpegQuality: 95);
        var before = File.ReadAllBytes(file);
        var original = ocr.Pair.PreferKnownLines;
        FieldDiagnostic? baselineName = null;
        try
        {
            foreach (var fast in new[] { false, true })
            {
                ocr.Pair.PreferKnownLines = fast;
                var diagnostics = new List<RecognitionDiagnostic>();
                Cv2.SetTheRNG(1729UL);
                var result = DocumentReader.Read(file, [template], ocr.Pair, CancellationToken.None, diagnostics.Add);
                if (result.Values is { } values)
                    Assert.Equal(new FieldValues(designation, fullName, type), values);
                else
                    Assert.False(string.IsNullOrWhiteSpace(result.Problem));
                var reading = Assert.Single(diagnostics, d => d.Stage == "Распознавание" && d.Field?.Role == FieldRole.Name).Field!;
                output.WriteLine($"scale={smallScale}, above={above}, fast={fast}, result={result.Values?.Name ?? result.Problem}; " +
                    $"Tesseract='{reading.First.RawText}', Paddle='{reading.Second.RawText}'");
                using var prepared = Cv2.ImDecode(reading.CropPng, ImreadModes.Grayscale);
                Assert.False(KnownSingleLineRegion.TryGetBounds(prepared, out _));
                if (!fast) baselineName = reading;
                else
                {
                    Assert.Equal(baselineName!.First, reading.First);
                    Assert.Equal(baselineName.Second, reading.Second);
                    Assert.Equal(baselineName.Bounds, reading.Bounds);
                    Assert.Equal(baselineName.CropPng, reading.CropPng);
                }
            }
            Assert.Equal(before, File.ReadAllBytes(file));
        }
        finally { ocr.Pair.PreferKnownLines = original; }
    }

    [OcrTheory]
    [InlineData(12)]
    [InlineData(90)]
    public void Tilted_or_vertical_text_uses_full_detection(int degrees)
    {
        using var crop = LoadCrop("01-specification-a4", FieldRole.Designation);
        using var rotated = new Mat();
        if (degrees == 90) Cv2.Rotate(crop, rotated, RotateFlags.Rotate90Clockwise);
        else
        {
            using var padded = new Mat();
            Cv2.CopyMakeBorder(crop, padded, crop.Height, crop.Height, 0, 0, BorderTypes.Constant, Scalar.All(255));
            using var matrix = Cv2.GetRotationMatrix2D(new Point2f(padded.Width / 2f, padded.Height / 2f), degrees, 1);
            Cv2.WarpAffine(padded, rotated, matrix, padded.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(255));
        }

        Assert.False(KnownSingleLineRegion.TryGetBounds(rotated, out _));
    }

    [OcrFact]
    public void Blank_combined_and_stray_ink_regions_use_full_detection()
    {
        using var blank = new Mat(180, 700, MatType.CV_8UC1, Scalar.All(255));
        Assert.False(KnownSingleLineRegion.TryGetBounds(blank, out _));
        using var combined = LoadCrop("04-assembly-a3", FieldRole.Combined);
        Assert.False(KnownSingleLineRegion.TryGetBounds(combined, out _));
        using var name = LoadCrop("02-assembly-a4", FieldRole.Name);
        using var stray = new Mat();
        Cv2.CopyMakeBorder(name, stray, name.Height, name.Height, 20, 20, BorderTypes.Constant, Scalar.All(255));
        Cv2.Circle(stray, new Point(10, 10), 1, Scalar.All(0), -1);
        Cv2.Circle(stray, new Point(stray.Width - 10, stray.Height - 10), 1, Scalar.All(0), -1);
        Assert.False(KnownSingleLineRegion.TryGetBounds(stray, out _));
    }

    [Fact]
    public void Confirmation_reuses_exact_policy_including_confidence_and_ambiguous_B()
    {
        Assert.True(EngineAgreement.AreConfirmed(Parsed.Ok("Вал"), Parsed.Ok("Bал")));
        Assert.True(EngineAgreement.AreConfirmed(Parsed.Ok(""), Parsed.Ok("")));
        Assert.False(EngineAgreement.AreConfirmed(Parsed.Ok("Корпус"), Parsed.Ok("КОРПУС")));
        Assert.False(EngineAgreement.AreConfirmed(Parsed.Ok("Блок ТКС"), Parsed.Ok("Блок TKC")));
        Assert.False(EngineAgreement.AreConfirmed(Parsed.Ok("АBРС.713656.909"), Parsed.Ok("АBРС.713656.909")));
        Assert.False(EngineAgreement.AreConfirmed(Parsed.Ok("Корпус"), Parsed.Ok("")));
        var low = FieldParser.ParseName(new OcrReading("PaddleOCR", [new OcrLine("Корпус", .49, 0, 0, 100, 30)]), .5);
        Assert.False(EngineAgreement.AreConfirmed(Parsed.Ok("Корпус"), low));
        Assert.False(EngineAgreement.AreConfirmed(Parsed.Fail("не прочитано"), Parsed.Fail("не прочитано")));
    }

    [Fact]
    public void High_average_confidence_does_not_hide_a_weak_word()
    {
        const string text = "Длинноенаименование ТКС";
        var weakWordStart = text.IndexOf(' ') + 1;
        var chars = text.Select((c, i) => new RecognizedChar(c.ToString(), i < weakWordStart ? .99f : .2f, i)).ToArray();
        var native = new PaddleOcrRecognizerResult(text, chars.Average(c => c.Score), chars);
        Assert.True(native.Score > .5);
        var score = PaddleEngine.KnownLineConfidence(native);
        Assert.Equal(.2f, score, 5);
        var candidate = new OcrReading("PaddleOCR", [new OcrLine(text, score, 0, 0, 500, 40)]);
        var peer = new OcrReading("Tesseract", [new OcrLine(text, .99, 0, 0, 500, 40)]);
        Assert.False(DocumentReader.IsKnownLineConfirmed(FieldRole.Name, candidate, .5, peer, .3));
    }

    [Fact]
    public void Empty_or_invalid_direct_reading_requires_full_detection_even_for_type()
    {
        var empty = new OcrReading("PaddleOCR", []);
        Assert.False(DocumentReader.IsKnownLineConfirmed(FieldRole.Type, empty, .5, new OcrReading("Tesseract", []), .3));
        foreach (var score in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            var candidate = new OcrReading("PaddleOCR", [new OcrLine("Сборочный чертеж", score, 0, 0, 500, 40)]);
            var peer = new OcrReading("Tesseract", [new OcrLine("Сборочный чертеж", .99, 0, 0, 500, 40)]);
            Assert.False(DocumentReader.IsKnownLineConfirmed(FieldRole.Type, candidate, .5, peer, .3));
        }
        Assert.True(float.IsNaN(PaddleEngine.KnownLineConfidence(new PaddleOcrRecognizerResult("Корпус", .99f, []))));
        Assert.True(float.IsNaN(PaddleEngine.KnownLineConfidence(new PaddleOcrRecognizerResult("Корпус", .99f,
            [new RecognizedChar("Крышка", .99f, 0)]))));
    }

    [Fact]
    public void Nonempty_raw_punctuation_that_parses_as_empty_type_requires_full_detection()
    {
        var candidate = new OcrReading("PaddleOCR", [new OcrLine("---", .99, 0, 0, 100, 30)]);
        var blank = new OcrReading("Tesseract", []);
        Assert.Equal("", FieldParser.ParseType(candidate, .5).Value);
        Assert.Equal("", FieldParser.ParseType(blank, .3).Value);
        Assert.False(DocumentReader.IsKnownLineConfirmed(FieldRole.Type, candidate, .5, blank, .3));
    }

    [OcrFact]
    public void Real_crop_outputs_are_confirmed_against_independent_truth_with_both_flag_values()
    {
        var original = ocr.Pair.PreferKnownLines;
        try
        {
            foreach (var group in Crops.GroupBy(c => c.Sample))
            {
                var cases = group.ToArray();
                var crops = cases.Select(c => LoadCrop(c.Sample, c.Role)).ToArray();
                try
                {
                    foreach (var fast in new[] { false, true })
                    {
                        ocr.Pair.PreferKnownLines = fast;
                        var readings = DocumentReader.ReadSeparateFields(crops, cases.Select(c => c.Role).ToArray(), ocr.Pair, CancellationToken.None);
                        for (var i = 0; i < cases.Length; i++)
                        {
                            var first = Parse(cases[i].Role, readings.First[i], OcrPair.MinConfidence(ocr.Pair.First));
                            var second = Parse(cases[i].Role, readings.Second[i], OcrPair.MinConfidence(ocr.Pair.Second));
                            var accepted = EngineAgreement.AreConfirmed(first, second);
                            // The old detector may lose ')' in sample05; the experimental path must
                            // actually read this known case, without accepting any incorrect name.
                            var knownOldRefusal = !fast && cases[i].Sample.StartsWith("05-", StringComparison.Ordinal) && cases[i].Role == FieldRole.Name;
                            if (!knownOldRefusal)
                                Assert.True(accepted, $"fast={fast}, {cases[i]}: {first} / {second}");
                            if (accepted)
                                Assert.Equal(cases[i].Expected, TextRules.MergeTwins(first.Value!, second.Value!).Value);
                        }
                    }
                }
                finally { foreach (var crop in crops) crop.Dispose(); }
            }
        }
        finally { ocr.Pair.PreferKnownLines = original; }
    }

    [OcrTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Only_unconfirmed_field_is_retried_and_peer_is_not_read_again(bool lowConfidence)
    {
        var cases = Crops.Where(c => c.Sample == "02-assembly-a4").ToArray();
        var crops = cases.Select(c => LoadCrop(c.Sample, c.Role)).ToArray();
        try
        {
            var peer = new Answers(
                (cases[0].Expected, .99),
                (lowConfidence ? cases[1].Expected : "Крышка", lowConfidence ? .1 : .99),
                (cases[2].Expected, .99));
            // Fixture owns Paddle; this lightweight pair intentionally does not dispose it.
            var pair = new OcrPair(peer, ocr.Pair.Second) { PreferKnownLines = true };
            var phases = new List<string>();
            var result = DocumentReader.ReadSeparateFields(crops, cases.Select(c => c.Role).ToArray(), pair,
                CancellationToken.None, (stage, _) => phases.Add(stage));

            Assert.Equal(3, peer.Calls);
            Assert.Single(phases, p => p == "paddle.detect");
            var first = Parse(FieldRole.Name, result.First[1], OcrPair.MinConfidence(peer));
            var second = Parse(FieldRole.Name, result.Second[1], OcrPair.MinConfidence(pair.Second));
            Assert.False(EngineAgreement.AreConfirmed(first, second));
            Assert.Equal("Корпус", second.Value);
            Assert.True(EngineAgreement.AreConfirmed(Parse(FieldRole.Designation, result.First[0], .5), Parse(FieldRole.Designation, result.Second[0], .5)));
            Assert.True(EngineAgreement.AreConfirmed(Parse(FieldRole.Type, result.First[2], .5), Parse(FieldRole.Type, result.Second[2], .5)));
        }
        finally { foreach (var crop in crops) crop.Dispose(); }
    }

    [OcrFact]
    public void Fast_metadata_is_local_to_each_call_and_cancellation_stops_before_native_work()
    {
        using var name = LoadCrop("02-assembly-a4", FieldRole.Name);
        using var combined = LoadCrop("04-assembly-a3", FieldRole.Combined);
        var engine = Assert.IsType<PaddleEngine>(ocr.Pair.Second);
        var first = engine.ReadKnownLines([name]);
        Assert.Equal(new[] { true }, first.KnownLines);
        var second = engine.ReadKnownLines([combined]);
        Assert.Equal(new[] { false }, second.KnownLines);
        Assert.NotSame(first.KnownLines, second.KnownLines);
        Assert.Equal(new[] { true }, first.KnownLines);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var phases = new List<string>();
        Assert.Throws<OperationCanceledException>(() => engine.ReadKnownLines([name], (stage, _) => phases.Add(stage), cancel.Token));
        Assert.Throws<OperationCanceledException>(() => engine.ReadMany([name], (stage, _) => phases.Add(stage), cancel.Token));
        Assert.Empty(phases);
    }

    [OcrFact]
    public void Engine_exception_is_propagated_without_retry_or_fallback()
    {
        using var crop = LoadCrop("02-assembly-a4", FieldRole.Name);
        var failure = new FailingEngine();
        // Fixture retains ownership of Paddle.
        var pair = new OcrPair(failure, ocr.Pair.Second) { PreferKnownLines = true };
        var phases = new List<string>();

        var actual = Assert.Throws<IOException>(() => DocumentReader.ReadSeparateFields([crop], [FieldRole.Name], pair,
            CancellationToken.None, (phase, _) => phases.Add(phase)));

        Assert.Same(failure.Error, actual);
        Assert.Equal(1, failure.Calls);
        Assert.Empty(phases);
    }

    [OcrTheory]
    [InlineData(FormKind.Drawing, 0, false, false)]
    [InlineData(FormKind.Drawing, 1, false, false)]
    [InlineData(FormKind.Drawing, 2, false, false)]
    [InlineData(FormKind.Drawing, 3, false, false)]
    [InlineData(FormKind.Drawing, 0, true, false)]
    [InlineData(FormKind.Specification, 0, false, false)]
    [InlineData(FormKind.DrawingA3, 0, false, false)]
    [InlineData(FormKind.Drawing, 0, false, true)]
    public void Whole_documents_preserve_crops_and_never_accept_wrong_fields(FormKind kind, int turns, bool multiline, bool poorScan)
    {
        using var wb = new Workbench();
        var sample = new ManualTemplateSamples.Sample("scan.pdf", "Образец", kind,
            poorScan ? "АБРС.713656.909" : kind == FormKind.DrawingA3 ? "ПРМД.301261.010" : "ЮИЛГ.461411.123",
            multiline ? "Крышка\nверхняя" : kind == FormKind.DrawingA3 ? "Рама сварная" : "Корпус",
            kind == FormKind.Specification ? DocTypes.WhenMissing : "Сборочный чертеж",
            TypeAbsent: kind == FormKind.Specification, Combined: kind == FormKind.DrawingA3);
        var templateFile = ManualTemplateSamples.AddTemplate(wb, sample);
        using var template = TemplateLibrary.Load(templateFile).Template!;
        Assert.NotNull(template);
        var file = ManualTemplateSamples.AddDocument(wb, sample, new ScanOptions
        {
            QuarterTurns = turns, Dpi = poorScan ? 150 : 250, JpegQuality = 92,
            SkewDegrees = poorScan ? 1.5 : turns == 0 ? 0 : 1.2,
            Noise = poorScan ? .04 : 0, Blur = poorScan ? .6 : 0,
        });
        var bytes = File.ReadAllBytes(file);
        var original = ocr.Pair.PreferKnownLines;
        var logs = new List<RecognitionDiagnostic>[2];
        var answers = new DocumentResult[2];
        try
        {
            for (var i = 0; i < 2; i++)
            {
                ocr.Pair.PreferKnownLines = i != 0;
                logs[i] = [];
                Cv2.SetTheRNG(1729UL);
                answers[i] = DocumentReader.Read(file, [template], ocr.Pair, CancellationToken.None, logs[i].Add);
                if (!poorScan) Assert.True(answers[i].Values is not null, $"fast={i != 0}: {answers[i].Problem}");
                if (answers[i].Values is { } values)
                    Assert.Equal(new FieldValues(sample.Designation, sample.Name.Replace('\n', ' '), sample.Type), values);
                else Assert.False(string.IsNullOrWhiteSpace(answers[i].Problem));
            }
            var oldFields = logs[0].Where(d => d.Field is not null).Select(d => d.Field!).ToArray();
            var newFields = logs[1].Where(d => d.Field is not null).Select(d => d.Field!).ToArray();
            Assert.Equal(oldFields.Length, newFields.Length);
            for (var i = 0; i < oldFields.Length; i++)
            {
                Assert.Equal(oldFields[i].Role, newFields[i].Role);
                Assert.Equal(oldFields[i].Bounds, newFields[i].Bounds);
                Assert.Equal(oldFields[i].CropPng, newFields[i].CropPng);
                Assert.Equal(oldFields[i].First, newFields[i].First);
                if (sample.Combined) Assert.Equal(oldFields[i].Second, newFields[i].Second);
            }
            if (sample.Combined)
            {
                Assert.Equal(answers[0].Values, answers[1].Values);
                Assert.Equal(answers[0].Problem, answers[1].Problem);
            }
            Assert.Equal(bytes, File.ReadAllBytes(file));
        }
        finally { ocr.Pair.PreferKnownLines = original; }
    }

    private static Parsed Parse(FieldRole role, OcrReading reading, double confidence) => role switch
    {
        FieldRole.Designation => FieldParser.ParseDesignation(reading, confidence),
        FieldRole.Name => FieldParser.ParseName(reading, confidence),
        FieldRole.Type => FieldParser.ParseType(reading, confidence),
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    private static Mat MixedFontName(double smallScale, bool above)
    {
        using var main = LoadCrop("02-assembly-a4", FieldRole.Name);
        using var secondary = LoadCrop("03-schematic-a4", FieldRole.Name);
        using var mainInk = new Mat();
        using var smallInk = new Mat();
        Cv2.Threshold(main, mainInk, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
        Cv2.Threshold(secondary, smallInk, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
        using var mainText = new Mat(main, Cv2.BoundingRect(mainInk));
        using var secondaryText = new Mat(secondary, Cv2.BoundingRect(smallInk));
        using var resized = new Mat();
        var smallHeight = (int)Math.Round(mainText.Height * smallScale);
        Cv2.Resize(secondaryText, resized, new Size((int)Math.Round(secondaryText.Width * smallHeight / (double)secondaryText.Height), smallHeight),
            interpolation: InterpolationFlags.Area);
        const int gap = 2, padding = 8;
        var width = Math.Max(mainText.Width, resized.Width) + 2 * padding;
        var image = new Mat(mainText.Height + resized.Height + gap + 2 * padding, width, MatType.CV_8UC1, Scalar.All(255));
        var mainY = above ? padding + resized.Height + gap : padding;
        var smallY = above ? padding : padding + mainText.Height + gap;
        using (var target = new Mat(image, new Rect((width - mainText.Width) / 2, mainY, mainText.Width, mainText.Height))) mainText.CopyTo(target);
        using (var target = new Mat(image, new Rect((width - resized.Width) / 2, smallY, resized.Width, resized.Height))) resized.CopyTo(target);
        return image;
    }

    private static Mat LoadCrop(string sample, FieldRole role)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "docs", "test-samples", "crops", $"{sample}-{role}.png");
            if (File.Exists(path))
            {
                var result = Cv2.ImRead(path, ImreadModes.Grayscale);
                Assert.False(result.Empty());
                return result;
            }
            directory = directory.Parent;
        }
        throw new FileNotFoundException($"Tracked crop missing: {sample}-{role}.png");
    }

    private sealed class Answers(params (string Text, double Confidence)[] answers) : IOcrEngine
    {
        public string Name => "Управляемый ответ";
        private readonly Queue<(string Text, double Confidence)> _answers = new(answers);
        public int Calls { get; private set; }
        public OcrReading Read(Mat gray)
        {
            Calls++;
            var answer = _answers.Dequeue();
            return new OcrReading(Name, [new OcrLine(answer.Text, answer.Confidence, 0, 0, gray.Width, gray.Height)]);
        }
        public void Dispose() { }
    }

    private sealed class FailingEngine : IOcrEngine
    {
        public string Name => "Ошибка движка";
        public IOException Error { get; } = new("Контролируемая ошибка распознавания");
        public int Calls { get; private set; }
        public OcrReading Read(Mat gray)
        {
            Calls++;
            throw Error;
        }
        public void Dispose() { }
    }
}
