using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Core.Templates;
using DocumentTextRecognition.Tests.Samples;

namespace DocumentTextRecognition.Tests.Ocr;

/// <summary>Пакетная обработка сохраняет принадлежность строк полям и результат сверки двух OCR.</summary>
[Collection("OCR")]
public sealed class BatchedFieldsTests(OcrFixture ocr)
{
    [OcrTheory]
    [InlineData(FormKind.Drawing, 0, "Корпус", "Сборочный чертеж")]
    [InlineData(FormKind.Drawing, 1, "Крышка\nверхняя", "Габаритный чертеж")]
    [InlineData(FormKind.Drawing, 2, "Корпус", "Схема электрическая")]
    [InlineData(FormKind.Drawing, 3, "Корпус", "")]
    [InlineData(FormKind.Specification, 0, "Модуль связи", "")]
    public void Batch_preserves_confirmed_fields_on_drawings_specifications_and_rotations(FormKind kind, int turns,
        string name, string type)
    {
        using var wb = new Workbench();
        var templateFile = wb.AddTemplate("Образец", kind);
        using var template = TemplateLibrary.Load(templateFile).Template!;
        Assert.NotNull(template);
        var file = wb.AddDocument("scan.pdf", kind, "ЮИЛГ.461411.123", name,
            new ScanOptions { QuarterTurns = turns, Dpi = 250, SkewDegrees = turns == 0 ? 0 : 1.2 }, docType: type);
        var original = ocr.Pair.BatchFields;
        try
        {
            ocr.Pair.BatchFields = false;
            OpenCvSharp.Cv2.SetTheRNG(1729UL);
            var separate = DocumentReader.Read(file, [template], ocr.Pair, CancellationToken.None);
            ocr.Pair.BatchFields = true;
            OpenCvSharp.Cv2.SetTheRNG(1729UL);
            var batch = DocumentReader.Read(file, [template], ocr.Pair, CancellationToken.None);

            Assert.True(separate.Values is not null, separate.Problem);
            Assert.True(batch.Values is not null, batch.Problem);
            Assert.Equal(separate.Values, batch.Values);
            Assert.Equal(new FieldValues("ЮИЛГ.461411.123", name.Replace('\n', ' '),
                string.IsNullOrEmpty(type) ? DocTypes.WhenMissing : type), batch.Values);
        }
        finally { ocr.Pair.BatchFields = original; }
    }

    [OcrFact]
    public void Batch_does_not_turn_known_ocr_disagreement_into_a_wrong_name()
    {
        using var wb = new Workbench();
        var templateFile = wb.AddTemplate("Чертеж", FormKind.Drawing);
        using var template = TemplateLibrary.Load(templateFile).Template!;
        var file = wb.AddDocument("scan.pdf", FormKind.Drawing, "АБРС.713656.909", "Корпус",
            new ScanOptions { Dpi = 150, Noise = 0.04, Blur = 0.6, SkewDegrees = 1.5 });
        var original = ocr.Pair.BatchFields;
        try
        {
            ocr.Pair.BatchFields = true;
            var result = DocumentReader.Read(file, [template], ocr.Pair, CancellationToken.None);
            if (result.Values is { } values)
                Assert.Equal(new FieldValues("АБРС.713656.909", "Корпус", DocTypes.WhenMissing), values);
            else Assert.False(string.IsNullOrWhiteSpace(result.Problem));
        }
        finally { ocr.Pair.BatchFields = original; }
    }
}
