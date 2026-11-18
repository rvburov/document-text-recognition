using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Tests.Samples;
using OpenCvSharp;

namespace DocumentTextRecognition.Tests.Ocr;

/// <summary>Raw-line подготовка сохраняет полное поле либо оставляет его обычному block OCR.</summary>
public sealed class GostRawLineTests
{
    [OcrFact]
    public void Complete_line_keeps_punctuation_and_gray_pixels_with_a_white_border()
    {
        using var image = Line();
        // Both marks lie between glyphs, inside the line's envelope. The gray dot must
        // survive even though it is much lighter than the main text.
        Cv2.Rectangle(image, new Rect(76, 87, 2, 2), Scalar.All(0), -1);
        Cv2.Rectangle(image, new Rect(108, 84, 2, 2), Scalar.All(225), -1);
        using var before = image.Clone();

        using var raw = TesseractEngine.TryPrepareRawDesignation(image);

        Assert.NotNull(raw);
        Assert.Equal(0, Cv2.Norm(before, image, NormTypes.INF));
        Assert.True(raw.Width < image.Width && raw.Height < image.Height);
        var originalInk = BlackBounds(image);
        var rawInk = BlackBounds(raw);
        Assert.Equal(originalInk.Size, rawInk.Size);
        Assert.True(rawInk.Left > 0 && rawInk.Top > 0);
        Assert.Equal(rawInk.Left, raw.Width - rawInk.Right);
        Assert.Equal(rawInk.Top, raw.Height - rawInk.Bottom);

        var offset = new Point(rawInk.Left - originalInk.Left, rawInk.Top - originalInk.Top);
        AssertAllVisiblePixelsPreserved(image, raw, offset);
        Assert.Equal((byte)225, raw.At<byte>(84 + offset.Y, 108 + offset.X));
        AssertWhiteOutside(raw, rawInk);
    }

    [OcrFact]
    public void Strided_input_is_not_modified_and_returned_pixels_are_independent()
    {
        using var backing = new Mat(210, 520, MatType.CV_8UC1, Scalar.All(255));
        using var image = new Mat(backing, new Rect(17, 29, 360, 140));
        DrawLine(image, 50, 60);
        Assert.False(image.IsContinuous());
        using var before = backing.Clone();

        using var raw = TesseractEngine.TryPrepareRawDesignation(image);

        Assert.NotNull(raw);
        var rawInk = BlackBounds(raw);
        var originalInk = BlackBounds(image);
        AssertAllVisiblePixelsPreserved(image, raw,
            new Point(rawInk.Left - originalInk.Left, rawInk.Top - originalInk.Top));
        raw.SetTo(Scalar.All(123));
        Assert.Equal(0, Cv2.Norm(before, backing, NormTypes.INF));
    }

