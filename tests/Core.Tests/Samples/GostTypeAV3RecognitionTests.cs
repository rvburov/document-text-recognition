using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Core.Rename;
using DocumentTextRecognition.Core.Templates;
using OpenCvSharp;
using Xunit.Abstractions;

namespace DocumentTextRecognition.Tests.Samples;

/// <summary>Actual selected product models; independent, predeclared text reaches only this evaluator.</summary>
[Collection("OCR")]
public sealed class GostTypeAV3RecognitionTests(ITestOutputHelper output)
{
    private const string CorpusSha = "605A04017EB37C09FFC745506B4BF7FF8BB1943C696D6D72E58A42A282F6797B";
    private const string SelectedModelSha = "C92B7C889F0BD4FB07CA90148C011BE7A1865BE2B8BBA036FA1A86D875A5ADD0";
    private const string SelectionReportSha = "FEF34D30A48622F87DA65BA9C66D6715642D37714E1A2C7B78E17568CF515DC8";

    [OcrFact]
    public void Selected_models_read_new_vector_and_raster_pdfs_with_real_positive_results()
    {
        Assert.Equal(SelectedModelSha, GostTesseractModel.Sha256.ToUpperInvariant());
        Assert.True(GostTesseractModel.RawDesignation);
        var corpus = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(GostTypeASamples.FontPath)!,
            "..", "..", "..", "docs", "test-samples", "gost-type-a", "corpus-v3"));
        var manifestPath = Path.Combine(corpus, "manifest.json");
        Assert.Equal(CorpusSha, Hash(manifestPath));
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var split = manifest.RootElement.GetProperty("Splits").EnumerateArray()
            .Single(item => item.GetProperty("Split").GetString() == "evaluation");
        var truthPath = Path.Combine(corpus, split.GetProperty("File").GetString()!);
        Assert.Equal(split.GetProperty("Sha256").GetString()!.ToUpperInvariant(), Hash(truthPath));
        using var truth = JsonDocument.Parse(File.ReadAllText(truthPath));
        var cases = truth.RootElement.GetProperty("Documents").EnumerateArray().Take(8).ToArray();
        Assert.Equal(8, cases.Length);

        var folder = Path.Combine(Environment.GetEnvironmentVariable("DTR_GOST_OUTPUT") is { Length: > 0 } root
            ? root : Path.Combine(AppContext.BaseDirectory, "gost-type-a-evidence"), "pipeline-v3");
        Directory.CreateDirectory(folder);
        OcrComponents.ConfigureRuntime();
        var tessdata = OcrComponents.FindTessdata() ?? throw new InvalidOperationException("Tesseract data missing.");
        Assert.Equal(SelectedModelSha, Hash(Path.Combine(tessdata, GostTesseractModel.FileName)));
        using var pair = OcrPair.CreateReal(tessdata);
        Assert.True(pair.PreferKnownLines);
        var counts = new Dictionary<string, Counts> { ["vector"] = new(), ["raster"] = new() };
        var records = new List<object>();
        var failures = new List<string>();
        for (var index = 0; index < cases.Length; index++)
        {
            var item = cases[index];
            var id = item.GetProperty("Id").GetString()!;
            var rawDesignation = item.GetProperty("Designation").GetString()!;
            var rawName = item.GetProperty("Name").GetString()!;
            Assert.Equal(JsonValueKind.Null, item.GetProperty("Type").ValueKind);
            // Existing application contract, declared independently from either engine:
            // whitespace joins and ё/е folding; the absent type is the template's default.
            var expected = new FieldValues(Canonical(rawDesignation), Canonical(rawName), "Спецификация");
            var expectedFileName = $"{expected.Designation} {expected.Name} {expected.Type}.pdf";
            using var work = new Workbench();
            var sample = GostTypeASamples.CreatePair(work, "neutral", rawDesignation, rawName,
                dpi: new[] { 250, 300, 325 }[index % 3]);
            var loaded = TemplateLibrary.Load(sample.TemplatePath);
            Assert.True(loaded.Template is not null, loaded.Problem);
            using var template = loaded.Template!;
            var caseFolder = Path.Combine(folder, id);
            Directory.CreateDirectory(caseFolder);
            File.Copy(sample.TemplatePath, Path.Combine(caseFolder, "template.pdf"), overwrite: true);
            var markup = TemplateMarkupStore.PathFor(sample.TemplatePath);
            File.Copy(markup, Path.Combine(caseFolder, "template.pdf.template.json"), overwrite: true);
            foreach (var (kind, source) in new[] { ("vector", sample.VectorPath), ("raster", sample.RasterPath) })
            {
                var before = Hash(source);
                var templateBefore = Hash(sample.TemplatePath);
                var markupBefore = Hash(markup);
                File.Copy(source, Path.Combine(caseFolder, $"scan-{kind}.pdf"), overwrite: true);
                var diagnostics = new List<RecognitionDiagnostic>();
                Cv2.SetTheRNG(1729UL);
                var result = DocumentReader.Read(source, [template], pair, CancellationToken.None, diagnostics.Add);
                var actualFileName = result.Values is null ? null : FileNames.Build(result.Values).Name;
                var outcome = result.Values is null ? "refused" : result.Values == expected && actualFileName == expectedFileName
                    ? "correct" : "wrong";
                counts[kind].Add(outcome);
                if (outcome == "wrong") failures.Add($"{id}/{kind}: accepted '{actualFileName}', expected '{expectedFileName}'.");
                if (outcome == "refused" && string.IsNullOrWhiteSpace(result.Problem)) failures.Add($"{id}/{kind}: refusal lacks reason.");
                if (before != Hash(source) || templateBefore != Hash(sample.TemplatePath) || markupBefore != Hash(markup))
                    failures.Add($"{id}/{kind}: source or template changed.");
                if (result.Trace is not { MatchingCalls: 1, MatchedPages: 1 }) failures.Add($"{id}/{kind}: expected one actual successful page match.");
                if (result.Trace is null || result.Trace.PaddleReads == 0)
                    failures.Add($"{id}/{kind}: independent raster recognizer did not execute.");
                var detailRecords = new List<object>();
                for (var diagnosticIndex = 0; diagnosticIndex < diagnostics.Count; diagnosticIndex++)
                {
                    var diagnostic = diagnostics[diagnosticIndex];
                    object? field = null;
                    if (diagnostic.Field is { } detail)
                    {
                        var file = $"{kind}-{diagnosticIndex + 1:000}-{detail.Role}.png";
                        File.WriteAllBytes(Path.Combine(caseFolder, file), detail.CropPng);
                        var saved = sample.Regions.Single(region => region.Role == detail.Role);
                        if (detail.Bounds != saved.Bounds) failures.Add($"{id}/{kind}/{detail.Role}: diagnostic ROI differs from saved bounds.");
                        field = new { Role = detail.Role.ToString(), detail.Bounds, Crop = file,
                            CropSha256 = Convert.ToHexString(SHA256.HashData(detail.CropPng)), detail.First, detail.Second };
                    }
                    detailRecords.Add(new { DiagnosticIndex = diagnosticIndex + 1, diagnostic.Stage,
                        diagnostic.PageNumber, diagnostic.Message, Field = field });
                }
                records.Add(new
                {
                    Id = id, Kind = kind, sample.RasterDpi,
                    RawExpected = new { Designation = rawDesignation, Name = rawName, Type = (string?)null },
                    CanonicalExpected = expected, ExpectedFileName = expectedFileName,
                    Actual = result.Values, ActualFileName = actualFileName, Outcome = outcome, result.Problem,
                    SourceSha256 = before, TemplateSha256 = templateBefore, MarkupSha256 = markupBefore,
                    SourceTrace = result.Trace, SourcePath = result.Trace?.SourcePath, Diagnostics = detailRecords,
                });
            }
        }
        if (counts["vector"].Correct == 0) failures.Add("No exact positive vector PDF: refusals do not satisfy this test.");
        if (counts["raster"].Correct == 0) failures.Add("No exact positive raster PDF: refusals do not satisfy this test.");
        var evidence = new
        {
            Scope = "Sixteen actual DocumentReader reads of eight predeclared held-out GOST cases. All refusals remain in the denominator.",
            ManifestSha256 = CorpusSha, SplitManifestSha256 = Hash(truthPath), SelectionReportSha256 = SelectionReportSha,
            SelectedModelSha256 = SelectedModelSha, GostTesseractModel.Languages, GostTesseractModel.RawDesignation,
            TesseractVersion = TesseractEngine.Version(), KnownLines = pair.PreferKnownLines,
            CanonicalPolicy = "Join whitespace; ё→е and Ё→Е; absent type→Спецификация. Printed raw truths are preserved separately.",
            PositiveGate = "At least one exact vector and one exact raster case; zero accepted incorrect values. Counts describe coverage.",
            Counts = counts, Samples = records, Failures = failures,
        };
        var json = JsonSerializer.Serialize(evidence, new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        });
        File.WriteAllText(Path.Combine(folder, "verification.json"), json + "\n");
        output.WriteLine(json);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string Canonical(string text) => string.Join(" ", text.Split((char[]?)null,
        StringSplitOptions.RemoveEmptyEntries)).Replace('ё', 'е').Replace('Ё', 'Е');
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private sealed class Counts
    {
        public int Correct { get; private set; }
        public int Refused { get; private set; }
        public int Wrong { get; private set; }
        internal void Add(string outcome) { if (outcome == "correct") Correct++; else if (outcome == "refused") Refused++; else Wrong++; }
    }
}
