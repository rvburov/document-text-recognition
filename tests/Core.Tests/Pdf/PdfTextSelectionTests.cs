using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Pdf;

namespace DocumentTextRecognition.Tests.Pdf;

/// <summary>Managed safety policy; native PDF geometry is covered separately.</summary>
public sealed class PdfTextSelectionTests
{
    private static readonly NormRect Whole = new(0, 0, 1, 1);
    private static PdfTextCharacter Letter(int index, char text, double x, double y = 10) =>
        new(index, text, new(x, y, 8, 10), false, false, 0, 255, 255, 12);
    private static PdfTextCharacter Space(int index) => new(index, 32, default, true, false, -1, null, null, 0);
    private static PdfTextPage Page(params PdfTextCharacter[] chars) => new(0, new(100, 100), Array.AsReadOnly(chars));

    [Fact]
    public void Candidate_preserves_generated_spaces_lines_and_original_indices_without_claiming_trust()
    {
        var page = Page(Letter(0, 'А', 10), Space(1), Letter(2, 'B', 30), Letter(3, 'Ц', 10, 40));
        var result = page.Select(Whole);
        Assert.Equal(PdfTextStatus.Candidate, result.Status);
        Assert.Equal("А B\nЦ", result.Text);
        Assert.Equal(new[] { 0, 1, 2 }, result.Lines[0].Characters.Select(c => c.Index));
        Assert.True(result.RequiresIndependentValidation);
        Assert.Null(result.Problem);
    }

    [Fact]
    public void Whole_word_and_distant_column_can_be_selected_but_partial_word_cannot()
    {
        var word = Page(Letter(0, 'А', 10), Letter(1, 'Б', 20), Letter(2, 'В', 30), Letter(3, 'Г', 40));
        Assert.Equal("АБВГ", word.Select(new NormRect(.09, .09, .4, .12)).Text);
        Untrusted(word.Select(new NormRect(.29, .09, .2, .12)));
        var columns = Page(Letter(0, 'А', 10), Letter(1, 'Б', 20), Space(2), Letter(3, 'В', 70));
        Assert.Equal("АБ", columns.Select(new NormRect(.09, .09, .2, .12)).Text);
    }

    [Fact]
    public void Cropping_between_nearby_words_is_not_a_complete_label()
    {
        var page = Page(Letter(0, 'А', 10), Letter(1, 'Б', 20), Space(2), Letter(3, 'В', 32));
        Untrusted(page.Select(new NormRect(.09, .09, .2, .12)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cropping_between_nearby_complete_lines_is_not_a_complete_name(bool selectSecondLine)
    {
        var newline = Space(2) with { Unicode = 10 };
        var page = Page(Letter(0, 'А', 10), Letter(1, 'Б', 20), newline,
            Letter(3, 'В', 10, 24), Letter(4, 'Г', 20, 24));
        Untrusted(page.Select(new NormRect(.09, selectSecondLine ? .23 : .09, .2, .12)));
        Assert.Equal("АБ\nВГ", page.Select(new NormRect(.09, .09, .2, .26)).Text);
    }

    [Fact]
    public void Reversed_object_order_cannot_silently_remove_a_word_boundary()
    {
        var page = Page(Letter(0, 'Б', 30), Space(1), Letter(2, 'А', 10));
        Untrusted(page.Select(Whole));
    }

    [Fact]
    public void Dense_geometry_has_a_finite_comparison_budget_and_explicit_refusal()
    {
        var chars = Enumerable.Range(0, 1600).Select(i => Letter(i, 'А', 10, i * 2)
            with { Bounds = new(10, i * 2, 8, 1) }).ToArray();
        var result = new PdfTextPage(0, new(100, 4000), Array.AsReadOnly(chars)).Select(Whole);
        Untrusted(result);
        Assert.Contains("сложная геометрия", result.Problem);
    }

    [Fact]
    public void Partial_glyph_and_overlaid_conflicting_glyphs_are_untrusted()
    {
        Untrusted(Page(Letter(0, 'А', 10)).Select(new NormRect(.12, 0, .5, .5)));
        Untrusted(Page(Letter(0, 'А', 10), Letter(1, 'Б', 11)).Select(Whole));
    }

    [Fact]
    public void A_normal_italic_baseline_dot_is_preserved_but_overlaid_dots_are_not()
    {
        // Actual PDFium envelopes of the licensed GOST Type A font, 20pt.
        var letter = Letter(0, 'С', 0) with { Bounds = new(65.26, 62.08, 7.96, 12.92) };
        var dot = Letter(1, '.', 0) with { Bounds = new(71.88, 74.08, .90, .92) };
        Assert.Equal("С.", Page(letter, dot).Select(Whole).Text);
        Untrusted(Page(letter, dot, dot with { Index = 2 }).Select(Whole));
        Untrusted(Page(letter, dot with { Unicode = '1' }).Select(Whole));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0xFFFD)]
    [InlineData(0xE100)]
    [InlineData(0xD800)]
    [InlineData(0x202E)]
    [InlineData(10)]
    [InlineData(13)]
    public void Invalid_unicode_or_private_mapping_cannot_be_a_candidate(int unicode) =>
        Untrusted(Page(Letter(0, 'А', 10) with { Unicode = (uint)unicode }).Select(Whole));

    [Fact]
    public void Native_mapping_error_is_untrusted_even_when_unicode_looks_correct() =>
        Untrusted(Page(Letter(0, 'А', 10) with { UnicodeMapError = true }).Select(Whole));

    [Theory]
    [InlineData(3, 255, 0)]
    [InlineData(7, 255, 0)]
    [InlineData(0, 0, 0)]
    [InlineData(0, 200, 0)]
    [InlineData(0, 255, 0xFFFFFF)]
    public void Hidden_translucent_or_white_text_does_not_claim_visible_ink(int mode, int alpha, int rgb) =>
        Untrusted(Page(Letter(0, 'А', 10) with { RenderMode = mode, FillAlpha = (uint)alpha, FillRgb = (uint)rgb }).Select(Whole));

    [Fact]
    public void Empty_outside_region_and_degenerate_affine_are_distinct()
    {
        var page = Page(Letter(0, 'А', 10));
        Assert.Equal(PdfTextStatus.Empty, page.Select(new NormRect(.8, .8, .1, .1)).Status);
        Untrusted(page.Select(new PdfTextRegion(new(0, 0), new(1, 0), new(2, 0))));
        Untrusted(page.Select(new NormRect(0, 0, double.NaN, 1)));
        Untrusted(page.Select(new PdfTextRegion(new(0, 0), new(1e200, 0), new(0, 1e200))));
    }

    [Fact]
    public void Rotated_affine_region_preserves_logical_reading_order()
    {
        // Logical A then B on a page rotated clockwise: displayed top-to-bottom.
        var page = Page(Letter(0, 'А', 50, 10) with { Bounds = new(50, 10, 10, 8) },
            Letter(1, 'Б', 50, 20) with { Bounds = new(50, 20, 10, 8) });
        var result = page.Select(new PdfTextRegion(new(70, 0), new(70, 100), new(0, 0)));
        Assert.True(result.CanUseCandidate, result.Problem);
        Assert.Equal("АБ", result.Text);
    }

    private static void Untrusted(PdfTextSelection result)
    {
        Assert.Equal(PdfTextStatus.Untrusted, result.Status);
        Assert.False(result.CanUseCandidate);
        Assert.False(string.IsNullOrWhiteSpace(result.Problem));
        Assert.True(result.RequiresIndependentValidation);
    }
}