    [OcrTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Line_touching_input_edges_is_complete_and_padded_in_output_coordinates(bool bottomRight)
    {
        using var image = new Mat(140, 360, MatType.CV_8UC1, Scalar.All(255));
        DrawLine(image, bottomRight ? 182 : 0, bottomRight ? 108 : 0);
        var originalInk = BlackBounds(image);
        using var before = image.Clone();

        using var raw = TesseractEngine.TryPrepareRawDesignation(image);

        Assert.NotNull(raw);
        var rawInk = BlackBounds(raw);
        Assert.Equal(originalInk.Size, rawInk.Size);
        Assert.True(rawInk.Left > 0 && rawInk.Top > 0);
        Assert.Equal(rawInk.Left, raw.Width - rawInk.Right);
        Assert.Equal(rawInk.Top, raw.Height - rawInk.Bottom);
        AssertAllVisiblePixelsPreserved(image, raw,
            new Point(rawInk.Left - originalInk.Left, rawInk.Top - originalInk.Top));
        AssertWhiteOutside(raw, rawInk);
        Assert.Equal(0, Cv2.Norm(before, image, NormTypes.INF));
    }

    [OcrTheory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void Faint_mark_just_outside_strong_ink_is_preserved_instead_of_whitened(int distance, bool left)
    {
        using var image = Line();
        var bounds = BlackBounds(image);
        var x = left ? bounds.Left - distance : bounds.Right - 1 + distance;
        Cv2.Rectangle(image, new Rect(x, 87, 1, 2), Scalar.All(225), -1);
        using var before = image.Clone();

        using var raw = TesseractEngine.TryPrepareRawDesignation(image);

        Assert.NotNull(raw);
        var rawInk = BlackBounds(raw);
        var offset = new Point(rawInk.Left - bounds.Left, rawInk.Top - bounds.Top);
        AssertAllVisiblePixelsPreserved(image, raw, offset);
        Assert.Equal((byte)225, raw.At<byte>(87 + offset.Y, x + offset.X));
        Assert.Equal(0, Cv2.Norm(before, image, NormTypes.INF));
    }

    [OcrFact]
    public void Faint_detached_accent_above_one_glyph_remains_part_of_the_line()
    {
        using var image = Line();
        var originalInk = BlackBounds(image);
        Cv2.Rectangle(image, new Rect(52, 55, 10, 2), Scalar.All(225), -1);

        using var raw = TesseractEngine.TryPrepareRawDesignation(image);

        Assert.NotNull(raw);
        var rawInk = BlackBounds(raw);
        AssertAllVisiblePixelsPreserved(image, raw,
            new Point(rawInk.Left - originalInk.Left, rawInk.Top - originalInk.Top));
    }

    [OcrTheory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(20)]
    public void A_second_full_line_is_not_cropped_away(int gap)
    {
        using var image = new Mat(170, 360, MatType.CV_8UC1, Scalar.All(255));
        DrawLine(image, 50, 35);
        // Shift the second line so components stay distinct even without an empty row.
        DrawLine(image, 58, 67 + gap);
        using var before = image.Clone();

        using var raw = TesseractEngine.TryPrepareRawDesignation(image);

        Assert.Null(raw);
        Assert.Equal(0, Cv2.Norm(before, image, NormTypes.INF));
    }

    [OcrTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Faint_outlying_text_forces_the_full_field_path(bool secondLine)
    {
        using var image = Line();
        var x = secondLine ? 110 : 280;
        var y = secondLine ? 112 : 67;
        for (var i = 0; i < 3; i++) DrawGlyph(image, x + i * 13, y, 7, 12, 1, 225);
        // The ordinary strong-text geometry still looks like a single line. Raw mode
        // must independently notice the additional light text instead of cutting it off.
        Assert.True(KnownSingleLineRegion.TryGetBounds(image, out _));
        using var before = image.Clone();

        using var raw = TesseractEngine.TryPrepareRawDesignation(image);

        Assert.Null(raw);
        Assert.Equal(0, Cv2.Norm(before, image, NormTypes.INF));
    }

    [OcrTheory]
    [InlineData(4, false, 0)]
    [InlineData(5, false, 0)]
    [InlineData(4, true, 0)]
    [InlineData(5, true, 0)]
    [InlineData(4, false, 3)]
    [InlineData(5, false, 3)]
    [InlineData(4, true, 3)]
    [InlineData(5, true, 3)]
    public void Tiny_secondary_text_is_not_mistaken_for_disposable_noise(int height, bool above, int gap)
    {
        using var image = Line();
        var y = above ? 60 - height - gap : 92 + gap;
        for (var i = 0; i < 4; i++) DrawGlyph(image, 72 + i * 32, y, 6, height, 1, 0);

        using var raw = TesseractEngine.TryPrepareRawDesignation(image);

        Assert.Null(raw);
    }

    [OcrTheory]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    public void Nearby_faint_secondary_line_is_still_a_second_line(int height, bool above)
    {
        using var image = Line();
        var y = above ? 60 - height - 2 : 94;
        for (var i = 0; i < 4; i++) DrawGlyph(image, 72 + i * 32, y, 6, height, 1, 225);
        Assert.True(KnownSingleLineRegion.TryGetBounds(image, out _));

        using var raw = TesseractEngine.TryPrepareRawDesignation(image);

        Assert.Null(raw);
    }

    [OcrFact]
    public void Blank_field_does_not_become_a_raw_line()
    {
        using var image = new Mat(140, 360, MatType.CV_8UC1, Scalar.All(255));
        using var raw = TesseractEngine.TryPrepareRawDesignation(image);
        Assert.Null(raw);
    }

    private static Mat Line()
    {
        var image = new Mat(140, 360, MatType.CV_8UC1, Scalar.All(255));
        DrawLine(image, 50, 60);
        return image;
    }

    private static void DrawLine(Mat image, int left, int top)
    {
        for (var i = 0; i < 6; i++) DrawGlyph(image, left + i * 32, top, 18, 32, 2, 0);
    }

    private static void DrawGlyph(Mat image, int left, int top, int width, int height, int stroke, byte shade)
    {
        Cv2.Rectangle(image, new Rect(left, top, stroke, height), Scalar.All(shade), -1);
        Cv2.Rectangle(image, new Rect(left + width - stroke, top, stroke, height), Scalar.All(shade), -1);
        Cv2.Rectangle(image, new Rect(left, top + height / 2, width, stroke), Scalar.All(shade), -1);
    }

    private static Rect BlackBounds(Mat image)
    {
        using var black = new Mat();
        Cv2.InRange(image, Scalar.All(0), Scalar.All(0), black);
        return Cv2.BoundingRect(black);
    }

    private static void AssertAllVisiblePixelsPreserved(Mat original, Mat raw, Point offset)
    {
        var width = original.Width;
        var height = original.Height;
        var rawWidth = raw.Width;
        var rawHeight = raw.Height;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var shade = original.At<byte>(y, x);
            if (shade == 255) continue;
            Assert.InRange(x + offset.X, 0, rawWidth - 1);
            Assert.InRange(y + offset.Y, 0, rawHeight - 1);
            Assert.Equal(shade, raw.At<byte>(y + offset.Y, x + offset.X));
        }
    }

    private static void AssertWhiteOutside(Mat image, Rect content)
    {
        using var border = image.Clone();
        using (var inside = new Mat(border, content)) inside.SetTo(Scalar.All(255));
        Cv2.MinMaxLoc(border, out var minimum, out _, out _, out _);
        Assert.Equal(255, minimum);
    }
}
