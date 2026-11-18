using System.Reflection;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Core.Rename;
using DocumentTextRecognition.Core.Templates;
using OpenCvSharp;

if (args.Length < 2 || args.Length % 2 != 0)
{
    Console.Error.WriteLine("Usage: PerformanceVerify <samples-folder> <output.json> [--known-lines true|false] [--onnx true|false --onnx-models directory]");
    return 2;
}
var options = new Dictionary<string, string>();
for (var i = 2; i < args.Length; i += 2)
{
    if (args[i] is not ("--known-lines" or "--onnx" or "--onnx-models"))
        throw new ArgumentException("Unknown option " + args[i]);
    options[args[i]] = args[i + 1];
}
bool? knownLines = options.TryGetValue("--known-lines", out var lines) ? bool.Parse(lines) : null;
bool? onnx = options.TryGetValue("--onnx", out var backend) ? bool.Parse(backend) : null;
var onnxModels = options.GetValueOrDefault("--onnx-models");
if (onnxModels is not null && onnx != true) throw new ArgumentException("--onnx-models requires --onnx true.");
bool? resolvedKnownLines = null, resolvedUsesOnnx = null;
var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
typeof(OcrComponents).GetMethod("ConfigureRuntime", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, null);
var root = Path.GetFullPath(args[0]);
using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "verification.json")));
var templates = TemplateLibrary.LoadFolder(Path.Combine(root, "templates")).Select(l => l.Template ?? throw new Exception(l.Problem)).ToArray();
var results = new List<object>();
int correct = 0, rejected = 0, incorrect = 0;
try
{
    var factory = typeof(OcrPair).GetMethod("CreateReal", BindingFlags.Public | BindingFlags.Static)!;
    var parameters = factory.GetParameters();
    if (onnx == true && parameters.Length < 4) throw new NotSupportedException("This core build has no ONNX option.");
    if (onnxModels is not null && parameters.Length < 5) throw new NotSupportedException("This core build has no ONNX model path option.");
    var arguments = parameters.Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
    arguments[0] = OcrComponents.FindTessdata()!;
    if (onnx is { } selected && parameters.Length > 3) arguments[3] = selected;
    if (onnxModels is not null && parameters.Length > 4) arguments[4] = onnxModels;
    using var pair = (OcrPair)factory.Invoke(null, arguments)!;
    resolvedUsesOnnx = pair.Second.GetType().GetProperty("UsesOnnx", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(pair.Second) as bool?;
    var knownLinesProperty = typeof(OcrPair).GetProperty("PreferKnownLines", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    if (knownLines is { } preferKnownLines)
    {
        if (knownLinesProperty is null && preferKnownLines)
            throw new NotSupportedException("This core build does not support known text lines.");
        knownLinesProperty?.SetValue(pair, preferKnownLines);
    }
    resolvedKnownLines = knownLinesProperty?.GetValue(pair) as bool?;
    foreach (var sample in document.RootElement.GetProperty("Samples").EnumerateArray())
    {
        var file = sample.GetProperty("File").GetString()!;
        var expected = sample.GetProperty("ExpectedName").GetString()!;
        var diagnostics = new List<RecognitionDiagnostic>();
        Cv2.SetTheRNG(1729UL);
        var result = DocumentReader.Read(Path.Combine(root, file), templates, pair, CancellationToken.None, diagnostics.Add);
        var actual = result.Values is null ? null : FileNames.Build(result.Values).Name;
        if (actual == expected) correct++; else if (actual is null) rejected++; else incorrect++;
        results.Add(new {
            File = file, ExpectedName = expected, ActualName = actual, Correct = actual == expected, result.Problem,
            ReadingDetails = diagnostics.Where(d => d.Field is not null).Select(d => new {
                Role = d.Field!.Role.ToString(), d.Field.First, d.Field.Second,
                CropSha256 = Convert.ToHexString(SHA256.HashData(d.Field.CropPng))
            }).ToArray()
        });
        Console.WriteLine($"{file}: {(actual == expected ? "PASS" : actual is null ? "REFUSED" : "INCORRECT")} {actual ?? result.Problem}");
    }
}
finally { foreach (var t in templates) t.Dispose(); }
File.WriteAllText(output, JsonSerializer.Serialize(new {
    RecordedUtc = DateTimeOffset.UtcNow, CoreSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(OcrPair).Assembly.Location))),
    Configuration = new { KnownLines = knownLines, ResolvedKnownLines = resolvedKnownLines, Onnx = onnx, OnnxModels = onnxModels, ResolvedUsesOnnx = resolvedUsesOnnx },
    RngSeed = 1729, Correct = correct, Rejected = rejected, Incorrect = incorrect, Samples = results
}, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
Console.WriteLine($"Correct={correct}; refused={rejected}; incorrect={incorrect}");
return incorrect == 0 ? 0 : 1;
