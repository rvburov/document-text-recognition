using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Pdf;
using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Core.Templates;
using DocumentTextRecognition.Tests.Samples;
using OpenCvSharp;

namespace DocumentTextRecognition.Tests.Ocr;

/// <summary>Independent raster disagreement must not be outvoted by a false PDF map.</summary>
[Collection("OCR")]
public sealed class PdfTextConflictTests
{
    [OcrFact]
    public void Confident_visible_designation_conflict_vetoes_secondary_confirmation_of_false_pdf_digits()
    {
        const string visible = "УИЕС.468732.271";
        const string falsePdf = "УИЕС.468732.277";
        const string name = "Кронштейн";
        using var wb = new Workbench();
        var regions = new[]
        {
            new TemplateRegion(FieldRole.Designation, new(220d / 595, 667d / 842, 345d / 595, 40d / 842)),
            new TemplateRegion(FieldRole.Name, new(220d / 595, 717d / 842, 345d / 595, 40d / 842)),
        };
        var templatePath = Create(wb.Templates, "template", "ОБРЗ.000000.000", "Образец");
        Assert.Null(TemplateMarkupStore.Save(templatePath, 0, regions, typeAbsent: true));
        using var template = Assert.IsType<DocTemplate>(TemplateLibrary.Load(templatePath).Template);
        var path = Create(wb.Source, "false-map", visible, name, new Dictionary<char, string> { ['1'] = "7" });
        using (var pdf = PdfDocument.Open(path))
        {
            var candidate = pdf.ReadTextPage(0).Select(regions[0].Bounds);
            Assert.True(candidate.CanUseCandidate, candidate.Problem);
            Assert.Equal(falsePdf, candidate.Text); // Different digits, not a visual alphabet alias.
        }

        // First models Tesseract; second is selected as the initial PDF raster peer.
        // Both answer confidently, but only the initial peer matches the actual glyphs.
        using var pair = new OcrPair(
            new Answers(falsePdf, name, falsePdf, name),
            new Answers(visible, name, visible, name))
            { UsePdfText = true, UseGostFallback = false };
        Cv2.SetTheRNG(1729UL);

        var result = DocumentReader.Read(path, [template], pair, CancellationToken.None);

        Assert.Null(result.Values);
        Assert.False(string.IsNullOrWhiteSpace(result.Problem));
        Assert.Equal(1, result.Trace!.PdfTextAttempts);
        Assert.Equal(0, result.Trace.PdfTextAccepted);
        Assert.NotEqual("pdf-text-confirmed", result.Trace.SourcePath);
    }

    private static string Create(string folder, string stem, string designation, string name,
        IReadOnlyDictionary<char, string>? overrides = null) => PdfTextFixtures.Create(folder, stem,
        [new PdfTextFixtures.Page([new(designation, 230, 150, 18), new(name, 230, 100, 18)],
            Width: 595, Height: 842, RegistrationGrid: true)],
        unicodeOverrides: overrides, fontFile: Font());

    private static string Font()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "src", "Ui", "Assets", "Fonts", "GolosText-Regular.ttf");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("Pinned vector fixture font is required; font substitution is forbidden.");
    }

    private sealed class Answers(params string[] values) : IOcrEngine
    {
        private int _next;
        public string Name => "Controlled confidence/conflict regression";
        public OcrReading Read(Mat gray)
        {
            Assert.InRange(_next, 0, values.Length - 1);
            return new(Name, [new(values[_next++], .99, 0, 0, gray.Width, gray.Height)]);
        }
        public void Dispose() { }
    }
}
