using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Core.Templates;
using DocumentTextRecognition.Tests.Samples;

namespace DocumentTextRecognition.Tests.Ocr;

/// <summary>Повторное использование предиктора не меняет детекцию, текст, уверенность и решение двух OCR
/// при смене размера входа, полей, страниц и качества скана.</summary>
[Collection("OCR")]
public sealed class PredictorReuseTests
{
    [OcrTheory]
    [InlineData(FormKind.Drawing, 0, false, false)]
    [InlineData(FormKind.Drawing, 1, false, false)]
    [InlineData(FormKind.Drawing, 2, false, false)]
    [InlineData(FormKind.Drawing, 3, false, false)]
    [InlineData(FormKind.Specification, 0, false, false)]
    [InlineData(FormKind.DrawingA3, 0, false, false)]
    [InlineData(FormKind.DrawingA3, 0, false, true)]
    [InlineData(FormKind.Drawing, 0, true, false)]
    public void Reused_predictors_preserve_document_result_and_each_field_reading(FormKind kind, int turns, bool poorScan, bool unclearCombined)
    {
        using var wb = new Workbench();
        var templateFile = wb.AddTemplate("Образец", kind, kind == FormKind.DrawingA3 ? Marking.Combined : Marking.Separate);
        using var template = TemplateLibrary.Load(templateFile).Template!;
        Assert.NotNull(template);
        var designation = poorScan ? "АБРС.713656.909" : kind == FormKind.DrawingA3 && !unclearCombined ? "ПРМД.301261.011" : "ЮИЛГ.461411.123";
        var name = kind == FormKind.DrawingA3 && !unclearCombined ? "Рама сварная" : "Корпус";
        var options = new ScanOptions
        {
            QuarterTurns = turns, Dpi = poorScan ? 150 : 250, SkewDegrees = kind == FormKind.DrawingA3 && !unclearCombined ? 0 : 1.2,
            Noise = poorScan ? 0.04 : 0, Blur = poorScan ? 0.6 : 0,
        };
        var file = wb.AddDocument("scan.pdf", kind, designation, name, options, docType: "Сборочный чертеж");
        // Проверяем выбранный производственный backend явно: новый PaddleConfig по умолчанию
        // еще не включает oneDNN и иначе незаметно проверялся бы только запасной BLAS-путь.
        const bool oneDnn = true;
        using var original = OcrPair.CreateReal(OcrComponents.FindTessdata()!, useOneDnn: oneDnn, reusePredictors: false, useOnnx: false);
        using var reused = OcrPair.CreateReal(OcrComponents.FindTessdata()!, useOneDnn: oneDnn, reusePredictors: true, useOnnx: false);
        original.PreferKnownLines = false;
        reused.PreferKnownLines = false;
        var before = new List<RecognitionDiagnostic>();
        var after = new List<RecognitionDiagnostic>();
        // Одинаковое состояние RANSAC: сравниваем OCR на одинаковых вырезанных пикселях.
        OpenCvSharp.Cv2.SetTheRNG(1729UL);
        var expected = DocumentReader.Read(file, [template], original, CancellationToken.None, before.Add);
        OpenCvSharp.Cv2.SetTheRNG(1729UL);
        var actual = DocumentReader.Read(file, [template], reused, CancellationToken.None, after.Add);

        Assert.Equal(expected.Values, actual.Values);
        Assert.Equal(expected.Problem, actual.Problem);
        if (!poorScan && !unclearCombined) Assert.True(actual.Values is not null, actual.Problem);
        if (actual.Values is null) Assert.False(string.IsNullOrWhiteSpace(actual.Problem));
        var oldFields = before.Where(d => d.Field is not null).Select(d => d.Field!).ToArray();
        var newFields = after.Where(d => d.Field is not null).Select(d => d.Field!).ToArray();
        Assert.Equal(oldFields.Length, newFields.Length);
        for (var i = 0; i < oldFields.Length; i++)
        {
            Assert.Equal(oldFields[i].Role, newFields[i].Role);
            Assert.Equal(oldFields[i].First, newFields[i].First);
            Assert.Equal(oldFields[i].Second, newFields[i].Second);
            Assert.Equal(oldFields[i].CropPng, newFields[i].CropPng);
        }
    }

    [OcrFact]
    public void Persistent_recognition_keeps_confidence_and_boxes_when_input_shapes_change()
    {
        using var original = new PaddleEngine(OcrComponents.PaddleDir, useOneDnn: true, reusePredictors: false, useOnnx: false);
        using var reused = new PaddleEngine(OcrComponents.PaddleDir, useOneDnn: true, reusePredictors: true, useOnnx: false);
        using var page = SyntheticForms.Render(FormKind.Drawing, "ЮИЛГ.461411.123", "Корпус", 250, docType: "Сборочный чертеж");
        var fields = SyntheticForms.Fields(FormKind.Drawing);
        var regions = new[] { fields.Designation, fields.Name, fields.Type, fields.Name, fields.Designation };
        var px = 250 / 25.4;
        foreach (var field in regions)
        {
            using var part = new OpenCvSharp.Mat(page, new OpenCvSharp.Rect((int)(field.X * px), (int)(field.Y * px),
                (int)(field.Width * px), (int)(field.Height * px)));
            using var crop = FieldCrop.Prepare(part);
            var expected = original.Read(crop);
            var actual = reused.Read(crop);
            Assert.Equal(expected.Lines.Count, actual.Lines.Count);
            for (var i = 0; i < expected.Lines.Count; i++)
            {
                Assert.Equal(expected.Lines[i].Text, actual.Lines[i].Text);
                Assert.Equal(expected.Lines[i].Confidence, actual.Lines[i].Confidence, 6);
                Assert.Equal(expected.Lines[i] with { Confidence = 0 }, actual.Lines[i] with { Confidence = 0 });
            }
        }
    }
}
