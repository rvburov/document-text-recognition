using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.OnnxPerformance;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;

if (args.Length is < 4 or > 5)
{
    Console.Error.WriteLine("Usage: OnnxPerformance <paddle-models> <crop-pngs> <onnx-models> <output.json> [repeats=5]");
    return 2;
}
var repeats = args.Length == 5 ? int.Parse(args[4]) : 5;
if (repeats < 1) throw new ArgumentOutOfRangeException(nameof(repeats));
OcrComponents.ConfigureRuntime();
Cv2.SetNumThreads(1);
const double scoreTolerance = 1e-5; // Differential reporting only; production thresholds remain unchanged.
var files = Directory.GetFiles(args[1], "*.png").OrderBy(Path.GetFileName, StringComparer.Ordinal).ToArray();
if (files.Length != 11) throw new InvalidOperationException("Expected the 11 tracked docs/test-samples/crops PNGs.");
var groups = files.Select((path, index) => (path, index)).GroupBy(x => Path.GetFileName(x.path)[..2]).ToArray();
if (groups.Length != 5) throw new InvalidOperationException("Expected document prefixes 01 through 05.");
var model = new FullOcrModel(DetectionModel.FromDirectory(Path.Combine(args[0], "mobile_zh_det"), ModelVersion.V5),
    RecognizationModel.FromDirectoryV5(Path.Combine(args[0], "eslav_PP_OCRv5_mobile_rec")));
