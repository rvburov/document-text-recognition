using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Core.Templates;
using DocumentTextRecognition.Tests.Samples;
using OpenCvSharp;

namespace DocumentTextRecognition.Tests.Ocr;

/// <summary>Алфавит обозначения принадлежит одному чтению, а не переиспользуемому Tesseract.</summary>
[Collection("OCR")]
public sealed class TesseractProfileTests(OcrFixture ocr)
{
    private const string Designation = "ЮИЛГ.461411.123";
    private const string Name = "Плата Epcos USB";
    private const string MultilineName = "Плата Epcos USB\nМодуль USB";

    [OcrFact]
    public void Reused_tesseract_alternates_cyrillic_designation_and_general_mixed_text()
    {
        using var engine = new TesseractEngine(OcrComponents.FindTessdata()!);
        using var designation = Crop(FieldRole.Designation, Name);
        using var name = Crop(FieldRole.Name, Name);
        using var multiline = Crop(FieldRole.Name, MultilineName);

        for (var repeat = 0; repeat < 2; repeat++)
        {
            AssertDesignation(engine.Read(designation, OcrTextProfile.CyrillicDesignation));
            AssertName(engine.Read(name), Name); // The ordinary entry point must reset the profile too.
            var mixed = engine.Read(multiline, OcrTextProfile.General);
            Assert.Equal(2, mixed.Lines.Count);
            AssertName(mixed, MultilineName.Replace('\n', ' '));
            AssertDesignation(engine.Read(designation, OcrTextProfile.CyrillicDesignation));
        }
    }

    [OcrTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Reader_preserves_mixed_names_after_designation_in_every_field_reading_path(bool knownLines, bool batch)
    {
        using var wb = new Workbench();
        var templateFile = wb.AddTemplate("Чертеж", FormKind.Drawing);
        using var template = TemplateLibrary.Load(templateFile).Template!;
        Assert.NotNull(template);
        var names = new[] { Name, MultilineName };
        var originalKnownLines = ocr.Pair.PreferKnownLines;
        var originalBatch = ocr.Pair.BatchFields;
        try
        {
            ocr.Pair.PreferKnownLines = knownLines;
            ocr.Pair.BatchFields = batch;
            foreach (var name in names)
            {
                var file = wb.AddDocument("scan-" + Array.IndexOf(names, name) + ".pdf", FormKind.Drawing,
                    Designation, name, new ScanOptions { Dpi = 300, JpegQuality = 100 });
                var diagnostics = new List<RecognitionDiagnostic>();
                Cv2.SetTheRNG(1729UL);

                var result = DocumentReader.Read(file, [template], ocr.Pair, CancellationToken.None, diagnostics.Add);

                Assert.True(result.Values is not null, result.Problem);
                Assert.Equal(new FieldValues(Designation, name.Replace('\n', ' '), DocTypes.WhenMissing), result.Values);
                var fields = diagnostics.Where(d => d.Field is not null).Select(d => d.Field!).ToArray();
                var designation = Assert.Single(fields, f => f.Role == FieldRole.Designation).First;
                Assert.Equal("Tesseract", designation.Engine);
                Assert.DoesNotContain(designation.RawText, TextRules.IsLatin);
                Assert.Contains(designation.RawText, TextRules.IsCyrillic);
                Assert.Equal(Designation, designation.ParsedText);
                var reading = Assert.Single(fields, f => f.Role == FieldRole.Name).First;
                Assert.Equal("Tesseract", reading.Engine);
                Assert.Contains("USB", reading.RawText, StringComparison.Ordinal);
                Assert.Equal(name.Replace('\n', ' '), reading.ParsedText);
            }
        }
        finally
        {
            ocr.Pair.PreferKnownLines = originalKnownLines;
            ocr.Pair.BatchFields = originalBatch;
        }
    }

    [OcrFact]
    public void Reader_combined_region_keeps_general_alphabet_after_a_separate_designation_read()
    {
        using var wb = new Workbench();
        var templateFile = wb.AddTemplate("Чертеж A3", FormKind.DrawingA3, Marking.Combined);
        using var template = TemplateLibrary.Load(templateFile).Template!;
        Assert.NotNull(template);
        using var designation = Crop(FieldRole.Designation, Name);
        var tesseract = Assert.IsType<TesseractEngine>(ocr.Pair.First);
        AssertDesignation(tesseract.Read(designation, OcrTextProfile.CyrillicDesignation));
        var file = wb.AddDocument("combined.pdf", FormKind.DrawingA3, Designation, Name,
            new ScanOptions { Dpi = 300, JpegQuality = 100 });
        var diagnostics = new List<RecognitionDiagnostic>();
        Cv2.SetTheRNG(1729UL);

        var result = DocumentReader.Read(file, [template], ocr.Pair, CancellationToken.None, diagnostics.Add);

        Assert.True(result.Values is not null, result.Problem);
        Assert.Equal(new FieldValues(Designation, Name, DocTypes.WhenMissing), result.Values);
        var combined = Assert.Single(diagnostics, d => d.Field is not null).Field!;
        Assert.Equal(FieldRole.Combined, combined.Role);
        Assert.Equal("Tesseract", combined.First.Engine);
        Assert.Contains("USB", combined.First.RawText, StringComparison.Ordinal);
    }

    private static Mat Crop(FieldRole role, string name)
    {
        const int dpi = 300;
        using var page = SyntheticForms.Render(FormKind.Drawing, Designation, name, dpi);
        var fields = SyntheticForms.Fields(FormKind.Drawing);
        var field = (role == FieldRole.Designation ? fields.Designation : fields.Name).Inflate(-1);
        var pixels = dpi / 25.4;
        using var part = new Mat(page, new Rect((int)(field.X * pixels), (int)(field.Y * pixels),
            (int)(field.Width * pixels), (int)(field.Height * pixels)));
        return FieldCrop.Prepare(part);
    }

    private static void AssertDesignation(OcrReading reading)
    {
        Assert.False(reading.IsEmpty);
        Assert.DoesNotContain(reading.Text, TextRules.IsLatin);
        Assert.Contains(reading.Text, TextRules.IsCyrillic); // A digits-only answer is not a success.
        var parsed = FieldParser.ParseDesignation(reading, 0.30);
        Assert.True(parsed.IsOk, $"{reading.Text}: {parsed.Problem}");
        Assert.Equal(Designation, parsed.Value);
    }

    private static void AssertName(OcrReading reading, string expected)
    {
        Assert.Contains("USB", reading.Text, StringComparison.Ordinal);
        var parsed = FieldParser.ParseName(reading, 0.30);
        Assert.True(parsed.IsOk, $"{reading.Text}: {parsed.Problem}");
        Assert.Equal(expected, parsed.Value);
    }
}
