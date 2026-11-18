using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Recognition;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;

// Isolated candidate comparison. Expected text is used only after inference has returned.
// Each invocation runs one model/backend so peak RSS is attributable to that process.
var options = Enumerable.Range(0, args.Length / 2).ToDictionary(i => args[i * 2], i => args[i * 2 + 1]);
string Option(string key, string fallback) => options.GetValueOrDefault(key, fallback);
var engineName = Option("--engine", "eslav-onnx");
var profileName = Option("--profile", "field");
var knownLines = bool.Parse(Option("--known-lines", "false"));
var repeats = int.Parse(Option("--repeats", "3"));
var root = Path.GetFullPath(Option("--root", Directory.GetCurrentDirectory()));
var inputPath = Path.GetFullPath(Option("--cases", Path.Combine(root, "tools/OcrComparison/input-cases.json")));
var outputPath = Path.GetFullPath(Option("--output", Path.Combine(root, "artifacts/ocr-candidates/" + engineName + "-" + profileName + ".json")));
var jsonOptions = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
using var manifest = JsonDocument.Parse(File.ReadAllBytes(inputPath));
var cases = JsonSerializer.Deserialize<Case[]>(manifest.RootElement.GetProperty("Cases").GetRawText(), jsonOptions)!;
if (options.TryGetValue("--normalize-external", out var externalFile))
{
    var report = JsonNode.Parse(File.ReadAllText(externalFile))!;
    foreach (var row in report["Results"]!.AsArray())
    {
        var item = cases.Single(c => c.Id == row!["Id"]!.GetValue<string>());
        var reading = row!["Reading"]!.Deserialize<OcrReading>(jsonOptions)!;
        var parsed = Parse(item.Role, reading, 0);
        row["SharedParserIgnoringThreshold"] = JsonSerializer.SerializeToNode(parsed, jsonOptions);
        row["NormalizedExact"] = parsed.Value == item.Expected;
        // Candidate scores are not calibrated to a production acceptance threshold.
        // This is recognition evidence only; no rename or two-engine decision is made.
    }
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    File.WriteAllText(outputPath, report.ToJsonString(jsonOptions));
    return 0;
}
if (repeats < 1 || profileName is not ("field" or "general")) throw new ArgumentException("Invalid repeats or profile.");
OcrComponents.ConfigureRuntime();
Cv2.SetNumThreads(1);
if (engineName == "gost-specialist")
{
    var report = GostSpecialistProbe.Run(root, cases, bool.Parse(Option("--known-lines", "true")));
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    File.WriteAllText(outputPath, JsonSerializer.Serialize(report, jsonOptions));
    return 0;
}
if (Option("--compare-tensors", "false") == "true")
{
    var tensors = TensorComparison.Run(root, cases, Option("--candidate-rec", Path.Combine(root,
        "artifacts/ocr-candidates/models/cyrillic_PP-OCRv5_mobile_rec_infer")), Option("--candidate-onnx", Path.Combine(root,
        "artifacts/ocr-candidates/cyrillic-onnx/rec.onnx")));
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    File.WriteAllText(outputPath, JsonSerializer.Serialize(tensors, jsonOptions));
    return 0;
}
var started = Stopwatch.GetTimestamp();
using var engine = CreateEngine();
var initializationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
var results = new List<object>();
foreach (var item in cases)
{
    var path = Path.Combine(root, item.File);
    var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    if (!hash.Equals(item.CropSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Input checksum mismatch: " + item.File);
    using var crop = Cv2.ImRead(path, ImreadModes.Grayscale);
    if (crop.Empty()) throw new InvalidDataException(item.File);
    var profile = profileName == "field" && item.Role == "Designation" ? OcrTextProfile.CyrillicDesignation : OcrTextProfile.General;
    var coldStarted = Stopwatch.GetTimestamp();
    var reading = Read(engine, crop, profile, knownLines);
    var coldMs = Stopwatch.GetElapsedTime(coldStarted).TotalMilliseconds;
    var times = new List<double>();
    for (var repeat = 0; repeat < repeats; repeat++)
    {
        var sampleStarted = Stopwatch.GetTimestamp();
        var repeated = Read(engine, crop, profile, knownLines);
        times.Add(Stopwatch.GetElapsedTime(sampleStarted).TotalMilliseconds);
        if (repeated.Text != reading.Text) throw new InvalidOperationException("Repeated input changed output: " + item.Id);
    }
    var threshold = engine is TesseractEngine ? .30 : .50;
    var parsed = Parse(item.Role, reading, threshold);
    // Also report normalization independently from acceptance/confidence, without using it to accept.
    var normalized = Parse(item.Role, reading, 0);
    var exact = parsed.Value == item.Expected;
    results.Add(new { item.Id, item.File, item.Source, CropSha256 = hash, item.Role, item.Expected, Profile = profile.ToString(),
        Raw = reading.Text, Reading = reading, Parsed = parsed, NormalizedIgnoringThreshold = normalized.Value,
        Threshold = threshold, Exact = exact, RawExact = reading.Text.Replace('\n', ' ') == item.Expected,
        KnownLine = (engine as CandidatePaddle)?.LastKnownLine,
        RecognitionRegions = (engine as CandidatePaddle)?.LastRegions,
        ColdMilliseconds = coldMs, WarmMilliseconds = times, MedianMilliseconds = times.Order().ElementAt(times.Count / 2),
        WorkingSetBytes = Process.GetCurrentProcess().WorkingSet64, PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64 });
    Console.WriteLine($"{engineName}/{profileName} {item.Id}: {reading.Text.Replace('\n', '|')} conf={reading.MinConfidence:F4} exact={exact} {coldMs:F1}ms");
}
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
File.WriteAllText(outputPath, JsonSerializer.Serialize(new { SchemaVersion = 1, Engine = engineName, Profile = profileName, KnownLinesRequested = knownLines,
    Model = engine is CandidatePaddle ? Option("--candidate-rec", "") : OcrComponents.PaddleRecognizer,
    InitializationMilliseconds = initializationMs, Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription, LogicalProcessors = Environment.ProcessorCount,
    InputManifestSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(inputPath))), Results = results,
    PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64 }, jsonOptions));
