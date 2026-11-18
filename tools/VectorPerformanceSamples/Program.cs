using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Pdf;
using DocumentTextRecognition.Core.Templates;
using DocumentTextRecognition.Tests.Samples;

// Independent, literal goldens are fixed before rendering or reading any PDF.
// This program does not run OCR or decide which samples count from recognition results.
var cases = new[]
{
    new Case("01-specification-multiline", "specification-a4", "УИЕС.468732.271", "Плата УПЧ\n1489 МГц", null,
        "УИЕС.468732.271 Плата УПЧ 1489 МГц Спецификация.pdf"),
    new Case("02-specification-brand", "specification-a4", "ВКЕМ.461411.200", "Плата Epcos\nUSB 1489 МГц", null,
        "ВКЕМ.461411.200 Плата Epcos USB 1489 МГц Спецификация.pdf"),
    new Case("03-assembly", "drawing-a4", "ЮИЛГ.461411.123", "Корпус", "Сборочный чертёж",
        "ЮИЛГ.461411.123 Корпус Сборочный чертеж.pdf"),
    new Case("04-schematic", "drawing-a4", "АЦИЕ.202132.004", "Блок питания", "Схема электрическая",
        "АЦИЕ.202132.004 Блок питания Схема электрическая.pdf"),
};
foreach (var item in cases)
{
    var logicalName = $"{Canonical(item.Designation)} {Canonical(item.PrintedName)} {Canonical(item.PrintedType ?? "Спецификация")}.pdf";
    if (logicalName != item.ExpectedName)
        throw new InvalidDataException("Frozen expected filename differs from source truth: " + item.Stem);
}

if (args.Length > 1) throw new ArgumentException("Usage: VectorPerformanceSamples [empty-output-folder]");
var output = Path.GetFullPath(args.Length == 1 ? args[0] : "docs/test-samples/vector-performance");
if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
    throw new IOException("Output folder must be empty; existing data is never overwritten: " + output);
Directory.CreateDirectory(output);
var templatesFolder = Path.Combine(output, "templates");
Directory.CreateDirectory(templatesFolder);
Directory.CreateDirectory(Path.Combine(output, "scans"));
Directory.CreateDirectory(Path.Combine(output, "paired-raster"));

const int rasterDpi = 250;
var templates = new Dictionary<string, TemplateInfo>(StringComparer.Ordinal);
var records = new List<object>();
OcrComponents.ConfigureRuntime();
using (var work = new Workbench())
{
    foreach (var item in cases)
    {
        var pair = GostTypeASamples.CreatePair(work, item.Stem, item.Designation,
            item.PrintedName, item.PrintedType, rasterDpi);
        var templateFile = $"templates/{item.Layout}.pdf";
        var saved = TemplateMarkupStore.Load(pair.TemplatePath);
        if (saved.Problem is not null || saved.Markup is null || saved.Markup.PageIndex != 0 ||
            saved.Markup.TypeAbsent != (item.PrintedType is null) || !pair.Regions.SequenceEqual(saved.Markup.Regions))
            throw new InvalidDataException("Generated saved field coordinates are inconsistent: " + item.Stem + ": " + saved.Problem);
        if (!templates.TryGetValue(item.Layout, out var template))
        {
            File.Copy(pair.TemplatePath, Path.Combine(output, templateFile));
            File.Copy(TemplateMarkupStore.PathFor(pair.TemplatePath),
                TemplateMarkupStore.PathFor(Path.Combine(output, templateFile)));
            template = new TemplateInfo(templateFile, pair.Regions, saved.Markup.TypeAbsent,
                pair.PageSize.Width, pair.PageSize.Height);
            templates.Add(item.Layout, template);
        }
        else if (!template.Regions.SequenceEqual(pair.Regions) || template.TypeAbsent != saved.Markup.TypeAbsent ||
                 Math.Abs(template.WidthPoints - pair.PageSize.Width) > 0.0001 ||
                 Math.Abs(template.HeightPoints - pair.PageSize.Height) > 0.0001)
            throw new InvalidDataException("A shared template has different geometry: " + item.Layout);

        var file = $"scans/{item.Stem}.pdf";
        var rasterFile = $"paired-raster/{item.Stem}.pdf";
        File.Copy(pair.VectorPath, Path.Combine(output, file));
        File.Copy(pair.RasterPath, Path.Combine(output, rasterFile));
        using var vector = PdfDocument.Open(pair.VectorPath);
        using var raster = PdfDocument.Open(pair.RasterPath);
        var vectorSize = vector.GetPageSize(0);
        var rasterSize = raster.GetPageSize(0);
        var vectorText = vector.ReadTextPage(0);
        var rasterText = raster.ReadTextPage(0);
        if (vector.PageCount != 1 || raster.PageCount != 1 || vectorText.Problem is not null || rasterText.Problem is not null ||
            Math.Abs(vectorSize.Width - rasterSize.Width) > 0.0001 || Math.Abs(vectorSize.Height - rasterSize.Height) > 0.0001)
            throw new InvalidDataException("Generated pair is not a readable single-page PDF: " + item.Stem);
        var fields = pair.Regions.Select(region =>
        {
            var printed = region.Role switch
            {
                FieldRole.Designation => item.Designation,
                FieldRole.Name => item.PrintedName,
                FieldRole.Type => item.PrintedType!,
                _ => throw new InvalidDataException("Unexpected generated field role."),
            };
            var selected = vectorText.Select(region.Bounds);
            var rasterSelected = rasterText.Select(region.Bounds);
            if (!selected.CanUseCandidate || !selected.RequiresIndependentValidation ||
                Whitespace(selected.Text) != Whitespace(printed) || rasterSelected.Status != PdfTextStatus.Empty)
                throw new InvalidDataException("Text-layer integrity differs from frozen truth at saved coordinates: " +
                                               item.Stem + "/" + region.Role);
            return new
            {
                Role = region.Role.ToString(),
                region.Bounds,
                PrintedText = printed,
                CanonicalText = Canonical(printed),
                ExtractedVectorText = selected.Text,
                RequiresIndependentRasterValidation = true,
                RasterTextStatus = rasterSelected.Status.ToString(),
            };
        }).ToArray();
        records.Add(new
        {
            File = file,
            Sha256 = Sha(Path.Combine(output, file)),
            Template = templateFile,
            ExpectedName = item.ExpectedName,
            RecognitionRequired = true,
            DatasetKind = "vector",
            Synthetic = true,
            ExpectedSource = "pdf-text-confirmed",
            FrozenTruth = new
            {
                item.Designation,
                PrintedName = item.PrintedName,
                Name = Canonical(item.PrintedName),
                PrintedType = item.PrintedType,
                Type = Canonical(item.PrintedType ?? "Спецификация"),
            },
            Pair = new
            {
                RasterFile = rasterFile,
                RasterSha256 = Sha(Path.Combine(output, rasterFile)),
                RasterDpi = rasterDpi,
                RasterIsRenderedFromVector = true,
                WidthPoints = pair.PageSize.Width,
                HeightPoints = pair.PageSize.Height,
            },
            Fields = fields,
        });
        Console.WriteLine($"Generated {file}; exact text/coordinates verified; OCR not run.");
    }
}

