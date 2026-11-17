using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Templates;
using OpenCvSharp;

namespace DocumentTextRecognition.Tests.Samples;

/// <summary>
/// Демонстрационные сканы с областями, сохранёнными так же, как их сохраняет редактор.
/// В самих PDF нет цветных рамок: проверяется чтение именно по сохранённым координатам.
/// </summary>
internal static class ManualTemplateSamples
{
    internal sealed record Sample(string FileName, string TemplateName, FormKind Kind,
        string Designation, string Name, string Type, bool TypeAbsent = false, bool Combined = false,
        bool AllowOcrRefusal = false)
    {
        public string ExpectedName => $"{Designation} {Name} {Type}.pdf";
    }

    internal static readonly Sample[] Cases =
    [
        new("01-specification-a4.pdf", "Спецификация A4", FormKind.Specification,
            "ВКЕМ.461411.200", "Модуль связи", "Спецификация", TypeAbsent: true),
        new("02-assembly-a4.pdf", "Основная надпись A4", FormKind.Drawing,
            "ЮИЛГ.461411.123", "Корпус", "Сборочный чертеж"),
        new("03-schematic-a4.pdf", "Основная надпись A4", FormKind.Drawing,
            "ЮИЛГ.461411.124", "Блок питания", "Схема электрическая"),
        new("04-assembly-a3.pdf", "Основная надпись A3", FormKind.DrawingA3,
            "ПРМД.301261.010", "Рама сварная", "Сборочный чертеж", Combined: true),
        new("05-screenshot-like-specification.pdf", "Спецификация A4", FormKind.Specification,
            "АЦИЕ.202132.004", "Э(Т)О МОЭА", "Спецификация", TypeAbsent: true, AllowOcrRefusal: true),
    ];

    internal static string AddTemplate(Workbench wb, Sample sample, int pageIndex = 0)
    {
        const int dpi = 200;
        using var page = SyntheticForms.Render(sample.Kind, "ОБРЗ.000000.000", "Образец", dpi,
            seed: 7, docType: sample.TypeAbsent ? null : "Тип документа");
        var path = Path.Combine(wb.Templates, sample.TemplateName + ".pdf");
        if (pageIndex == 0)
            SyntheticForms.SavePdf(path, [page], dpi, 92);
        else
        {
            using var cover = new Mat(page.Size(), MatType.CV_8UC3, Scalar.All(255));
            Cv2.PutText(cover, "COVER", new Point(150, 350), HersheyFonts.HersheySimplex, 3, Scalar.All(0), 4);
            SyntheticForms.SavePdf(path, [cover, page], dpi, 92);
        }
        var error = TemplateMarkupStore.Save(path, pageIndex, Regions(sample.Kind, sample.Combined, sample.TypeAbsent), sample.TypeAbsent);
        if (error is not null) throw new InvalidOperationException(error);
        return path;
    }

    internal static IReadOnlyList<TemplateRegion> Regions(FormKind kind, bool combined, bool typeAbsent)
    {
        var f = SyntheticForms.Fields(kind);
        TemplateRegion Region(FieldRole role, RectMm bounds)
        {
            // Не захватывать соседние графы. При чтении добавляется запас FieldCrop.MarginMm.
            var r = bounds.Inflate(-1.5);
            return new TemplateRegion(role, new NormRect(r.X / f.PageWidthMm, r.Y / f.PageHeightMm,
                r.Width / f.PageWidthMm, r.Height / f.PageHeightMm));
        }
        if (combined) return [Region(FieldRole.Combined, f.Both)];
        List<TemplateRegion> regions = [Region(FieldRole.Designation, f.Designation), Region(FieldRole.Name, f.Name)];
        if (!typeAbsent) regions.Add(Region(FieldRole.Type, f.Type));
        return regions;
    }

    internal static string AddDocument(Workbench wb, Sample sample, ScanOptions? options = null) =>
        wb.AddDocument(sample.FileName, sample.Kind, sample.Designation, sample.Name,
            options ?? new ScanOptions { Dpi = 250, JpegQuality = 92 },
            docType: sample.TypeAbsent ? null : sample.Type);
}
