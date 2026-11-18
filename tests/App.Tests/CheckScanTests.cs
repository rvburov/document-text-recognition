using DocumentTextRecognition.Dialogs;
using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Tests.Samples;

namespace DocumentTextRecognition.AppTests;

public sealed class CheckScanTests
{
    [Fact]
    public void Confidence_diagnostics_keep_optional_metadata_compatible_and_explain_the_threshold()
    {
        Assert.Null(CheckScanDialog.DescribeConfidence(new EngineDiagnostic("OCR", "Текст", "Текст", null)));
        var text = CheckScanDialog.DescribeConfidence(
            new EngineDiagnostic("OCR", "Текст", "", "низкая уверенность", 0.27, 0.30));
        Assert.Contains("27 %", text);
        Assert.Contains("требуется не менее 30 %", text);
    }

    [Fact]
    public void Confidence_diagnostics_do_not_round_a_rejected_score_up_to_the_threshold()
    {
        var text = CheckScanDialog.DescribeConfidence(new EngineDiagnostic("OCR", "Текст", "", null,
            MathF.BitDecrement(0.5f), 0.5));
        Assert.Contains("49,999997", text);
        Assert.Contains("требуется не менее 50 %", text);
    }

    [Fact]
    public void Confidence_diagnostics_distinguish_missing_text_and_unavailable_scores()
    {
        var empty = CheckScanDialog.DescribeConfidence(new EngineDiagnostic("OCR", "", "", null, 0, 0.5));
        Assert.Contains("нет распознанных строк", empty);
        Assert.DoesNotContain(": 0 %", empty);
        var unavailable = CheckScanDialog.DescribeConfidence(new EngineDiagnostic("OCR", "Текст", "", null,
            double.NaN, 0.5));
        Assert.Contains("оценка не получена", unavailable);
        Assert.DoesNotContain("NaN", unavailable);
    }

    [OcrFact]
    public void Invalid_selected_template_is_explained_without_using_another_template()
    {
        using var wb = new Workbench();
        wb.AddTemplate("Другой пригодный шаблон", FormKind.Drawing);
        var selected = Path.Combine(wb.Templates, "Поврежденный.pdf");
        File.WriteAllText(selected, "not a PDF");
        var document = wb.AddDocument("document.pdf", FormKind.Drawing, "ЮИЛГ.461411.123", "Корпус");
        var before = File.ReadAllBytes(document);

        var report = CheckScanDialog.Recognize(wb.Templates, document, selected);

        Assert.Null(report.Result.Values);
        Assert.Contains("Шаблон не готов", report.Result.Problem);
        Assert.Contains(report.Diagnostics, d => d.Stage == "Области шаблона" && d.Message.Contains("Поврежденный.pdf"));
        Assert.DoesNotContain(report.Diagnostics, d => d.Stage == "Совмещение" || d.Field is not null);
        Assert.Equal(before, File.ReadAllBytes(document));
    }
}
