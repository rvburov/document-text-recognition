using System.Reflection;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Core.Rename;
using DocumentTextRecognition.Core.Templates;
using OpenCvSharp;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: PerformanceVerify <samples-folder> <output.json>");
    return 2;
}
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
    using var pair = OcrPair.CreateReal(OcrComponents.FindTessdata()!);
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
    RngSeed = 1729, Correct = correct, Rejected = rejected, Incorrect = incorrect, Samples = results
}, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
Console.WriteLine($"Correct={correct}; refused={rejected}; incorrect={incorrect}");
return incorrect == 0 ? 0 : 1;
