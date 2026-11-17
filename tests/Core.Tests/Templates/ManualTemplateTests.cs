using System.Text.Json.Nodes;
using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Templates;
using DocumentTextRecognition.Tests.Samples;

namespace DocumentTextRecognition.Tests.Templates;

public sealed class ManualTemplateTests
{
    private static readonly TemplateRegion[] TwoFields =
    [
        new(FieldRole.Designation, new NormRect(0.4, 0.6, 0.5, 0.1)),
        new(FieldRole.Name, new NormRect(0.4, 0.7, 0.3, 0.1)),
    ];

    private static string CreatePdf(TempDir directory, int pages = 1)
    {
        var path = Path.Combine(directory.Path, "Образец.pdf");
        using var pdf = new PdfSharp.Pdf.PdfDocument();
        for (var i = 0; i < pages; i++)
        {
            var page = pdf.AddPage();
            page.Width = PdfSharp.Drawing.XUnit.FromPoint(595);
            page.Height = PdfSharp.Drawing.XUnit.FromPoint(842);
            using var drawing = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
            for (var row = 0; row < 12; row++)
                drawing.DrawRectangle(PdfSharp.Drawing.XPens.Black, 40, 100 + row * 40, 500, 40);
        }
        pdf.Save(path);
        return path;
    }

    [OcrFact]
    public void Manual_regions_survive_reload_without_colored_pixels_and_do_not_change_pdf()
    {
        using var dir = new TempDir();
        var file = CreatePdf(dir);
        var original = File.ReadAllBytes(file);
        Assert.Null(TemplateMarkupStore.Save(file, 0, TwoFields, typeAbsent: true));

        var saved = TemplateMarkupStore.Load(file);
        Assert.True(saved.Exists);
        Assert.Null(saved.Problem);
        Assert.Equal(TwoFields, saved.Markup!.Regions);
        Assert.Equal(595, saved.Markup.PageWidth);
        Assert.Equal(842, saved.Markup.PageHeight);
        Assert.Equal(64, saved.Markup.PdfSha256.Length);
        Assert.Equal(original, File.ReadAllBytes(file));

        var result = TemplateLibrary.Load(file);
        using var template = result.Template;
        Assert.Null(result.Problem);
        Assert.NotNull(template);
        Assert.True(template.IsManual);
        Assert.True(template.TypeAbsent);
        Assert.Equal(TwoFields.Select(r => r.Bounds), template.Frames.Select(f => f.Inner));
    }

    [OcrFact]
    public void Manual_page_selection_is_not_limited_to_first_five_pages()
    {
        using var dir = new TempDir();
        var file = CreatePdf(dir, pages: 7);
        Assert.Null(TemplateMarkupStore.Save(file, 6, TwoFields, typeAbsent: true));

        var result = TemplateLibrary.Load(file);
        using var template = result.Template;
        Assert.Null(result.Problem);
        Assert.Equal(6, template!.PageIndex);
        Assert.Contains("страницу", TemplateMarkupStore.Save(file, 7, TwoFields, typeAbsent: true));
        Assert.Equal(6, TemplateMarkupStore.Load(file).Markup!.PageIndex);
    }

    [OcrFact]
    public void Invalid_save_keeps_previous_markup_and_no_temporary_file()
    {
        using var dir = new TempDir();
        var file = CreatePdf(dir);
        Assert.Null(TemplateMarkupStore.Save(file, 0, TwoFields, typeAbsent: true));
        var previous = File.ReadAllBytes(TemplateMarkupStore.PathFor(file));
        TemplateRegion[] overlapping = [TwoFields[0], new(FieldRole.Name, new NormRect(0.5, 0.65, 0.3, 0.1))];

        Assert.Contains("перекрываются", TemplateMarkupStore.Save(file, 0, overlapping, typeAbsent: true));
        Assert.Equal(previous, File.ReadAllBytes(TemplateMarkupStore.PathFor(file)));
        Assert.Empty(Directory.EnumerateFiles(dir.Path, "*.tmp-*"));
    }

    [OcrFact]
    public void Changed_pdf_is_rejected_until_user_resaves_regions()
    {
        using var dir = new TempDir();
        var file = CreatePdf(dir);
        Assert.Null(TemplateMarkupStore.Save(file, 0, TwoFields, typeAbsent: true));
        File.AppendAllText(file, "\n% Changed PDF\n");

        var load = TemplateMarkupStore.Load(file);
        Assert.True(load.Exists);
        Assert.NotNull(load.Markup); // editor can inspect the old rectangles before saving anew
        Assert.Contains("PDF изменился", load.Problem);
        Assert.Null(TemplateLibrary.Load(file).Template);

        Assert.Null(TemplateMarkupStore.Save(file, 0, TwoFields, typeAbsent: true));
        Assert.Null(TemplateMarkupStore.Load(file).Problem);
    }