var device = PaddleDevice.OneDnn(cacheCapacity: 10, cpuMathThreadCount: 1);
using var referenceDetector = new WorkerPaddleDetector(model.DetectionModel, device) { UnclipRatio = PaddleEngine.UnclipRatio };
using var referenceRecognizer = new WorkerPaddleRecognizer(model.RecognizationModel, device);
using var detector = new OnnxDetector(model.DetectionModel, Path.Combine(args[2], "det.onnx")) { UnclipRatio = PaddleEngine.UnclipRatio };
using var recognizer = new OnnxRecognizer(model.RecognizationModel, Path.Combine(args[2], "rec.onnx"));
using var paddleBatch = new PaddleEngine(args[0], useOneDnn: true, reusePredictors: true, useOnnx: false);
using var onnxBatch = new OnnxEngine(model, args[2]);
var images = files.Select(f => Cv2.ImRead(f, ImreadModes.Color)).ToArray();
var grays = files.Select(f => Cv2.ImRead(f, ImreadModes.Grayscale)).ToArray();
var fieldRows = new List<object>();
var batchRows = new List<object>();
bool passed = true;
double fieldsPaddleMs = 0, fieldsOnnxMs = 0, batchesPaddleMs = 0, batchesOnnxMs = 0;
try
{
    for (var i = 0; i < images.Length; i++)
    {
        var measurements = Measure(() => RunReference(images[i]), () => RunOnnx(images[i]), repeats);
        var comparisons = measurements.Results.Select(r => CompareRegions(r.Expected, r.Actual)).ToArray();
        var ok = comparisons.All(c => c.SameText && c.SameBoxes && c.ScoreMaxDelta is <= scoreTolerance);
        passed &= ok;
        fieldsPaddleMs += measurements.PaddleMilliseconds;
        fieldsOnnxMs += measurements.OnnxMilliseconds;
        fieldRows.Add(new { File = Path.GetFileName(files[i]), Passed = ok, measurements.PaddleMilliseconds,
            measurements.OnnxMilliseconds, Repetitions = comparisons });
    }
    foreach (var group in groups)
    {
        var batch = group.Select(x => grays[x.index]).ToArray();
        var paths = group.Select(x => x.path).ToArray();
        var measurements = Measure(() => paddleBatch.ReadMany(batch), () => onnxBatch.ReadMany(batch), repeats);
        var comparisonRows = new List<object>();
        bool batchPassed = true;
        foreach (var (expected, actual) in measurements.Results)
        {
            if (expected.Length != paths.Length || actual.Length != paths.Length)
                throw new InvalidOperationException("A backend changed the batch field count.");
            for (var i = 0; i < paths.Length; i++)
            {
                var a = expected[i];
                var b = actual[i];
                var sameText = a.Lines.Select(l => l.Text).SequenceEqual(b.Lines.Select(l => l.Text));
                var sameBoxes = a.Lines.Select(l => (l.Left, l.Top, l.Right, l.Bottom))
                    .SequenceEqual(b.Lines.Select(l => (l.Left, l.Top, l.Right, l.Bottom)));
                double? delta = a.Lines.Count == b.Lines.Count
                    ? a.Lines.Zip(b.Lines).Select(p => ScoreDelta(p.First.Confidence, p.Second.Confidence)).DefaultIfEmpty(0).Max() : null;
                var role = Path.GetFileNameWithoutExtension(paths[i]).Split('-')[^1];
                var parsedExpected = Parse(role, a, OcrPair.MinConfidence(paddleBatch));
                var parsedActual = Parse(role, b, OcrPair.MinConfidence(paddleBatch));
                var sameParsing = parsedExpected.SequenceEqual(parsedActual);
                var ok = sameText && sameBoxes && delta is <= scoreTolerance && sameParsing;
                batchPassed &= ok;
                comparisonRows.Add(new { File = Path.GetFileName(paths[i]), Role = role, Passed = ok, SameText = sameText,
                    SameBoxes = sameBoxes, ScoreMaxDelta = delta, SameParsing = sameParsing,
                    Expected = a.Text, Actual = b.Text, ParsedExpected = parsedExpected, ParsedActual = parsedActual });
            }
        }
        passed &= batchPassed;
        batchesPaddleMs += measurements.PaddleMilliseconds;
        batchesOnnxMs += measurements.OnnxMilliseconds;
        batchRows.Add(new { Document = group.Key, Fields = paths.Select(Path.GetFileName).ToArray(), Passed = batchPassed,
            measurements.PaddleMilliseconds, measurements.OnnxMilliseconds, Comparisons = comparisonRows });
    }
}
finally
{
    foreach (var image in images) image.Dispose();
    foreach (var gray in grays) gray.Dispose();
}
var report = new
{
    SchemaVersion = 1, RecordedUtc = DateTimeOffset.UtcNow,
    Scope = "Isolated FP32 backend comparison on 11 tracked synthetic field crops, grouped into five documents. No PDF rendering, matching, Tesseract or renaming is timed. Negative screenshot-like input is retained; equality does not imply correct transcription.",
    Environment = new { OS = RuntimeInformation.OSDescription, DotNet = RuntimeInformation.FrameworkDescription,
        ProcessorCount = Environment.ProcessorCount, Cpu = Environment.GetEnvironmentVariable("DTR_BENCHMARK_CPU"),
        OpenCvThreads = Cv2.GetNumThreads(), OmpThreadLimit = Environment.GetEnvironmentVariable("OMP_THREAD_LIMIT"),
        OmpNumThreads = Environment.GetEnvironmentVariable("OMP_NUM_THREADS") },
    Configuration = new { Repeats = repeats, OneDnn = true, IntraOpThreads = 1, InterOpThreads = 1,
        ManagedOnnxRuntime = "1.11.0", BundledNativeOnnxRuntime = "1.11.1 from Paddle 3.3.1.70", ScoreTolerance = scoreTolerance,
        Warmup = "Each input runs once on both backends before timing. Timed order alternates on each repetition." },
    Inputs = files.Select(p => new { File = Path.GetFileName(p), Sha256 = Sha(p) }),
    Models = new[] { "det.onnx", "rec.onnx" }.Select(n => new { File = n, Sha256 = Sha(Path.Combine(args[2], n)) }),
    Quality = new { Passed = passed },
    Totals = new { FieldPaddleMilliseconds = fieldsPaddleMs, FieldOnnxMilliseconds = fieldsOnnxMs,
        FieldSpeedup = fieldsPaddleMs / fieldsOnnxMs, BatchPaddleMilliseconds = batchesPaddleMs,
        BatchOnnxMilliseconds = batchesOnnxMs, BatchSpeedup = batchesPaddleMs / batchesOnnxMs },
    Fields = fieldRows, Batches = batchRows,
};
var output = Path.GetFullPath(args[3]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + Environment.NewLine);
Console.WriteLine($"Quality={(passed ? "PASS" : "FAIL")}; fields {fieldsPaddleMs:F1} → {fieldsOnnxMs:F1} ms ({fieldsPaddleMs / fieldsOnnxMs:F3}x); batches {batchesPaddleMs:F1} → {batchesOnnxMs:F1} ms ({batchesPaddleMs / batchesOnnxMs:F3}x)");
Console.WriteLine(output);
return passed ? 0 : 1;

