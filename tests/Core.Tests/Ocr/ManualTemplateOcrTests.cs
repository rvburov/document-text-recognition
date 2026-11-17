using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Pdf;
using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Core.Rename;
using DocumentTextRecognition.Core.Templates;
using DocumentTextRecognition.Tests.Samples;
using OpenCvSharp;

namespace DocumentTextRecognition.Tests.Ocr;

/// <summary>Настоящий PDF → сохранённые области → совмещение → два OCR → проверенное имя.</summary>
[Collection("OCR")]
public sealed class ManualTemplateOcrTests(OcrFixture ocr)
{
    [OcrFact]
    public void Saved_regions_read_specification_drawing_and_schematic_and_record_sample_pdfs()
    {
        using var wb = new Workbench();
        foreach (var sample in ManualTemplateSamples.Cases.DistinctBy(s => s.TemplateName))
            ManualTemplateSamples.AddTemplate(wb, sample);
        var loads = TemplateLibrary.LoadFolder(wb.Templates);
        var templates = RequireTemplates(loads);
        var records = new List<object>();
        var crops = new List<(string FileName, byte[] Png)>();
        try
        {
            foreach (var sample in ManualTemplateSamples.Cases)
            {
                var path = ManualTemplateSamples.AddDocument(wb, sample);
                var original = SHA256.HashData(File.ReadAllBytes(path));
                var diagnostics = new List<RecognitionDiagnostic>();
                var result = DocumentReader.Read(path, templates, ocr.Pair, CancellationToken.None, diagnostics.Add);
                string? name = null;
                if (result.Values is not null)
                {
                    Assert.Equal(new FieldValues(sample.Designation, sample.Name, sample.Type), result.Values);
                    Assert.Equal(sample.TemplateName, result.Template);
                    var built = FileNames.Build(result.Values);
                    Assert.Null(built.Problem);
                    Assert.Equal(sample.ExpectedName, built.Name);
                    name = built.Name;
                }
                else
                {
                    // Четыре положительных образца обязаны распознаться. Пятый намеренно
                    // сохраняет известную трудность OCR: Paddle может терять ')' в «Э(Т)О».
                    // Улучшение движков допустимо, но любое принятое имя обязано быть точным.
                    Assert.True(sample.AllowOcrRefusal, $"{sample.FileName}: {result.Problem}");
                    Assert.Contains("наименование прочитано по-разному", result.Problem);
                    Assert.Equal(2, diagnostics.Count(d => d.Field is not null));
                    var designation = Assert.Single(diagnostics, d => d.Field?.Role == FieldRole.Designation).Field!;
                    Assert.Equal(sample.Designation, designation.First.ParsedText);
                    Assert.Equal(sample.Designation, designation.Second.ParsedText);
                    Assert.Null(designation.First.Problem);
                    Assert.Null(designation.Second.Problem);
                    var reading = Assert.Single(diagnostics, d => d.Field?.Role == FieldRole.Name).Field!;
                    Assert.Null(reading.First.Problem);
                    Assert.Null(reading.Second.Problem);
                    Assert.NotEqual(reading.First.ParsedText, reading.Second.ParsedText);
                    Assert.True(reading.First.ParsedText == sample.Name || reading.Second.ParsedText == sample.Name);
                }
                Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(path)));
                var fields = diagnostics.Where(d => d.Field is not null).Select(d =>
                {
                    var field = d.Field!;
                    var cropName = $"{Path.GetFileNameWithoutExtension(sample.FileName)}-{field.Role}.png";
                    crops.Add((cropName, field.CropPng));
                    return new
                    {
                        d.PageNumber,
                        Role = field.Role.ToString(),
                        field.Bounds,
                        Crop = "crops/" + cropName,
                        field.First,
                        field.Second,
                    };
                }).ToArray();
                records.Add(new
                {
                    File = "scans/" + sample.FileName,
                    Template = "templates/" + sample.TemplateName + ".pdf",
                    ExpectedName = sample.ExpectedName,
                    ActualName = name,
                    Fields = result.Values,
                    Engines = new[] { ocr.Pair.First.Name, ocr.Pair.Second.Name },
                    ReadingDetails = fields,
                    Stages = diagnostics.Select(d => new { d.Stage, d.PageNumber, d.Message }).ToArray(),
                    RecognitionRequired = !sample.AllowOcrRefusal,
                    Result = result.Values is not null ? "recognized" : "skipped",
                    result.Problem,
                });
            }

