using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Pdf;
using DocumentTextRecognition.Core.Templates;
using OpenCvSharp;

namespace DocumentTextRecognition.Tests.Samples;

/// <summary>Corpus integrity, not an OCR accuracy claim. Actual OCR is checked separately.</summary>
public sealed class GostTypeAPairedPdfTests
{
    [OcrTheory]
    [InlineData(null)]
    [InlineData("Сборочный чертёж")]
    public void Vector_and_image_only_pages_share_physical_size_and_saved_field_geometry(string? documentType)
    {
        const string designation = "УИЕС.468732.271";
        const string name = "Плата Epcos\nUSB 1489 МГц";
        using var work = new Workbench();
        var pair = GostTypeASamples.CreatePair(work, "neutral", designation, name, documentType, 250);
        var saved = TemplateMarkupStore.Load(pair.TemplatePath);
        Assert.Null(saved.Problem);
        Assert.NotNull(saved.Markup);
        Assert.Equal(pair.Regions, saved.Markup.Regions);
        using var vector = PdfDocument.Open(pair.VectorPath);
        using var raster = PdfDocument.Open(pair.RasterPath);
        Assert.Equal(vector.GetPageSize(0).Width, raster.GetPageSize(0).Width, 4);
        Assert.Equal(vector.GetPageSize(0).Height, raster.GetPageSize(0).Height, 4);
        var vectorText = vector.ReadTextPage(0);
        var rasterText = raster.ReadTextPage(0);
        Assert.Null(vectorText.Problem);
        Assert.Null(rasterText.Problem);
        foreach (var region in pair.Regions)
        {
            var expected = region.Role switch
            {
                FieldRole.Designation => designation,
                FieldRole.Name => NormalizeWhitespace(name),
                FieldRole.Type => documentType,
                _ => throw new InvalidOperationException(),
            };
            var selected = vectorText.Select(region.Bounds);
            Assert.True(selected.CanUseCandidate, selected.Problem);
            Assert.True(selected.RequiresIndependentValidation);
            Assert.Equal(expected, NormalizeWhitespace(selected.Text));
            Assert.Equal(PdfTextStatus.Empty, rasterText.Select(region.Bounds).Status);
        }
        var side = (int)Math.Round(Math.Max(pair.PageSize.Width, pair.PageSize.Height) * pair.RasterDpi / 72);
        using var sourcePixels = vector.RenderPage(0, side);
        using var rasterPixels = raster.RenderPage(0, side);
        Assert.Equal(sourcePixels.Size(), rasterPixels.Size());
        using var delta = new Mat();
        Cv2.Absdiff(sourcePixels, rasterPixels, delta);
        Assert.InRange(Cv2.Mean(delta).Val0, 0, 1.5); // image PDF sampling may interpolate at subpixel edges

        if (Environment.GetEnvironmentVariable("DTR_GOST_PAIR_OUTPUT") is { Length: > 0 } exportRoot)
        {
            var folder = Path.Combine(exportRoot, documentType is null ? "specification" : "drawing");
            Directory.CreateDirectory(folder);
            var files = new[]
            {
                (Source: pair.VectorPath, Name: "scan-vector.pdf"),
                (Source: pair.RasterPath, Name: "scan-raster.pdf"),
                (Source: pair.TemplatePath, Name: "template.pdf"),
                (Source: TemplateMarkupStore.PathFor(pair.TemplatePath), Name: "template.pdf.template.json"),
            };
            foreach (var file in files) File.Copy(file.Source, Path.Combine(folder, file.Name), overwrite: true);
            var evidence = new
            {
                Scope = "Fixture integrity and PDF text extraction only; no OCR success is claimed by this record.",
                FontSha256 = GostTypeASamples.FontSha256,
                Expected = new { Designation = designation, Name = NormalizeWhitespace(name), Type = documentType },
                pair.PageSize, pair.RasterDpi,
                MeanAbsolutePixelDifference = Cv2.Mean(delta).Val0,
                Fields = pair.Regions.Select(region => new
                {
                    Role = region.Role.ToString(), region.Bounds,
                    VectorText = vectorText.Select(region.Bounds).Text,
                    VectorRequiresIndependentValidation = true,
                    RasterTextStatus = rasterText.Select(region.Bounds).Status.ToString(),
                }),
                Files = files.Select(file => new
                {
                    file.Name,
                    Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file.Source))),
                }),
            };
            File.WriteAllText(Path.Combine(folder, "verification.json"), System.Text.Json.JsonSerializer.Serialize(evidence,
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }) + "\n");
        }
    }

    // Text-layer integrity checks preserve Unicode exactly, including ё/е; the production
    // filename parser deliberately folds that pair and is not the oracle for this fixture.
    private static string NormalizeWhitespace(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
