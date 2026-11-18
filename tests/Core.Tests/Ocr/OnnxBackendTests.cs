using System.Security.Cryptography;
using System.Text.Json;
using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Core.Rename;
using DocumentTextRecognition.Core.Templates;
using DocumentTextRecognition.Tests.Samples;
using OpenCvSharp;

namespace DocumentTextRecognition.Tests.Ocr;

/// <summary>
/// Differential ONNX tests use the explicitly supplied converted versions of the production
/// models. The optional 48-document corpus remains outside Git; an absent opt-in skips these
/// tests, while an invalid explicitly requested model/corpus path fails them.
/// </summary>
[Collection("OCR")]
public sealed class OnnxBackendTests
{
    internal const string ModelsVariable = "DTR_ONNX_MODELS_DIR";
    internal const string DiverseSamplesVariable = "DTR_ONNX_DIVERSE_SAMPLES_DIR";
    private const double ScoreTolerance = 1e-5;

    [OcrFact]
    public void Explicit_native_backend_reads_without_onnx_model_files()
    {
        OcrComponents.ConfigureRuntime();
        var unavailableModels = Path.Combine(Path.GetTempPath(), "dtr-no-onnx-" + Guid.NewGuid().ToString("N"));
        using var engine = new PaddleEngine(OcrComponents.PaddleDir, useOnnx: false, onnxModelsDir: unavailableModels);
        Assert.False(engine.UsesOnnx);
        using var crop = LoadCrop(Path.Combine(TrackedSamples(), "crops", "02-assembly-a4-Name.png"));

        var reading = engine.Read(crop);

        Assert.Equal("Корпус", FieldParser.ParseName(reading, OcrPair.MinConfidence(engine)).Value);
        Assert.False(Directory.Exists(unavailableModels));
    }

    [OcrFact]
    public void Default_pair_reads_using_bundled_onnx_models_and_enables_confirmed_lines()
    {
        OcrComponents.ConfigureRuntime();
        using var pair = OcrPair.CreateReal(OcrComponents.FindTessdata()!);
        var engine = Assert.IsType<PaddleEngine>(pair.Second);
        Assert.True(engine.UsesOnnx);
        Assert.True(pair.PreferKnownLines);
        using var crop = LoadCrop(Path.Combine(TrackedSamples(), "crops", "02-assembly-a4-Name.png"));
        Assert.Equal("Корпус", FieldParser.ParseName(engine.Read(crop), OcrPair.MinConfidence(engine)).Value);
    }