            // Явный режим генерации примеров для ручной проверки. Обычный прогон пишет только
            // во временную папку; опубликованный набор создаётся лишь после всех утверждений.
            var output = Environment.GetEnvironmentVariable("DTR_TEST_SAMPLES_OUTPUT");
            if (!string.IsNullOrWhiteSpace(output))
            {
                CopyFolder(wb.Templates, Path.Combine(output, "templates"));
                CopyFolder(wb.Source, Path.Combine(output, "scans"));
                Directory.CreateDirectory(Path.Combine(output, "crops"));
                foreach (var crop in crops)
                    File.WriteAllBytes(Path.Combine(output, "crops", crop.FileName), crop.Png);
                ExportTemplatePreviews(templates, Path.Combine(output, "previews"));
                var json = JsonSerializer.Serialize(new
                {
                    Test = nameof(Saved_regions_read_specification_drawing_and_schematic_and_record_sample_pdfs),
                    VerifiedUtc = DateTimeOffset.UtcNow,
                    Environment = new
                    {
                        OperatingSystem = RuntimeInformation.OSDescription,
                        DotNet = RuntimeInformation.FrameworkDescription,
                        Tesseract = TesseractEngine.Version(),
                        NativeLibraryPreload = System.Environment.GetEnvironmentVariable("LD_PRELOAD")?
                            .Split(':', StringSplitOptions.RemoveEmptyEntries).Select(Path.GetFileName).ToArray() ?? [],
                    },
                    Scope = "Синтетические PDF; чтение настоящими Tesseract и PaddleOCR. Не оценка точности на реальных сканах.",
                    Samples = records,
                }, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                File.WriteAllText(Path.Combine(output, "verification.json"), json + Environment.NewLine);
            }
        }
        finally { foreach (var t in templates) t.Dispose(); }
    }

    [OcrTheory]
    [InlineData(0, 1.2, 1.0, -0.8)]
    [InlineData(1, 0.0, 0.0, 0.0)]
    public void Saved_regions_follow_shifted_and_rotated_scan(int turns, double skew, double shiftX, double shiftY)
    {
        using var wb = new Workbench();
        var sample = ManualTemplateSamples.Cases[1];
        var templatePath = ManualTemplateSamples.AddTemplate(wb, sample);
        var load = TemplateLibrary.Load(templatePath);
        Assert.True(load.Template is not null, load.Problem);
        using var template = load.Template!;
        var scan = ManualTemplateSamples.AddDocument(wb, sample, new ScanOptions
        {
            Dpi = 250, QuarterTurns = turns, SkewDegrees = skew, ShiftXmm = shiftX, ShiftYmm = shiftY,
        });
        var result = DocumentReader.Read(scan, [template], ocr.Pair, CancellationToken.None);
        Assert.True(result.Values is not null, result.Problem);
        Assert.Equal(new FieldValues(sample.Designation, sample.Name, sample.Type), result.Values);
    }

    [OcrFact]
    public void Saved_second_template_page_reads_document_after_cover()
    {
        using var wb = new Workbench();
        var sample = ManualTemplateSamples.Cases[0];
        var templatePath = ManualTemplateSamples.AddTemplate(wb, sample, pageIndex: 1);
        var load = TemplateLibrary.Load(templatePath);
        Assert.True(load.Template is not null, load.Problem);
        using var template = load.Template!;
        Assert.Equal(1, template.PageIndex);
        using var cover = new Mat(2339, 1654, MatType.CV_8UC3, Scalar.All(255));
        Cv2.PutText(cover, "COVER", new Point(150, 350), HersheyFonts.HersheySimplex, 3, Scalar.All(0), 4);
        using var page = SyntheticForms.Render(sample.Kind, sample.Designation, sample.Name, 200);
        var scan = wb.AddPages("with-cover.pdf", [cover, page], 200);

        var result = DocumentReader.Read(scan, [template], ocr.Pair, CancellationToken.None);

        Assert.True(result.Values is not null, result.Problem);
        Assert.Equal(new FieldValues(sample.Designation, sample.Name, sample.Type), result.Values);
        Assert.Equal("2", result.Pages);
    }

    [OcrFact]
    public void Full_run_from_saved_regions_renames_without_changing_pdf_and_repeat_is_idempotent()
    {
        using var wb = new Workbench();
        var sample = ManualTemplateSamples.Cases[0];
        var templatePath = ManualTemplateSamples.AddTemplate(wb, sample);
        var scan = ManualTemplateSamples.AddDocument(wb, sample);
        var original = SHA256.HashData(File.ReadAllBytes(scan));
        var templateOriginal = SHA256.HashData(File.ReadAllBytes(templatePath));
        var settings = new RunSettings(wb.Templates, wb.Source, Recursive: false, Workers: 1);
        var sink = new Results();

        var result = OcrRenameRun.Execute(settings,
            () => OcrPair.CreateReal(OcrComponents.FindTessdata()!), sink, CancellationToken.None);

        Assert.Null(result.Fatal);
        Assert.True(result.Renamed == 1, string.Join("; ", sink.Entries.Select(e => e.Reason)));
        var renamed = Path.Combine(wb.Source, sample.ExpectedName);
        Assert.True(File.Exists(renamed));
        Assert.False(File.Exists(scan));
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(renamed)));
        Assert.Equal(templateOriginal, SHA256.HashData(File.ReadAllBytes(templatePath)));
        Assert.True(File.Exists(TemplateMarkupStore.PathFor(templatePath)));

        var again = OcrRenameRun.Execute(settings,
            () => OcrPair.CreateReal(OcrComponents.FindTessdata()!), new Results(), CancellationToken.None);
        Assert.Null(again.Fatal);
        Assert.Equal(0, again.Renamed);
        Assert.Equal(1, again.AlreadyNamed);
    }

    private static List<DocTemplate> RequireTemplates(IEnumerable<TemplateLoad> loads)
    {
        var result = new List<DocTemplate>();
        foreach (var load in loads)
        {
            Assert.True(load.Template is not null, $"{load.FileName}: {load.Problem}");
            result.Add(load.Template!);
        }
        return result;
    }

    private static void CopyFolder(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
    }

    private static void ExportTemplatePreviews(IEnumerable<DocTemplate> templates, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var template in templates)
        {
            using var pdf = PdfDocument.Open(template.FilePath);
            using var page = pdf.RenderPage(template.PageIndex, 1500);
            foreach (var region in template.Frames)
            {
                var rect = region.Inner.ToPixels(page.Width, page.Height);
                var color = region.Role switch
                {
                    FieldRole.Designation => FrameColors.Blue,
                    FieldRole.Name => FrameColors.Green,
                    FieldRole.Type => FrameColors.Yellow,
                    _ => FrameColors.Red,
                };
                Cv2.Rectangle(page, new Rect((int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height), color, 3);
            }
            File.WriteAllBytes(Path.Combine(target, template.Name + ".png"), page.ToBytes(".png"));
        }
    }

    private sealed class Results : IRunSink
    {
        internal readonly List<JournalEntry> Entries = [];
        public void Message(string text) { }
        public void Found(int count) { }
        public void File(JournalEntry entry) => Entries.Add(entry);
    }
}
