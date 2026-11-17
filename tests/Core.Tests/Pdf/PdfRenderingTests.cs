using DocumentTextRecognition.Core.Pdf;
using DocumentTextRecognition.Tests.Samples;
using OpenCvSharp;

namespace DocumentTextRecognition.Tests.Pdf;

public sealed class PdfRenderingTests
{
    [OcrTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reused_page_renders_identical_pixels_to_fresh_page_for_preview_and_fields(bool vectorFrames)
    {
        using var work = new Workbench();
        var file = vectorFrames
            ? work.AddTemplate("Векторные рамки", FormKind.Drawing, Marking.SeparateVector)
            : work.AddDocument("scan.pdf", FormKind.Specification, "АБВГ.466229.001", "Плата");
        using var reused = PdfDocument.Open(file);
        var size = reused.GetPageSize(0);

        using (var expected = RenderFresh(file, 0, 1600))
        using (var actual = reused.RenderPage(0, 1600))
            EqualPixels(expected, actual);

        // Меняются масштаб и начало области: PDFium должен повторно использовать только
        // декодированную страницу, а не картинку предыдущего фрагмента.
        var regions = new[]
        {
            (X: 0.4, Y: 0.8, W: 0.55, H: 0.055, Scale: 300 / 72.0),
            (X: 0.4, Y: 0.855, W: 0.35, H: 0.055, Scale: 300 / 72.0),
            (X: 0.4, Y: 0.91, W: 0.35, H: 0.04, Scale: 300 / 72.0),
            (X: 0.0, Y: 0.0, W: 1.0, H: 1.0, Scale: 100 / 72.0),
        };
        foreach (var region in regions.Concat(regions.Reverse()))
        {
            using var fresh = PdfDocument.Open(file);
            using var expected = fresh.RenderRegion(0, region.Scale, region.X * size.Width, region.Y * size.Height,
                region.W * size.Width, region.H * size.Height);
            using var actual = reused.RenderRegion(0, region.Scale, region.X * size.Width, region.Y * size.Height,
                region.W * size.Width, region.H * size.Height);
            EqualPixels(expected, actual);
        }
    }

    [OcrFact]
    public void Switching_pages_of_different_sizes_and_concurrent_reads_do_not_reuse_wrong_page()
    {
        using var work = new Workbench();
        using var first = SyntheticForms.Render(FormKind.Specification, "АБВГ.466229.001", "Плата", 150);
        using var second = SyntheticForms.Render(FormKind.DrawingA3, "ЮИЛГ.461411.123", "Корпус", 150);
        var file = work.AddPages("pages.pdf", [first, second], 150);
        using var expectedFirst = RenderFresh(file, 0, 700);
        using var expectedSecond = RenderFresh(file, 1, 700);
        using var pdf = PdfDocument.Open(file);

        // Несколько вызывающих потоков делят один документ. Каждый рендер и смена текущей
        // страницы остаются под общим замком PDFium.
        Parallel.For(0, 12, iteration =>
        {
            var index = iteration % 2;
            using var actual = pdf.RenderPage(index, 700);
            EqualPixels(index == 0 ? expectedFirst : expectedSecond, actual);
        });

        Parallel.Invoke(pdf.Dispose, pdf.Dispose);
        Assert.Throws<ObjectDisposedException>(() => pdf.GetPageSize(0));
        Assert.Throws<ObjectDisposedException>(() => pdf.RenderPage(1, 700));
    }

    private static Mat RenderFresh(string file, int page, int longSide)
    {
        using var pdf = PdfDocument.Open(file);
        return pdf.RenderPage(page, longSide);
    }

    private static void EqualPixels(Mat expected, Mat actual)
    {
        Assert.Equal(expected.Size(), actual.Size());
        Assert.Equal(expected.Type(), actual.Type());
        Assert.Equal(0, Cv2.Norm(expected, actual, NormTypes.INF));
    }
}
