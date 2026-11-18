using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Pdf;
using DocumentTextRecognition.Core.Templates;
using OpenCvSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using RasterPdf = DocumentTextRecognition.Core.Pdf.PdfDocument;

namespace DocumentTextRecognition.Tests.Samples;

internal static partial class GostTypeASamples
{
    internal sealed record PairedPdf(string VectorPath, string RasterPath, string TemplatePath,
        IReadOnlyList<TemplateRegion> Regions, PageSize PageSize, int RasterDpi);

    /// <summary>
    /// The same visible page with and without a text layer. The source strings are supplied
    /// by the test's frozen truth; neither a filename nor a template contains the answer.
    /// </summary>
    internal static PairedPdf CreatePair(Workbench wb, string stem, string designation, string name,
        string? documentType = null, int dpi = 300)
    {
        var kind = documentType is null ? FormKind.Specification : FormKind.Drawing;
        var fields = SyntheticForms.Fields(kind);
        var regions = new List<TemplateRegion>
        {
            Region(FieldRole.Designation, fields.Designation, fields),
            Region(FieldRole.Name, fields.Name, fields),
        };
        if (documentType is not null) regions.Add(Region(FieldRole.Type, fields.Type, fields));
        var size = new PageSize(fields.PageWidthMm * 72 / 25.4, fields.PageHeightMm * 72 / 25.4);
        var vector = VectorPage(wb.Source, stem + "-vector", designation, name, documentType, fields);
        var template = VectorPage(wb.Templates, stem + "-layout", "ОБРЗ.000000.000", "Образец",
            documentType is null ? null : "Тип документа", fields);
        var problem = TemplateMarkupStore.Save(template, 0, regions, typeAbsent: documentType is null);
        if (problem is not null) throw new InvalidOperationException(problem);

        var raster = Path.Combine(wb.Source, stem + "-raster.pdf");
        using (var pdf = RasterPdf.Open(vector))
        using (var image = pdf.RenderPage(0, (int)Math.Round(Math.Max(size.Width, size.Height) * dpi / 72)))
            SaveRasterAtSize(raster, image, size);
        return new PairedPdf(vector, raster, template, regions, size, dpi);
    }

    private static string VectorPage(string folder, string stem, string designation, string name,
        string? documentType, FormFields f)
    {
        const double pointsPerMm = 72 / 25.4;
        using var font = OpenFont();
        var lines = new List<PdfTextFixtures.Line>();
        void Text(string value, double xMm, double baselineMm, double capMm)
        {
            using var paint = TextPaint(font, capMm, 72);
            lines.Add(new(value, xMm * pointsPerMm, (f.PageHeightMm - baselineMm) * pointsPerMm, paint.TextSize));
        }
        void Field(string value, RectMm r, double capMm)
        {
            using var paint = TextPaint(font, capMm, 72);
            var values = value.Split('\n');
            var step = capMm * 1.8;
            var firstBaseline = r.Y + (r.Height - capMm - (values.Length - 1) * step) / 2 + capMm;
            for (var index = 0; index < values.Length; index++)
            {
                if (paint.GetGlyphs(values[index]).Any(g => g == 0))
                    throw new InvalidDataException("Pinned GOST font has no source glyph.");
                var bounds = new SKRect();
                paint.MeasureText(values[index], ref bounds);
                if (bounds.Width > (r.Width - 3) * pointsPerMm || capMm + (values.Length - 1) * step > r.Height - 3)
                    throw new InvalidDataException("Source text does not fit the saved field: " + value);
                var x = (r.X + r.Width / 2) * pointsPerMm - (bounds.Left + bounds.Right) / 2;
                lines.Add(new(values[index], x,
                    (f.PageHeightMm - firstBaseline - index * step) * pointsPerMm, paint.TextSize));
            }
        }

        Field(designation, f.Designation, 5);
        Field(name, f.Name, 3.5);
        if (documentType is not null) Field(documentType, f.Type, 2.8);
        Text("Обозначение", 72, 12, 2.5);
        Text("Наименование", 134, 12, 2.5);
        for (var index = 0; index < 24 && 24 + index * 8 < f.Designation.Y; index++)
        {
            var y = 16 + index * 8;
            Text((index + 1).ToString(), 37, y + 5.5, 2);
            Text($"ЭЛМТ.000000.{index + 1:000}", 62, y + 5.5, 2.5);
            Text(index % 3 == 0 ? "Деталь" : index % 3 == 1 ? "Материал" : "Документ", 123, y + 5.5, 2.5);
            Text((index % 7 + 1).ToString(), 188, y + 5.5, 2);
        }
        var source = PdfTextFixtures.Create(folder, stem + "-text", [new(lines,
            f.PageWidthMm * pointsPerMm, f.PageHeightMm * pointsPerMm)]);
        var output = Path.Combine(folder, stem + ".pdf");
        try
        {
            using var document = PdfReader.Open(source, PdfDocumentOpenMode.Modify);
            using (var graphics = XGraphics.FromPdfPage(document.Pages[0], XGraphicsPdfPageOptions.Append))
            {
                var thin = new XPen(XColors.Black, 0.25 * pointsPerMm);
                var thick = new XPen(XColors.Black, 0.6 * pointsPerMm);
                void Segment(double x1, double y1, double x2, double y2, bool heavy = false) =>
                    graphics.DrawLine(heavy ? thick : thin, x1 * pointsPerMm, y1 * pointsPerMm, x2 * pointsPerMm, y2 * pointsPerMm);
                void Box(RectMm r) => graphics.DrawRectangle(thick, r.X * pointsPerMm, r.Y * pointsPerMm,
                    r.Width * pointsPerMm, r.Height * pointsPerMm);
                Box(new(20, 5, 185, 287));
                var top = f.Designation.Y;
                Box(new(20, top, 185, 292 - top));
                foreach (var x in new[] { 27d, 37, 60, 75, 85 }) Segment(x, top, x, 292);
                for (var y = top + 5; y < 292; y += 5) Segment(20, y, 85, y);
                Segment(85, top + 15, 205, top + 15, true);
                Segment(155, top + 15, 155, 292, true);
                Segment(155, top + 20, 205, top + 20);
                Segment(155, top + 25, 205, top + 25);
                Segment(170, top + 15, 170, top + 25);
                Segment(185, top + 15, 185, top + 25);
                Segment(20, 15, 205, 15, true);
                foreach (var x in new[] { 27d, 35, 45, 60, 120, 185, 195 }) Segment(x, 5, x, top);
                for (var index = 0; index < 24 && 24 + index * 8 < top; index++) Segment(20, 23 + index * 8, 205, 23 + index * 8);
            }
            document.Save(output);
        }
        finally { File.Delete(source); }
        return output;
    }

    private static void SaveRasterAtSize(string path, Mat pixels, PageSize size)
    {
        Cv2.ImEncode(".png", pixels, out var png);
        using var pdf = new PdfSharp.Pdf.PdfDocument();
        var page = pdf.AddPage();
        page.Width = XUnit.FromPoint(size.Width);
        page.Height = XUnit.FromPoint(size.Height);
        using (var graphics = XGraphics.FromPdfPage(page))
        using (var stream = new MemoryStream(png))
        using (var image = XImage.FromStream(stream))
            graphics.DrawImage(image, 0, 0, size.Width, size.Height);
        pdf.Save(path);
    }
}