    [OnnxTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tracked_full_pdfs_preserve_native_results_and_independent_expected_names(bool knownLines)
        => CompareCorpus(TrackedSamples(), expectedCount: 5, knownLines, requireTrackedPositives: true);

    [OnnxCorpusTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Diverse_full_pdfs_preserve_native_results_and_never_accept_wrong_names(bool knownLines)
        => CompareCorpus(RequiredDirectory(DiverseSamplesVariable), expectedCount: 48, knownLines,
            requireTrackedPositives: false);

    [OnnxTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Integrated_crop_batches_preserve_field_identity_geometry_parsing_and_confidence(bool knownLines)
    {
        OcrComponents.ConfigureRuntime();
        var modelDirectory = RequiredModelDirectory();
        using var native = new PaddleEngine(OcrComponents.PaddleDir, useOnnx: false);
        using var onnx = new PaddleEngine(OcrComponents.PaddleDir, useOnnx: true, onnxModelsDir: modelDirectory);
        Assert.False(native.UsesOnnx);
        Assert.True(onnx.UsesOnnx);
        var files = Directory.GetFiles(Path.Combine(TrackedSamples(), "crops"), "*.png")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal).ToArray();
        Assert.Equal(11, files.Length);
        var groups = files.GroupBy(f => Path.GetFileName(f)[..2]).ToArray();
        Assert.Equal(5, groups.Length);
        foreach (var group in groups)
        {
            var paths = group.ToArray();
            var crops = paths.Select(LoadCrop).ToArray();
            try
            {
                OcrReading[] expected;
                OcrReading[] actual;
                if (knownLines)
                {
                    var before = native.ReadKnownLines(crops);
                    var after = onnx.ReadKnownLines(crops);
                    Assert.Equal(before.KnownLines, after.KnownLines);
                    expected = before.Readings;
                    actual = after.Readings;
                }
                else
                {
                    expected = native.ReadMany(crops);
                    actual = onnx.ReadMany(crops);
                }
                Assert.Equal(paths.Length, expected.Length);
                Assert.Equal(paths.Length, actual.Length);
                for (var field = 0; field < paths.Length; field++)
                {
                    var context = $"knownLines={knownLines}, {Path.GetFileName(paths[field])}";
                    AssertReadings(expected[field], actual[field], context);
                    var role = Enum.Parse<FieldRole>(Path.GetFileNameWithoutExtension(paths[field]).Split('-')[^1]);
                    Assert.Equal(Parse(role, expected[field], OcrPair.MinConfidence(native)),
                        Parse(role, actual[field], OcrPair.MinConfidence(onnx)));
                }
            }
            finally { foreach (var crop in crops) crop.Dispose(); }
        }
    }

    private static void CompareCorpus(string samplesRoot, int expectedCount, bool knownLines, bool requireTrackedPositives)
    {
        OcrComponents.ConfigureRuntime();
        var modelDirectory = RequiredModelDirectory();
        var manifestPath = Path.Combine(samplesRoot, "verification.json");
        Assert.True(File.Exists(manifestPath), $"Requested corpus manifest is missing: {manifestPath}");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var samples = manifest.RootElement.GetProperty("Samples").EnumerateArray().ToArray();
        Assert.Equal(expectedCount, samples.Length);
        var loads = TemplateLibrary.LoadFolder(Path.Combine(samplesRoot, "templates"));
        var templates = loads.Where(l => l.Template is not null).Select(l => l.Template!).ToArray();
        try
        {
            Assert.NotEmpty(templates);
            Assert.All(loads, l => Assert.True(l.Template is not null, l.Problem));
            using var native = OcrPair.CreateReal(OcrComponents.FindTessdata()!, useOnnx: false);
            using var onnx = OcrPair.CreateReal(OcrComponents.FindTessdata()!, useOnnx: true, onnxModelsDir: modelDirectory);
            native.PreferKnownLines = onnx.PreferKnownLines = knownLines;
            Assert.False(Assert.IsType<PaddleEngine>(native.Second).UsesOnnx);
            Assert.True(Assert.IsType<PaddleEngine>(onnx.Second).UsesOnnx);
            foreach (var sample in samples)
            {
                var relativePath = sample.GetProperty("File").GetString()!;
                var expectedName = sample.GetProperty("ExpectedName").GetString()!;
                var path = Path.Combine(samplesRoot, relativePath);
                Assert.True(File.Exists(path), $"Requested corpus PDF is missing: {path}");
                var hashBefore = SHA256.HashData(File.ReadAllBytes(path));
                var before = new List<RecognitionDiagnostic>();
                var after = new List<RecognitionDiagnostic>();
                // Match both backends against exactly the same geometry/RANSAC state.
                Cv2.SetTheRNG(1729UL);
                var expected = DocumentReader.Read(path, templates, native, CancellationToken.None, before.Add);
                Cv2.SetTheRNG(1729UL);
                var actual = DocumentReader.Read(path, templates, onnx, CancellationToken.None, after.Add);
                var context = $"knownLines={knownLines}, {relativePath}";

                Assert.True(expected.Values == actual.Values, $"{context}: native={expected.Values}, ONNX={actual.Values}");
                Assert.Equal(expected.Problem, actual.Problem);
                Assert.Equal(expected.Template, actual.Template);
                Assert.Equal(expected.Pages, actual.Pages);
                if (requireTrackedPositives && (knownLines || sample.GetProperty("RecognitionRequired").GetBoolean()))
                    Assert.True(actual.Values is not null, $"{context}: {actual.Problem}");
                if (actual.Values is { } values)
                    Assert.Equal(expectedName, FileNames.Build(values).Name);
                else
                    Assert.False(string.IsNullOrWhiteSpace(actual.Problem), context);

                var oldFields = before.Where(d => d.Field is not null).ToArray();
                var newFields = after.Where(d => d.Field is not null).ToArray();
                Assert.Equal(oldFields.Length, newFields.Length);
                for (var i = 0; i < oldFields.Length; i++)
                {
                    var a = oldFields[i];
                    var b = newFields[i];
                    Assert.Equal(a.PageNumber, b.PageNumber);
                    Assert.Equal(a.Field!.Role, b.Field!.Role);
                    Assert.Equal(a.Field.Bounds, b.Field.Bounds);
                    Assert.Equal(a.Field.CropPng, b.Field.CropPng);
                    Assert.Equal(a.Field.First, b.Field.First);
                    Assert.Equal(a.Field.Second, b.Field.Second);
                }
                Assert.Equal(hashBefore, SHA256.HashData(File.ReadAllBytes(path)));
            }
        }
        finally { foreach (var template in templates) template.Dispose(); }
    }

    private static void AssertReadings(OcrReading expected, OcrReading actual, string context)
    {
        Assert.Equal(expected.Engine, actual.Engine);
        Assert.Equal(expected.Lines.Count, actual.Lines.Count);
        for (var line = 0; line < expected.Lines.Count; line++)
        {
            var a = expected.Lines[line];
            var b = actual.Lines[line];
            Assert.Equal(a with { Confidence = 0 }, b with { Confidence = 0 });
            if (double.IsFinite(a.Confidence) && double.IsFinite(b.Confidence))
                Assert.True(Math.Abs(a.Confidence - b.Confidence) <= ScoreTolerance,
                    $"{context}, line {line}: confidence {a.Confidence:R} vs {b.Confidence:R}");
            else
                Assert.Equal(a.Confidence, b.Confidence); // Identical invalid direct scores must still require fallback.
        }
    }

    private static Parsed[] Parse(FieldRole role, OcrReading reading, double threshold)
    {
        if (role == FieldRole.Combined)
        {
            var (designation, name, type) = FieldParser.ParseCombined(reading, threshold);
            return [designation, name, type];
        }
        return [role switch
        {
            FieldRole.Designation => FieldParser.ParseDesignation(reading, threshold),
            FieldRole.Name => FieldParser.ParseName(reading, threshold),
            FieldRole.Type => FieldParser.ParseType(reading, threshold),
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        }];
    }

    private static string RequiredModelDirectory()
    {
        var path = RequiredDirectory(ModelsVariable);
        foreach (var name in new[] { "det.onnx", "rec.onnx" })
            Assert.True(File.Exists(Path.Combine(path, name)), $"Requested ONNX model is missing: {Path.Combine(path, name)}");
        return path;
    }

    private static string RequiredDirectory(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        Assert.False(string.IsNullOrWhiteSpace(value), $"Set {variable} to enable this integration test.");
        var path = Path.GetFullPath(value!);
        Assert.True(Directory.Exists(path), $"Requested {variable} directory does not exist: {path}");
        return path;
    }

    private static string TrackedSamples()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "docs", "test-samples");
            if (File.Exists(Path.Combine(path, "verification.json"))) return path;
        }
        throw new DirectoryNotFoundException("Tracked docs/test-samples folder was not found above the test assembly.");
    }

    private static Mat LoadCrop(string path)
    {
        var image = Cv2.ImRead(path, ImreadModes.Grayscale);
        if (image.Empty())
        {
            image.Dispose();
            throw new IOException($"Tracked crop could not be read: {path}");
        }
        return image;
    }
}

public class OnnxTheoryAttribute : TheoryAttribute
{
    public OnnxTheoryAttribute()
    {
        if (!OcrPlatform.Supported) Skip = OcrPlatform.Reason;
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OnnxBackendTests.ModelsVariable)))
            Skip = $"Experimental ONNX tests require {OnnxBackendTests.ModelsVariable}; production uses native Paddle by default.";
    }
}

public sealed class OnnxCorpusTheoryAttribute : OnnxTheoryAttribute
{
    public OnnxCorpusTheoryAttribute()
    {
        if (Skip is null && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OnnxBackendTests.DiverseSamplesVariable)))
            Skip = $"Optional 48-document corpus requires {OnnxBackendTests.DiverseSamplesVariable}.";
    }
}