PaddleOcrResultRegion[] RunOnnx(Mat image) => RunRegions(image, detector.Run, recognizer.Run);
PaddleOcrResultRegion[] RunReference(Mat image) => RunRegions(image, referenceDetector.Run, referenceRecognizer.Run);

static PaddleOcrResultRegion[] RunRegions(Mat image, Func<Mat, RotatedRect[]> detect,
    Func<Mat[], int, PaddleOcrRecognizerResult[]> recognize)
{
    var rectangles = detect(image);
    var pieces = rectangles.Select(r => new Mat(image, r.BoundingRect().Intersect(new Rect(0, 0, image.Width, image.Height)))).ToArray();
    try { return recognize(pieces, 0).Select((r, i) => new PaddleOcrResultRegion(rectangles[i], r.Text, r.Score, r.Chars)).ToArray(); }
    finally { foreach (var piece in pieces) piece.Dispose(); }
}

static Measurement<T> Measure<T>(Func<T> paddle, Func<T> onnx, int repeats)
{
    paddle(); onnx();
    double a = 0, b = 0;
    var results = new List<(T Expected, T Actual)>();
    for (var i = 0; i < repeats; i++)
    {
        T expected, actual;
        if (i % 2 == 0) { expected = Timed(paddle, ref a); actual = Timed(onnx, ref b); }
        else { actual = Timed(onnx, ref b); expected = Timed(paddle, ref a); }
        results.Add((expected, actual));
    }
    return new(a / repeats, b / repeats, results);
}
static T Timed<T>(Func<T> run, ref double milliseconds)
{
    var start = Stopwatch.GetTimestamp();
    var result = run();
    milliseconds += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    return result;
}
static RegionComparison CompareRegions(PaddleOcrResultRegion[] a, PaddleOcrResultRegion[] b) => new(
    a.Select(r => r.Text).SequenceEqual(b.Select(r => r.Text)), a.Select(r => r.Rect).SequenceEqual(b.Select(r => r.Rect)),
    a.Length == b.Length ? a.Zip(b).Select(p => ScoreDelta(p.First.Score, p.Second.Score)).DefaultIfEmpty(0).Max() : null,
    a.Select(r => r.Text).ToArray(), b.Select(r => r.Text).ToArray());
static double ScoreDelta(double a, double b) => a.Equals(b) ? 0 : Math.Abs(a - b);
static string Sha(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
static Parsed[] Parse(string role, OcrReading reading, double threshold)
{
    if (role == "Combined")
    {
        var (d, n, t) = FieldParser.ParseCombined(reading, threshold);
        return [d, n, t];
    }
    return [role switch
    {
        "Designation" => FieldParser.ParseDesignation(reading, threshold),
        "Name" => FieldParser.ParseName(reading, threshold),
        "Type" => FieldParser.ParseType(reading, threshold),
        _ => throw new InvalidOperationException($"Unknown role {role}"),
    }];
}
internal sealed record RegionComparison(bool SameText, bool SameBoxes, double? ScoreMaxDelta, string[] Expected, string[] Actual);
internal sealed record Measurement<T>(double PaddleMilliseconds, double OnnxMilliseconds, IReadOnlyList<(T Expected, T Actual)> Results);