return 0;

IDisposable CreateEngine() => engineName switch
{
    "eslav-onnx" => new PaddleEngine(OcrComponents.PaddleDir, useOnnx: true),
    "eslav-native" => new PaddleEngine(OcrComponents.PaddleDir, useOnnx: false),
    "cyrillic-native" or "cyrillic-onnx" => new CandidatePaddle(Option("--candidate-rec", Path.Combine(root,
        "artifacts/ocr-candidates/models/cyrillic_PP-OCRv5_mobile_rec_infer")),
        engineName == "cyrillic-onnx" ? Option("--candidate-onnx", Path.Combine(root,
            "artifacts/ocr-candidates/cyrillic-onnx/rec.onnx")) : null),
    "tesseract" => new TesseractEngine(OcrComponents.FindTessdata()!),
    _ => throw new ArgumentException("Unknown engine " + engineName),
};
static OcrReading Read(IDisposable engine, Mat crop, OcrTextProfile profile, bool known) => engine switch
{
    PaddleEngine paddle => known ? paddle.ReadKnownLines([crop], profiles: [profile]).Readings[0] : paddle.Read(crop, null, profile),
    CandidatePaddle candidate => candidate.Read(crop, profile, known),
    TesseractEngine tess => tess.Read(crop, profile),
    _ => throw new ArgumentException(nameof(engine)),
};
static Parsed Parse(string role, OcrReading reading, double threshold) => role switch
{
    "Designation" => FieldParser.ParseDesignation(reading, threshold),
    "Name" => FieldParser.ParseName(reading, threshold),
    _ => throw new ArgumentException("Unsupported role " + role),
};
internal sealed record Case(string Id, string File, string Role, string Expected, string Source, string CropSha256);