var jsonOptions = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
var report = new
{
    SchemaVersion = 1,
    GeneratedUtc = DateTimeOffset.UtcNow,
    DatasetKind = "vector",
    Synthetic = true,
    Generator = "tools/VectorPerformanceSamples using GostTypeASamples.CreatePair",
    Scope = "Four fixed synthetic single-page vector PDFs with visible embedded GOST Type A text and exact saved field coordinates. " +
            "Literal expected filenames are frozen before rendering. Image-only siblings are generated from the same vector pages for audit, " +
            "and are not throughput inputs. No production PDFs, OCR inference, accuracy success, or benchmark performance is claimed by this manifest. " +
            "All four inputs are required; a subsequent refusal or wrong name must fail verification without excluding a sample.",
    FilenameCanonicalization = "Collapse whitespace and fold ё→е / Ё→Е in filenames; printed source and extracted text preserve their original Unicode.",
    SourceFont = new
    {
        File = "tests/Core.Tests/Fonts/" + GostTypeASamples.FontFile,
        Sha256 = GostTypeASamples.FontSha256,
        Family = "GOST 2.304-81 Type A Slanted",
        EmbeddedInVector = true,
        License = "tests/Core.Tests/Fonts/OFL-GOST-Type-A.txt",
        Source = "tests/Core.Tests/Fonts/GOST-Type-A-SOURCE.md",
    },
    Environment = new { OS = RuntimeInformation.OSDescription, DotNet = RuntimeInformation.FrameworkDescription },
    Integrity = new { SavedRegionsVerified = true, EqualPhysicalPageSizeVerified = true, ExactVectorTextVerified = true, RasterFieldsHaveNoTextLayer = true, OcrRun = false },
    Templates = templates.Values.Select(template => new
    {
        File = template.File,
        Sha256 = Sha(Path.Combine(output, template.File)),
        MarkupFile = template.File + ".template.json",
        MarkupSha256 = Sha(TemplateMarkupStore.PathFor(Path.Combine(output, template.File))),
        template.TypeAbsent,
        UnrelatedValues = new { Designation = "ОБРЗ.000000.000", Name = "Образец", PrintedType = template.TypeAbsent ? null : "Тип документа" },
    }).ToArray(),
    Samples = records,
};
File.WriteAllText(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(report, jsonOptions) + "\n");
File.WriteAllText(Path.Combine(output, "README.md"), """
    # Отдельный синтетический векторный набор

    Четыре PDF в `scans` созданы `tools/VectorPerformanceSamples` из заранее заданных строк.
    В них видимый встроенный шрифт ГОСТ типа А и настоящий Unicode-текстовый слой. Два PDF
    в `templates` содержат другие значения; области полей сохранены в `.pdf.template.json`.
    `paired-raster` содержит полученные отрисовкой версии без текстового слоя для аудита;
    они не входят в список `Samples` для замера производительности.

    `verification.json` фиксирует независимые ожидаемые имена, исходные строки, координаты,
    SHA-256 PDF, шрифта и разметки. В итоговых именах ё заменяется на е, переносы — пробелами.
    Генератор проверяет точный текст в выбранных областях и отсутствие текстового слоя у
    растровых пар. Он не запускает OCR и не утверждает, что распознавание прошло успешно.
    Все четыре файла обязательны: отказ движков или неверное имя проваливают будущий замер.

    Это регрессионные синтетические примеры, а не независимая оценка точности на документах
    пользователя. Векторный замер проверяет ветку `pdf-text-confirmed` с независимым чтением
    видимых пикселей; его нельзя выдавать за ускорение прежнего растрового OCR. Исходные пять
    PDF, старый raster-benchmark и его результаты остаются отдельными.
    """ + "\n");
Console.WriteLine($"Generated {records.Count} required vector samples and {templates.Count} shared templates: {output}");
return 0;

static string Whitespace(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
static string Canonical(string text) => Whitespace(text).Replace('ё', 'е').Replace('Ё', 'Е');
static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

internal sealed record Case(string Stem, string Layout, string Designation, string PrintedName, string? PrintedType, string ExpectedName);
internal sealed record TemplateInfo(string File, IReadOnlyList<TemplateRegion> Regions, bool TypeAbsent, double WidthPoints, double HeightPoints);
