using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Core.Templates;
using DocumentTextRecognition.Tests.Samples;
using OpenCvSharp;
using Sdcb.PaddleOCR;

namespace DocumentTextRecognition.Tests.Ocr;

/// <summary>Экспериментальный путь известной строки: геометрия, строгая сверка и возврат
/// к полному Paddle только для неподтвержденных полей. Эталонные значения заданы до OCR.</summary>
[Collection("OCR")]
public sealed class KnownLineTests(OcrFixture ocr)
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
    public void Experimental_path_is_disabled_by_default()
    {
        using var pair = new OcrPair(new Answers(), new Answers());
        Assert.False(pair.PreferKnownLines);
    }

    [OcrTheory]
    [MemberData(nameof(SingleLineCrops))]
    public void Single_band_keeps_every_ink_pixel_and_original_input(string sample, string roleName)
    {
        var role = Enum.Parse<FieldRole>(roleName);
        using var crop = LoadCrop(sample, role);
        var before = crop.ToBytes(".png");

        Assert.True(KnownSingleLineRegion.TryGetBounds(crop, out var box));

        Assert.True(box.Width > 0 && box.Height > 0);
        Assert.InRange(box.Left, 0, crop.Width);
        Assert.InRange(box.Top, 0, crop.Height);
        Assert.InRange(box.Right, 1, crop.Width);
        Assert.InRange(box.Bottom, 1, crop.Height);
        using var ink = new Mat();
        Cv2.Threshold(crop, ink, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
        using (var preserved = new Mat(ink, box)) preserved.SetTo(Scalar.All(0));
        Assert.Equal(0, Cv2.CountNonZero(ink)); // Including punctuation, never a selected subset of glyphs.
        Assert.Equal(before, crop.ToBytes(".png"));
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
