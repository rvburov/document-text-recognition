using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Pdf;
using DocumentTextRecognition.Core.Templates;

namespace DocumentTextRecognition.Tests.Samples;

/// <summary>Builds blind PDF fixtures without running an OCR model against their values.</summary>
public sealed class GostTypeAV3PdfTests
{
    [OcrFact]
    public void Frozen_v3_values_form_eight_exact_vector_and_image_only_pdf_pairs()
    {
        var corpus = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(GostTypeASamples.FontPath)!,
            "..", "..", "..", "docs", "test-samples", "gost-type-a", "corpus-v3"));
        var manifestPath = Path.Combine(corpus, "manifest.json");
        Assert.Equal("605A04017EB37C09FFC745506B4BF7FF8BB1943C696D6D72E58A42A282F6797B", Hash(manifestPath));
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var split = manifest.RootElement.GetProperty("Splits").EnumerateArray()
            .Single(item => item.GetProperty("Split").GetString() == "evaluation");
        var truthPath = Path.Combine(corpus, split.GetProperty("File").GetString()!);
        Assert.Equal(split.GetProperty("Sha256").GetString()!.ToUpperInvariant(), Hash(truthPath));
        using var truth = JsonDocument.Parse(File.ReadAllText(truthPath));
        var records = new List<object>();
        var outputRoot = Environment.GetEnvironmentVariable("DTR_GOST_V3_PDF_OUTPUT");
        var cases = truth.RootElement.GetProperty("Documents").EnumerateArray().Take(8).ToArray();
        Assert.Equal(8, cases.Length);
        for (var index = 0; index < cases.Length; index++)
        {
            var item = cases[index];
            var id = item.GetProperty("Id").GetString()!;
            var designation = item.GetProperty("Designation").GetString()!;
            var name = item.GetProperty("Name").GetString()!;
            Assert.Equal(JsonValueKind.Null, item.GetProperty("Type").ValueKind);
            using var work = new Workbench();
            var pair = GostTypeASamples.CreatePair(work, "neutral", designation, name, dpi: new[] { 250, 300, 325 }[index % 3]);
            using var vector = PdfDocument.Open(pair.VectorPath);
            using var raster = PdfDocument.Open(pair.RasterPath);
            var vectorText = vector.ReadTextPage(0);
            var rasterText = raster.ReadTextPage(0);
            Assert.Null(vectorText.Problem);
            Assert.Null(rasterText.Problem);
            Assert.Equal(vector.GetPageSize(0).Width, raster.GetPageSize(0).Width, 4);
            Assert.Equal(vector.GetPageSize(0).Height, raster.GetPageSize(0).Height, 4);
            var markup = TemplateMarkupStore.Load(pair.TemplatePath);
            Assert.Null(markup.Problem);
            Assert.Equal(pair.Regions, markup.Markup!.Regions);
            foreach (var region in pair.Regions)
            {
                var selected = vectorText.Select(region.Bounds);
                Assert.True(selected.CanUseCandidate, selected.Problem);
                Assert.True(selected.RequiresIndependentValidation);
                Assert.Equal(region.Role == FieldRole.Designation ? designation : name, selected.Text);
                Assert.Equal(PdfTextStatus.Empty, rasterText.Select(region.Bounds).Status);
            }
            if (string.IsNullOrWhiteSpace(outputRoot)) continue;
            var folder = Path.Combine(outputRoot, id);
            Directory.CreateDirectory(folder);
            var files = new[]
            {
                (Source: pair.VectorPath, File: "scan-vector.pdf"),
                (Source: pair.RasterPath, File: "scan-raster.pdf"),
                (Source: pair.TemplatePath, File: "template.pdf"),
                (Source: TemplateMarkupStore.PathFor(pair.TemplatePath), File: "template.pdf.template.json"),
            };
            foreach (var file in files) File.Copy(file.Source, Path.Combine(folder, file.File), overwrite: true);
            records.Add(new
            {
                Id = id, Expected = new { Designation = designation, Name = name, Type = (string?)null },
                pair.RasterDpi, pair.PageSize, Regions = pair.Regions,
                Files = files.Select(file => new { file.File, Sha256 = Hash(file.Source) }).ToArray(),
                VectorTextVerified = true, RasterTextLayerEmpty = true, OcrPerformed = false,
            });
        }
        if (!string.IsNullOrWhiteSpace(outputRoot))
            File.WriteAllText(Path.Combine(outputRoot, "verification.json"), JsonSerializer.Serialize(new
            {
                Scope = "Eight held-out fixture pairs; integrity/text extraction only. Do not use OCR results for model selection.",
                ManifestSha256 = Hash(manifestPath), SplitManifestSha256 = Hash(truthPath),
                GostTypeASamples.FontSha256, Samples = records,
            }, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
    }

    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
}
