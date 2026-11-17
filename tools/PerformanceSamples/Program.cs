using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Core.Rename;
using DocumentTextRecognition.Core.Templates;
using DocumentTextRecognition.Tests.Samples;

// Diverse input-size/content guard for the performance run. Expected names are set before OCR.
// A refusal or incorrect name fails the entire generated corpus; no failing samples are filtered out.
var output = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/performance-diverse-samples");
var originalTemplates = Path.GetFullPath(args.Length > 1 ? args[1] : "docs/test-samples/templates");
if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
    throw new IOException("Output folder must be empty: " + output);
Directory.CreateDirectory(output);
var templateFolder = Path.Combine(output, "templates");
Directory.CreateDirectory(templateFolder);
Directory.CreateDirectory(Path.Combine(output, "scans"));
foreach (var path in Directory.EnumerateFiles(originalTemplates))
    if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".template.json", StringComparison.OrdinalIgnoreCase))
        File.Copy(path, Path.Combine(templateFolder, Path.GetFileName(path)));

OcrComponents.ConfigureRuntime();
var cases = new[]
{
    new Case(FormKind.Specification, "ВКЕМ.461411", 200, "Модуль связи", "Спецификация"),
    new Case(FormKind.Drawing, "ЮИЛГ.461411", 120, "Корпус", "Сборочный чертеж"),
    new Case(FormKind.Drawing, "ЮИЛГ.461412", 130, "Блок питания", "Схема электрическая"),
    new Case(FormKind.DrawingA3, "ПРМД.301261", 10, "Рама сварная", "Сборочный чертеж"),
    new Case(FormKind.Drawing, "ЮИЛГ.301318", 100, "Плата управления", "Сборочный чертеж"),
    new Case(FormKind.Drawing, "ЮИЛГ.461413", 140, "Крышка\nверхняя", "Габаритный чертеж"),
    new Case(FormKind.Drawing, "ВКЕМ.461411", 150, "Корпус", "Габаритный чертеж"),
    new Case(FormKind.Specification, "ВКЕМ.461412", 210, "Корпус", "Спецификация"),
};
var pending = new List<(string File, Case Case, string Designation, ScanOptions Scan, string ExpectedName)>();
using (var workbench = new Workbench())
{
    for (var i = 0; i < cases.Length; i++)
        for (var variant = 0; variant < 6; variant++)
        {
            var item = cases[i];
            var designation = $"{item.Prefix}.{item.Number + variant:D3}";
            var scan = new ScanOptions
            {
                Dpi = new[] { 200, 250, 300 }[variant % 3],
                QuarterTurns = variant >= 3 ? 1 : 0,
                SkewDegrees = variant >= 3 ? (variant % 2 == 0 ? 0.35 : -0.35) : 0,
                ShiftXmm = variant >= 3 ? 0.3 : 0,
                ShiftYmm = variant >= 3 ? -0.2 : 0,
                JpegQuality = 92,
            };
            var file = $"scans/{i + 1:D2}-{variant + 1:D2}-{item.Kind}.pdf";
            var generated = workbench.AddDocument(Path.GetFileName(file), item.Kind, designation, item.Name,
                scan, seed: 100 + i * 6 + variant, docType: item.Kind == FormKind.Specification ? null : item.Type);
            File.Copy(generated, Path.Combine(output, file));
            var expected = FileNames.Build(new FieldValues(designation, item.Name.Replace('\n', ' '), item.Type));
            if (expected.Name is null) throw new InvalidOperationException(expected.Problem);
            pending.Add((file, item, designation, scan, expected.Name));
        }
}

var loads = TemplateLibrary.LoadFolder(templateFolder);
if (loads.Any(l => l.Template is null)) throw new InvalidOperationException(string.Join("; ", loads.Select(l => l.Problem)));
var templates = loads.Select(l => l.Template!).ToArray();
var records = new List<object>();
var failures = 0;
try
{
    using var pair = OcrPair.CreateReal(OcrComponents.FindTessdata() ?? throw new InvalidOperationException("tessdata missing"));
    foreach (var sample in pending)
    {
        var diagnostics = new List<RecognitionDiagnostic>();
        var path = Path.Combine(output, sample.File);
        var result = DocumentReader.Read(path, templates, pair, CancellationToken.None, diagnostics.Add);
        var actual = result.Values is null ? null : FileNames.Build(result.Values).Name;
        var passed = actual == sample.ExpectedName;
        if (!passed) failures++;
        records.Add(new
        {
            sample.File,
            Kind = sample.Case.Kind.ToString(),
            sample.Scan,
            sample.ExpectedName,
            ActualName = actual,
            RecognitionRequired = true,
            Passed = passed,
            result.Problem,
            result.Template,
            Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
            ReadingDetails = diagnostics.Where(d => d.Field is not null).Select(d => new
            {
                Role = d.Field!.Role.ToString(), d.Field.First, d.Field.Second,
            }).ToArray(),
        });
        Console.WriteLine($"{sample.File}: {(passed ? "PASS" : "FAIL")} {actual ?? result.Problem}");
    }
}
finally { foreach (var template in templates) template.Dispose(); }

var report = new
{
    GeneratedUtc = DateTimeOffset.UtcNow,
    Environment = new { OS = RuntimeInformation.OSDescription, DotNet = RuntimeInformation.FrameworkDescription, Tesseract = TesseractEngine.Version() },
    Scope = "48 distinct synthetic single-page PDFs: eight content cases, three raster resolutions, upright/rotated+small skew, varied designation suffixes and geometry seeds. No production documents. All expected names declared before real two-engine OCR; no failures filtered out.",
    Passed = failures == 0,
    Failures = failures,
    Samples = records,
};
File.WriteAllText(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(report,
    new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + Environment.NewLine);
Console.WriteLine($"Diverse corpus: {pending.Count - failures}/{pending.Count} exact, report {Path.Combine(output, "verification.json")}");
return failures == 0 ? 0 : 1;

internal sealed record Case(FormKind Kind, string Prefix, int Number, string Name, string Type);