    [OcrFact]
    public void Editor_cannot_save_old_coordinates_over_a_changed_pdf()
    {
        using var dir = new TempDir();
        var file = CreatePdf(dir);
        var openedFingerprint = TemplateMarkupStore.GetFingerprint(file);
        Assert.Null(TemplateMarkupStore.Save(file, 0, TwoFields, true, openedFingerprint));
        var previous = File.ReadAllBytes(TemplateMarkupStore.PathFor(file));
        File.AppendAllText(file, "\n% External editor changed this PDF\n");

        Assert.Contains("после открытия редактора", TemplateMarkupStore.Save(file, 0, TwoFields, true, openedFingerprint));
        Assert.Equal(previous, File.ReadAllBytes(TemplateMarkupStore.PathFor(file)));
    }

    [OcrFact]
    public void Pdf_and_sidecar_can_be_moved_together()
    {
        using var dir = new TempDir();
        var file = CreatePdf(dir);
        Assert.Null(TemplateMarkupStore.Save(file, 0, TwoFields, typeAbsent: true));
        var destination = Path.Combine(dir.Path, "Другое имя.pdf");
        File.Move(file, destination);
        File.Move(TemplateMarkupStore.PathFor(file), TemplateMarkupStore.PathFor(destination));

        var load = TemplateMarkupStore.Load(destination);
        Assert.Null(load.Problem);
        Assert.Equal(TwoFields, load.Markup!.Regions);
    }

    [OcrFact]
    public void Broken_sidecar_does_not_fall_back_to_old_colored_frames()
    {
        using var work = new Workbench();
        var file = work.AddTemplate("Чертеж", FormKind.Drawing);
        File.WriteAllText(TemplateMarkupStore.PathFor(file), "{broken");

        var result = TemplateLibrary.Load(file);
        Assert.Null(result.Template);
        Assert.Contains("поврежден файл разметки", result.Problem);
    }

    [OcrTheory]
    [InlineData("version", 99, "версия")]
    [InlineData("pageWidth", 100, "размер страницы")]
    [InlineData("pageIndex", 2, "страница разметки")]
    public void Invalid_metadata_has_actionable_problem(string property, int value, string expected)
    {
        using var dir = new TempDir();
        var file = CreatePdf(dir);
        Assert.Null(TemplateMarkupStore.Save(file, 0, TwoFields, typeAbsent: true));
        var path = TemplateMarkupStore.PathFor(file);
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        json[property] = value;
        File.WriteAllText(path, json.ToJsonString());

        Assert.Contains(expected, TemplateMarkupStore.Load(file).Problem);
    }

    [Fact]
    public void Type_omission_is_explicit_and_cannot_conflict_with_region()
    {
        Assert.Contains("выделите тип", TemplateMarkupStore.Validate(TwoFields, false));
        Assert.Null(TemplateMarkupStore.Validate(TwoFields, true));
        TemplateRegion[] three = [.. TwoFields, new(FieldRole.Type, new NormRect(0.4, 0.8, 0.3, 0.1))];
        Assert.Null(TemplateMarkupStore.Validate(three, false));
        Assert.Contains("уберите желтую", TemplateMarkupStore.Validate(three, true));
    }

    [Fact]
    public void Combined_area_is_an_exclusive_alternative()
    {
        TemplateRegion[] combined = [new(FieldRole.Combined, new NormRect(0.4, 0.6, 0.5, 0.3))];
        Assert.Null(TemplateMarkupStore.Validate(combined, false));
        Assert.NotNull(TemplateMarkupStore.Validate(combined, true));
        Assert.NotNull(TemplateMarkupStore.Validate([.. combined, TwoFields[0]], false));
    }

    [Theory]
    [InlineData(-0.1, 0.2, 0.1, 0.1)]
    [InlineData(0.9, 0.2, 0.2, 0.1)]
    [InlineData(0.1, 0.9, 0.2, 0.2)]
    [InlineData(0.1, 0.2, 0, 0.1)]
    [InlineData(0, 0.2, 1.0000000005, 0.1)]
    [InlineData(1.0000000005, 0.2, 0.0000000001, 0.1)]
    [InlineData(double.NaN, 0.2, 0.1, 0.1)]
    [InlineData(0.1, double.PositiveInfinity, 0.1, 0.1)]
    public void Invalid_coordinates_are_rejected(double x, double y, double width, double height)
    {
        Assert.Contains("внутри страницы", TemplateMarkupStore.Validate(
            [new(FieldRole.Combined, new NormRect(x, y, width, height))], false));
    }

    [Fact]
    public void Duplicate_roles_and_unknown_roles_are_rejected()
    {
        Assert.Contains("одна область", TemplateMarkupStore.Validate([TwoFields[0], TwoFields[0]], true));
        Assert.Contains("неизвестное", TemplateMarkupStore.Validate(
            [new((FieldRole)99, new NormRect(0.1, 0.1, 0.1, 0.1))], false));
    }
}
