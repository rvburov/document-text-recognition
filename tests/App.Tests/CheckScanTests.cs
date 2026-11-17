using DocumentTextRecognition.Dialogs;
using DocumentTextRecognition.Tests.Samples;

namespace DocumentTextRecognition.AppTests;

public sealed class CheckScanTests
{
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