internal sealed class CandidatePaddle : IDisposable
{
    private readonly WorkerPaddleDetector? _detector;
    private readonly WorkerPaddleRecognizer? _recognizer;
    private readonly WorkerOnnxDetector? _onnxDetector;
    private readonly WorkerOnnxRecognizer? _onnxRecognizer;
    internal object[] LastRegions { get; private set; } = [];
    internal bool LastKnownLine { get; private set; }
    internal CandidatePaddle(string recognitionDirectory, string? onnxRecognizer)
    {
        if (onnxRecognizer is not null)
        {
            _onnxDetector = new WorkerOnnxDetector(Path.Combine(OcrComponents.ProgramDir, "ocr/onnx/det.onnx"))
                { UnclipRatio = PaddleEngine.UnclipRatio };
            try { _onnxRecognizer = new WorkerOnnxRecognizer(RecognizationModel.FromDirectoryV5(recognitionDirectory), onnxRecognizer); }
            catch { _onnxDetector.Dispose(); throw; }
            return;
        }
        var device = PaddleDevice.OneDnn(cacheCapacity: 10, cpuMathThreadCount: 1);
        _detector = new WorkerPaddleDetector(DetectionModel.FromDirectory(
            Path.Combine(OcrComponents.PaddleDir, OcrComponents.PaddleDetector), ModelVersion.V5), device)
            { UnclipRatio = PaddleEngine.UnclipRatio };
        try { _recognizer = new WorkerPaddleRecognizer(RecognizationModel.FromDirectoryV5(recognitionDirectory), device); }
        catch { _detector.Dispose(); throw; }
    }
    internal OcrReading Read(Mat gray, OcrTextProfile profile, bool knownLines)
    {
        using var bgr = gray.CvtColor(ColorConversionCodes.GRAY2BGR);
        LastKnownLine = false;
        RotatedRect[] rectangles;
        if (knownLines && KnownSingleLineRegion.TryGetBounds(gray, out var bounds))
        {
            LastKnownLine = true;
            rectangles = [new RotatedRect(new Point2f(bounds.X + bounds.Width / 2f, bounds.Y + bounds.Height / 2f),
                new Size2f(bounds.Width, bounds.Height), 0)];
        }
        else rectangles = _onnxDetector?.Run(bgr) ?? _detector!.Run(bgr);
        var crops = new List<Mat>();
        try
        {
            foreach (var rectangle in rectangles)
            {
                // KnownSingleLineRegion's exact rectangle is used in the production shortcut.
                var box = LastKnownLine ? new Rect((int)(rectangle.Center.X - rectangle.Size.Width / 2),
                    (int)(rectangle.Center.Y - rectangle.Size.Height / 2), (int)rectangle.Size.Width, (int)rectangle.Size.Height)
                    : rectangle.BoundingRect();
                var clipped = Rect.FromLTRB(Math.Clamp(box.Left, 0, bgr.Width), Math.Clamp(box.Top, 0, bgr.Height),
                    Math.Clamp(box.Right, 0, bgr.Width), Math.Clamp(box.Bottom, 0, bgr.Height));
                crops.Add(new Mat(bgr, clipped));
            }
            var profiles = Enumerable.Repeat(profile, crops.Count).ToArray();
            var readings = _onnxRecognizer?.Run(crops.ToArray(), profiles: profiles) ?? _recognizer!.Run(crops.ToArray(), profiles: profiles);
            LastRegions = readings.Select((r, i) => (object)new { r.Text, r.Score,
                Rect = new { rectangles[i].Center.X, rectangles[i].Center.Y, rectangles[i].Size.Width, rectangles[i].Size.Height, rectangles[i].Angle },
                Characters = r.Chars.Select(c => new { c.Character, c.Score, c.Index }).ToArray() }).ToArray();
            return PaddleEngine.Collect(readings.Select((r, i) => new PaddleOcrResultRegion(rectangles[i], r.Text,
                LastKnownLine ? PaddleEngine.KnownLineConfidence(r) : r.Score, r.Chars)), profile);
        }
        finally { foreach (var crop in crops) crop.Dispose(); }
    }
    public void Dispose() { _onnxRecognizer?.Dispose(); _onnxDetector?.Dispose(); _recognizer?.Dispose(); _detector?.Dispose(); }
}
