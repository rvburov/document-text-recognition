using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Core.Rename;
using DocumentTextRecognition.Core.Templates;
using DocumentTextRecognition.Tests.Ocr;
using OpenCvSharp;
using Xunit.Abstractions;

namespace DocumentTextRecognition.Tests.Samples;

/// <summary>Real GOST Type A glyphs and independent predeclared truth; no OCR substitutions.</summary>
[Collection("OCR")]
public sealed class GostTypeATests(OcrFixture ocr, ITestOutputHelper output)
{
    private const string Designation = "УИЕС.468732.271";
    private const string PrintedName = "Плата УПЧ\n1489 МГц";
    private const string ExpectedName = "Плата УПЧ 1489 МГц";
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    [OcrFact]
    public void Direct_fields_use_production_profiles_and_accept_only_independent_goldens()
    {
        var folder = EvidenceFolder("direct-fields");
        var inputs = new[]
        {
            new Golden("designation", FieldRole.Designation, Designation, Designation, 5),
            new Golden("name-multiline", FieldRole.Name, PrintedName, ExpectedName, 3.5),
            new Golden("name-mixed", FieldRole.Name, "Плата Epcos USB", "Плата Epcos USB", 3.5),
            new Golden("name-mixed-multiline", FieldRole.Name, "Плата Epcos\nUSB 1489 МГц", "Плата Epcos USB 1489 МГц", 3.5),
        };
        var records = new List<object>();
        var combinations = new List<object>();
        var failures = new List<string>();
        var fieldsCount = new Counts();
        var combinationsCount = new Counts();
        var originalMode = ocr.Pair.PreferKnownLines;
        try
        {
            foreach (var knownLines in new[] { false, true })
            {
                ocr.Pair.PreferKnownLines = knownLines;
                var crops = new List<Mat>();
                try
                {
                    foreach (var input in inputs)
                        crops.Add(GostTypeASamples.RenderField(input.Rendered, input.CapHeightMm, dpi: 300));
                    var before = crops.Select(Png).ToArray();
                    // Production role dispatch applies CyrillicDesignation only to the first
                    // crop, then General to names. OCR receives pixels and roles, never truth.
                    var readings = DocumentReader.ReadSeparateFields(crops, inputs.Select(i => i.Role).ToArray(),
                        ocr.Pair, CancellationToken.None);
                    var first = new Parsed[inputs.Length];
                    var second = new Parsed[inputs.Length];
                    for (var i = 0; i < inputs.Length; i++)
                    {
                        var input = inputs[i];
                        first[i] = Parse(input.Role, readings.First[i], OcrPair.MinConfidence(ocr.Pair.First));
                        second[i] = Parse(input.Role, readings.Second[i], OcrPair.MinConfidence(ocr.Pair.Second));
                        var accepted = EngineAgreement.AreConfirmed(first[i], second[i])
                            ? TextRules.MergeTwins(first[i].Value!, second[i].Value!).Value : null;
                        var outcome = fieldsCount.Add(accepted, input.Expected);
                        var cropFile = $"{input.Id}-known-{knownLines}.png";
                        File.WriteAllBytes(Path.Combine(folder, cropFile), before[i]);
                        records.Add(new
                        {
                            input.Id,
                            KnownLines = knownLines,
                            Role = input.Role.ToString(),
                            Profile = input.Role == FieldRole.Designation ? "CyrillicDesignation" : "General",
                            input.Rendered,
                            input.CapHeightMm,
                            Dpi = 300,
                            Expected = input.Expected,
                            Crop = cropFile,
                            CropSha256 = Hash(before[i]),
                            First = Describe(readings.First[i], first[i], ocr.Pair.First),
                            Second = Describe(readings.Second[i], second[i], ocr.Pair.Second),
                            Accepted = accepted,
                            RefusalReason = accepted is null ? first[i].Problem ?? second[i].Problem ??
                                "The independent readings differ under the existing engine agreement policy." : null,
                            Outcome = outcome,
                        });
                        if (outcome == "wrong") failures.Add($"knownLines={knownLines}, {input.Id}: accepted '{accepted}', expected '{input.Expected}'.");
                        // This positive regression proves that a designation whitelist cannot
                        // leak into a following Cyrillic/Latin name on the reused engines.
                        if (input.Id == "name-mixed" && accepted != input.Expected)
                            failures.Add($"knownLines={knownLines}: General must confirm '{input.Expected}', got '{accepted}'; Tesseract='{readings.First[i].Text}', Paddle='{readings.Second[i].Text}'.");
                        if (!before[i].AsSpan().SequenceEqual(Png(crops[i]))) failures.Add(input.Id + ": OCR changed the input pixels.");
                    }
                    for (var i = 1; i < inputs.Length; i++)
                    {
                        // All designation/name values here are actual independent readings.
                        // No type is drawn in these specimens, as explicitly declared.
                        var combined = EngineAgreement.Combine(
                            new EngineFields(ocr.Pair.First.Name, first[0], first[i], Parsed.Ok("")),
                            new EngineFields(ocr.Pair.Second.Name, second[0], second[i], Parsed.Ok("")));
                        var expected = new FieldValues(Designation, inputs[i].Expected, "Спецификация");
                        var outcome = combinationsCount.Add(combined.Values, expected);
                        combinations.Add(new { inputs[i].Id, KnownLines = knownLines, Expected = expected,
                            combined.Values, combined.Problem, Outcome = outcome });
                        if (outcome == "wrong") failures.Add($"knownLines={knownLines}, {inputs[i].Id}: incorrect combined fields {combined.Values}.");
                        if (combined.Values is null && string.IsNullOrWhiteSpace(combined.Problem))
                            failures.Add(inputs[i].Id + ": refusal has no reason.");
                    }
                }
                finally { foreach (var crop in crops) crop.Dispose(); }
            }
        }
        finally { ocr.Pair.PreferKnownLines = originalMode; }
        WriteEvidence(folder, new
        {
            Test = nameof(Direct_fields_use_production_profiles_and_accept_only_independent_goldens),
            Scope = "Text-only GOST Type A Slanted crops; current production profiles; actual two-engine readings. Field outcomes are not PDF success counts.",
            GostTypeASamples.FontPath,
            GostTypeASamples.FontSha256,
            PaddleBackend = Assert.IsType<PaddleEngine>(ocr.Pair.Second).UsesOnnx ? "ONNX" : "Paddle native",
            DirectFields = fieldsCount,
            DirectFieldCombinations = combinationsCount,
            Fields = records,
            Combinations = combinations,
            Failures = failures,
        });
        Assert.IsType<TesseractEngine>(ocr.Pair.First);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [OcrFact]
    public void Saved_coordinates_follow_original_rotated_and_skewed_gost_pdf_without_wrong_acceptance()
    {
        var folder = EvidenceFolder("end-to-end");
        var cases = new[] { (Id: "original", Turns: 0, Skew: 0d), (Id: "rotated-90", Turns: 1, Skew: 0d),
            (Id: "skewed-1.2", Turns: 0, Skew: 1.2d) };
        var expected = new FieldValues(Designation, ExpectedName, "Спецификация");
        const string expectedFileName = Designation + " " + ExpectedName + " Спецификация.pdf";
        var records = new List<object>();
        var failures = new List<string>();
        var count = new Counts();
        var originalMode = ocr.Pair.PreferKnownLines;
        try
        {
            foreach (var knownLines in new[] { false, true })
            foreach (var variant in cases)
            {
                using var wb = new Workbench();
                // Generic filename and unrelated template values cannot supply the answer.
                var sample = GostTypeASamples.Create(wb, "scan", Designation, PrintedName,
                    dpi: 300, quarterTurns: variant.Turns, skewDegrees: variant.Skew);
                var sourceHash = Hash(File.ReadAllBytes(sample.PdfPath));
                var templateHash = Hash(File.ReadAllBytes(sample.TemplatePath));
                var markupPath = TemplateMarkupStore.PathFor(sample.TemplatePath);
                var markupHash = Hash(File.ReadAllBytes(markupPath));
                var saved = TemplateMarkupStore.Load(sample.TemplatePath);
                Assert.Null(saved.Problem);
                Assert.NotNull(saved.Markup);
                Assert.True(saved.Markup.TypeAbsent);
                Assert.Equal(sample.Regions, saved.Markup.Regions);
                var loaded = TemplateLibrary.Load(sample.TemplatePath);
                Assert.True(loaded.Template is not null, loaded.Problem);
                using var template = loaded.Template!;
                Assert.True(template.IsManual);
                Assert.True(template.TypeAbsent);
                Assert.False(ocr.Pair.PrefersGost(template)); // new template identity: base attempt precedes specialist
                foreach (var region in saved.Markup.Regions)
                    Assert.Equal(region.Bounds, Assert.Single(template.Frames, f => f.Role == region.Role).Inner);

                ocr.Pair.PreferKnownLines = knownLines;
                var diagnostics = new List<RecognitionDiagnostic>();
                Cv2.SetTheRNG(1729UL);
                var result = DocumentReader.Read(sample.PdfPath, [template], ocr.Pair,
                    CancellationToken.None, diagnostics.Add);
                var fields = diagnostics.Where(d => d.Field is not null).ToArray();
                var fieldRecords = new List<object>();
                var writtenRoles = saved.Markup.Regions.Select(r => r.Role).Order().ToArray();
                var expectedAttempts = 1 + (result.Trace?.GostAttempts ?? 0);
                if (result.Trace is null || fields.Length != saved.Markup.Regions.Count * expectedAttempts)
                    failures.Add($"{variant.Id}/knownLines={knownLines}: expected both saved fields for each of {expectedAttempts} model attempts, got {fields.Length}; {result.Problem}");
                if (!fields.Select(d => d.Field!.Role).Distinct().Order().SequenceEqual(writtenRoles))
                    failures.Add($"{variant.Id}/knownLines={knownLines}: actual field roles differ from saved regions.");
                var cropHashes = new Dictionary<FieldRole, string>();
                foreach (var (attempt, attemptIndex) in fields.Chunk(saved.Markup.Regions.Count).Select((items, index) => (items, index)))
                {
                    if (!attempt.Select(d => d.Field!.Role).Order().SequenceEqual(writtenRoles))
                        failures.Add($"{variant.Id}/knownLines={knownLines}/attempt={attemptIndex + 1}: missing or duplicate saved field.");
                    foreach (var field in attempt)
                    {
                        var detail = field.Field!;
                        var region = saved.Markup.Regions.Single(r => r.Role == detail.Role);
                        if (detail.Bounds != region.Bounds)
                            failures.Add($"{variant.Id}/knownLines={knownLines}/{detail.Role}: crop metadata differs from persisted normalized template coordinates.");
                        var cropHash = Hash(detail.CropPng);
                        if (cropHashes.TryGetValue(detail.Role, out var previousHash) && cropHash != previousHash)
                            failures.Add($"{variant.Id}/knownLines={knownLines}/{detail.Role}: model attempts received different field pixels.");
                        cropHashes[detail.Role] = cropHash;
                        var crop = $"{variant.Id}-known-{knownLines}-attempt-{attemptIndex + 1}-{detail.Role}.png";
                        File.WriteAllBytes(Path.Combine(folder, crop), detail.CropPng);
                        fieldRecords.Add(new { ModelAttempt = attemptIndex + 1, ModelProfile = attemptIndex == 0 ? "base" : "gost",
                            field.PageNumber, Role = detail.Role.ToString(), PersistedBounds = region.Bounds,
                            ActualBounds = detail.Bounds, Crop = crop, CropSha256 = cropHash, detail.First, detail.Second });
                        if (detail.First.MinConfidence is null || detail.Second.MinConfidence is null)
                            failures.Add($"{variant.Id}/{detail.Role}: missing actual engine confidence.");
                    }
                }
                var outcome = count.Add(result.Values, expected);
                var built = result.Values is null ? null : FileNames.Build(result.Values).Name;
                if (outcome == "wrong" || result.Values is not null && built != expectedFileName)
                    failures.Add($"{variant.Id}/knownLines={knownLines}: accepted '{built}', expected '{expectedFileName}'.");
                if (variant.Id == "original" && knownLines && outcome != "correct")
                    failures.Add("Original upright GOST PDF must be read exactly with the shipped known-line setting; refusal is not success.");
                if (result.Values is null && string.IsNullOrWhiteSpace(result.Problem))
                    failures.Add(variant.Id + ": refusal has no reason.");
                if (sourceHash != Hash(File.ReadAllBytes(sample.PdfPath)) ||
                    templateHash != Hash(File.ReadAllBytes(sample.TemplatePath)) ||
                    markupHash != Hash(File.ReadAllBytes(markupPath))) failures.Add(variant.Id + ": source/template bytes changed.");
                records.Add(new
                {
                    variant.Id, KnownLines = knownLines, variant.Turns, variant.Skew,
                    PdfFileName = Path.GetFileName(sample.PdfPath), SourceSha256 = sourceHash,
                    TemplateSha256 = templateHash, MarkupSha256 = markupHash,
                    Expected = expected, ExpectedFileName = expectedFileName,
                    Actual = result.Values, ActualFileName = built, result.Problem, Outcome = outcome,
                    SourceTrace = result.Trace,
                    Fields = fieldRecords,
                    Stages = diagnostics.Select(d => new { d.Stage, d.PageNumber, d.Message }).ToArray(),
                });
                // Only the explicitly requested original specimen is exported. The other
                // five PDFs remain temporary; historical direct-fields evidence is untouched.
                if (!knownLines && variant.Id == "original" &&
                    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DTR_GOST_OUTPUT")))
                {
                    var fixture = EvidenceFolder("fixture");
                    File.Copy(sample.PdfPath, Path.Combine(fixture, "scan.pdf"), overwrite: true);
                    File.Copy(sample.TemplatePath, Path.Combine(fixture, "template.pdf"), overwrite: true);
                    File.Copy(markupPath, Path.Combine(fixture, "template.pdf.template.json"), overwrite: true);
                }
            }
        }
        finally { ocr.Pair.PreferKnownLines = originalMode; }
        WriteEvidence(folder, new
        {
            Test = nameof(Saved_coordinates_follow_original_rotated_and_skewed_gost_pdf_without_wrong_acceptance),
            Scope = "Six actual raster-PDF reads, saved manual coordinates, unrelated template text, two real OCR engines. Refusal is reported separately from correct recognition.",
            GostTypeASamples.FontPath, GostTypeASamples.FontSha256,
            GostSpecialist = new { GostTesseractModel.Sha256, GostTesseractModel.Languages, GostTesseractModel.RawDesignation },
            EndToEnd = count, Samples = records, Failures = failures,
        });
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static object Describe(OcrReading reading, Parsed parsed, IOcrEngine engine) => new
    {
        Reading = reading, Parsed = parsed, Threshold = OcrPair.MinConfidence(engine),
    };

    private static Parsed Parse(FieldRole role, OcrReading reading, double threshold) => role switch
    {
        FieldRole.Designation => FieldParser.ParseDesignation(reading, threshold),
        FieldRole.Name => FieldParser.ParseName(reading, threshold),
        FieldRole.Type => FieldParser.ParseType(reading, threshold),
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    private void WriteEvidence(string folder, object evidence)
    {
        var json = JsonSerializer.Serialize(evidence, Json);
        File.WriteAllText(Path.Combine(folder, "verification.json"), json);
        output.WriteLine(json);
        output.WriteLine("Evidence: " + folder);
    }

    private static byte[] Png(Mat image)
    {
        Cv2.ImEncode(".png", image, out var bytes);
        return bytes;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string EvidenceFolder(string name)
    {
        var root = Environment.GetEnvironmentVariable("DTR_GOST_OUTPUT");
        if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(AppContext.BaseDirectory, "gost-type-a-evidence");
        var folder = Path.Combine(root, "current-profile-verification", name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private sealed class Counts
    {
        public int Correct { get; private set; }
        public int Refused { get; private set; }
        public int Wrong { get; private set; }
        internal string Add<T>(T? actual, T expected) where T : class
        {
            if (actual is null) { Refused++; return "refused"; }
            if (EqualityComparer<T>.Default.Equals(actual, expected)) { Correct++; return "correct"; }
            Wrong++;
            return "wrong";
        }
    }

    private sealed record Golden(string Id, FieldRole Role, string Rendered, string Expected, double CapHeightMm);
}
