using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Templates;
using OpenCvSharp;
using PdfSharp.Drawing;
using SkiaSharp;

namespace DocumentTextRecognition.Tests.Samples;

internal static partial class GostTypeASamples
{
    internal sealed record Sample(string PdfPath, string TemplatePath, IReadOnlyList<TemplateRegion> Regions);

    internal static Sample Create(Workbench wb, string stem, string designation, string name,
        string? documentType = null, int dpi = 300, int quarterTurns = 0, double skewDegrees = 0)
    {
        var kind = documentType is null ? FormKind.Specification : FormKind.Drawing;
        var fields = SyntheticForms.Fields(kind);
        var regions = new List<TemplateRegion>
        {
            Region(FieldRole.Designation, fields.Designation, fields),
            Region(FieldRole.Name, fields.Name, fields),
        };
        if (documentType is not null) regions.Add(Region(FieldRole.Type, fields.Type, fields));
        // The template deliberately contains unrelated values. Its role is only geometry.
        using var template = RenderPage(kind, "ОБРЗ.000000.000", "Образец", documentType is null ? null : "Тип документа", dpi);
        var templatePath = Path.Combine(wb.Templates, stem + "-layout.pdf");
        SaveRasterPdf(templatePath, template, dpi);
        var problem = TemplateMarkupStore.Save(templatePath, 0, regions, typeAbsent: documentType is null);
        if (problem is not null) throw new InvalidOperationException(problem);

        using var page = RenderPage(kind, designation, name, documentType, dpi);
        using var transformed = SyntheticForms.Scan(page, new ScanOptions
        {
            Dpi = dpi, QuarterTurns = quarterTurns, SkewDegrees = skewDegrees,
        }, fields.PageWidthMm);
        var pdfPath = Path.Combine(wb.Source, stem + ".pdf");
        SaveRasterPdf(pdfPath, transformed, dpi);
        return new Sample(pdfPath, templatePath, regions);
    }

    private static TemplateRegion Region(FieldRole role, RectMm rectangle, FormFields fields)
    {
        var inner = rectangle.Inflate(-1.5);
        return new TemplateRegion(role, new NormRect(inner.X / fields.PageWidthMm, inner.Y / fields.PageHeightMm,
            inner.Width / fields.PageWidthMm, inner.Height / fields.PageHeightMm));
    }

    private static Mat RenderPage(FormKind kind, string designation, string name, string? documentType, int dpi)
    {
        var f = SyntheticForms.Fields(kind);
        var px = dpi / 25.4;
        using var font = OpenFont();
        using var surface = SKSurface.Create(new SKImageInfo((int)Math.Round(f.PageWidthMm * px),
            (int)Math.Round(f.PageHeightMm * px)));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        using var line = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = (float)(0.25 * px), IsAntialias = true };
        using var heavy = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = (float)(0.6 * px), IsAntialias = true };
        void Segment(double x1, double y1, double x2, double y2, SKPaint? p = null) =>
            canvas.DrawLine((float)(x1 * px), (float)(y1 * px), (float)(x2 * px), (float)(y2 * px), p ?? line);
        void Box(RectMm r, SKPaint p) => canvas.DrawRect((float)(r.X * px), (float)(r.Y * px), (float)(r.Width * px), (float)(r.Height * px), p);
        void Text(string value, double x, double baseline, double capMm)
        {
            using var paint = TextPaint(font, capMm, dpi);
            canvas.DrawText(value, (float)(x * px), (float)(baseline * px), paint);
        }
        void Field(string value, RectMm r, double capMm)
        {
            using var paint = TextPaint(font, capMm, dpi);
            var lines = value.Split('\n');
            var step = capMm * 1.8;
            var firstBaseline = r.Y + (r.Height - capMm - (lines.Length - 1) * step) / 2 + capMm;
            for (var i = 0; i < lines.Length; i++)
            {
                var b = new SKRect();
                paint.MeasureText(lines[i], ref b);
                var left = (float)((r.X + r.Width / 2) * px) - (b.Left + b.Right) / 2;
                canvas.DrawText(lines[i], left, (float)((firstBaseline + i * step) * px), paint);
            }
        }

        Box(new RectMm(20, 5, 185, 287), heavy);
        var top = f.Designation.Y;
        Box(new RectMm(20, top, 185, 292 - top), heavy);
        foreach (var x in new[] { 27d, 37, 60, 75, 85 }) Segment(x, top, x, 292);
        for (var y = top + 5; y < 292; y += 5) Segment(20, y, 85, y);
        Segment(85, top + 15, 205, top + 15, heavy);
        Segment(155, top + 15, 155, 292, heavy);
        Segment(155, top + 20, 205, top + 20);
        Segment(155, top + 25, 205, top + 25);
        Segment(170, top + 15, 170, top + 25);
        Segment(185, top + 15, 185, top + 25);
        Text("Лит.", 158, top + 19, 2);
        Text("Лист", 172, top + 19, 2);
        Text("Листов", 187, top + 19, 2);
        Text("1", 176, top + 24, 2.5);
        Text("1", 193, top + 24, 2.5);
        Text("АО Пример", 161, top + 34, 2.5);
        var labels = new[] { "Изм.", "Лист", "Разраб.", "Пров.", "Н.контр.", "Утв." };
        for (var i = 0; i < labels.Length; i++) Text(labels[i], 21, top + 4 + i * 5, 1.8);
        Text("Иванов", 39, top + 14, 1.8);
        Text("Петров", 39, top + 19, 1.8);

        // A deterministic asymmetric table provides registration features outside the fields.
        // No target designation/name is repeated elsewhere on the page.
        Segment(20, 15, 205, 15, heavy);
        foreach (var x in new[] { 27d, 35, 45, 60, 120, 185, 195 }) Segment(x, 5, x, top);
        Text("Обозначение", 72, 12, 2.5);
        Text("Наименование", 134, 12, 2.5);
        for (var i = 0; i < 24 && 24 + i * 8 < top; i++)
        {
            var y = 16 + i * 8;
            Segment(20, y + 7, 205, y + 7);
            Text((i + 1).ToString(), 37, y + 5.5, 2);
            Text($"ЭЛМТ.000000.{i + 1:000}", 62, y + 5.5, 2.5);
            Text(i % 3 == 0 ? "Деталь" : i % 3 == 1 ? "Материал" : "Документ", 123, y + 5.5, 2.5);
            Text((i % 7 + 1).ToString(), 188, y + 5.5, 2);
        }

        Field(designation, f.Designation, 5);
        Field(name, f.Name, 3.5);
        if (documentType is not null) Field(documentType, f.Type, 2.8);
        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return Cv2.ImDecode(png.ToArray(), ImreadModes.Color);
    }

    private static void SaveRasterPdf(string path, Mat page, int dpi)
    {
        Cv2.ImEncode(".png", page, out var pixels);
        using var pdf = new PdfSharp.Pdf.PdfDocument();
        var sheet = pdf.AddPage();
        sheet.Width = XUnit.FromPoint(page.Width * 72.0 / dpi);
        sheet.Height = XUnit.FromPoint(page.Height * 72.0 / dpi);
        using (var graphics = XGraphics.FromPdfPage(sheet))
        using (var stream = new MemoryStream(pixels))
        using (var image = XImage.FromStream(stream))
            graphics.DrawImage(image, 0, 0, sheet.Width.Point, sheet.Height.Point);
        pdf.Save(path);
    }
}
