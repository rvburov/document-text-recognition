using System.Security.Cryptography;
using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Pdf;
using DocumentTextRecognition.Tests.Samples;

namespace DocumentTextRecognition.Tests.Pdf;

/// <summary>Actual PDFium extraction from independent vector PDFs; no OCR or expected-text injection.</summary>
public sealed class PdfTextExtractionTests
{
    private const string Designation = "УИЕС.468732.271";
    private const string Name = "Плата Epcos USB";
    private static readonly NormRect WholePage = new(0, 0, 1, 1);

    [OcrFact]
    public void Explicit_unicode_map_preserves_cyrillic_latin_lines_and_character_geometry()
    {
        using var folder = new TempDir("pdf-text-unicode");
        var path = PdfTextFixtures.Create(folder.Path, "scan", [Page()]);
        var before = Hash(path);
        using var pdf = PdfDocument.Open(path);
        var page = pdf.ReadTextPage(0);
        var selected = page.Select(WholePage);

        Assert.Null(page.Problem);
        Assert.True(selected.CanUseCandidate, selected.Problem + "\n" + string.Join("\n", page.Characters.Select(c =>
            $"{c.Index}: U+{c.Unicode:X4} '{c.Text}' {c.Bounds} generated={c.IsGenerated}")));
        Assert.True(selected.RequiresIndependentValidation);
        Assert.Equal(new[] { Designation, Name }, selected.Lines.Select(line => line.Text));
        Assert.Equal(Designation + "\n" + Name, selected.Text.Replace("\r\n", "\n"));
        Assert.NotEmpty(selected.Characters);
        foreach (var character in selected.Characters.Where(c => !c.IsGenerated && !string.IsNullOrWhiteSpace(c.Text)))
        {
            Assert.True(character.Bounds.Width > 0 && character.Bounds.Height > 0);
            Assert.InRange(character.Bounds.X, 0, page.Size.Width);
            Assert.InRange(character.Bounds.Y, 0, page.Size.Height);
        }
        Assert.Equal(Hash(path), before);
    }

    [OcrTheory]
    [InlineData(0, false)]
    [InlineData(90, false)]
    [InlineData(180, false)]
    [InlineData(270, false)]
    [InlineData(0, true)]
    [InlineData(90, true)]
    [InlineData(180, true)]
    [InlineData(270, true)]
    public void Character_boxes_and_affine_roi_follow_page_rotation_and_crop_origin(int rotation, bool cropOffset)
    {
        using var folder = new TempDir("pdf-text-rotation");
        PdfTextFixtures.Box? crop = cropOffset ? new(20, 10, 300, 210) : null;
        var path = PdfTextFixtures.Create(folder.Path, "rotated", [Page(crop: crop), Page(rotation, crop)]);
        using var pdf = PdfDocument.Open(path);
        var baseline = pdf.ReadTextPage(0);
        var rotated = pdf.ReadTextPage(1);
        var width = cropOffset ? 280d : 320d;
        var height = cropOffset ? 200d : 220d;
        Assert.Equal(width, baseline.Size.Width, 5);
        Assert.Equal(height, baseline.Size.Height, 5);
        Assert.Equal(rotation % 180 == 0 ? width : height, rotated.Size.Width, 5);
        Assert.Equal(rotation % 180 == 0 ? height : width, rotated.Size.Height, 5);
        var remaining = rotated.Characters.Where(c => !c.IsGenerated && !c.IsWhitespace).ToList();
        foreach (var source in baseline.Characters.Where(c => !c.IsGenerated && !c.IsWhitespace))
        {
            var corners = new[]
            {
                Rotate(source.Bounds.X, source.Bounds.Y, width, height, rotation),
                Rotate(source.Bounds.X + source.Bounds.Width, source.Bounds.Y, width, height, rotation),
                Rotate(source.Bounds.X, source.Bounds.Y + source.Bounds.Height, width, height, rotation),
                Rotate(source.Bounds.X + source.Bounds.Width, source.Bounds.Y + source.Bounds.Height, width, height, rotation),
            };
            // /Rotate can change PDFium's text-object traversal and generated CR/LF.
            // Every actual glyph must still exist exactly once at its analytic location.
            var actual = Assert.Single(remaining, c => c.Unicode == source.Unicode &&
                Math.Abs(c.Bounds.X - corners.Min(p => p.X)) <= .02 &&
                Math.Abs(c.Bounds.Y - corners.Min(p => p.Y)) <= .02 &&
                Math.Abs(c.Bounds.Width - (corners.Max(p => p.X) - corners.Min(p => p.X))) <= .02 &&
                Math.Abs(c.Bounds.Height - (corners.Max(p => p.Y) - corners.Min(p => p.Y))) <= .02);
            remaining.Remove(actual);
            Close(corners.Min(p => p.X), actual.Bounds.X);
            Close(corners.Min(p => p.Y), actual.Bounds.Y);
            Close(corners.Max(p => p.X) - corners.Min(p => p.X), actual.Bounds.Width);
            Close(corners.Max(p => p.Y) - corners.Min(p => p.Y), actual.Bounds.Height);
        }
        Assert.Empty(remaining);
        // The affine region's axes follow the logical text, just as an inverse template
        // alignment does. Selecting an axis-aligned enclosing box would mix up its rows.
        var region = new PdfTextRegion(Rotate(0, 0, width, height, rotation),
            Rotate(width, 0, width, height, rotation), Rotate(0, height, width, height, rotation));
        var selection = rotated.Select(region);
        Assert.True(selection.CanUseCandidate, selection.Problem);
        Assert.Equal(new[] { Designation, Name }, selection.Lines.Select(line => line.Text));
        Assert.True(selection.RequiresIndependentValidation);
    }

    [OcrFact]
    public void Nonoverlapping_roi_is_empty_but_a_cut_character_rejects_the_partial_text()
    {
        using var folder = new TempDir("pdf-text-partial");
        var path = PdfTextFixtures.Create(folder.Path, "scan", [Page()]);
        using var pdf = PdfDocument.Open(path);
        var page = pdf.ReadTextPage(0);
        var empty = page.Select(new NormRect(0, .9, .1, .1));
        Assert.Equal("Empty", empty.Status.ToString());
        Assert.False(empty.CanUseCandidate);
        Assert.Empty(empty.Text);

        var whole = page.Select(WholePage);
        Assert.True(whole.CanUseCandidate, whole.Problem);
        var line = whole.Lines[0];
        var letters = line.Characters.Where(c => !c.IsGenerated && !string.IsNullOrWhiteSpace(c.Text)).ToArray();
        var first = letters[0].Bounds;
        var left = first.X + first.Width / 2;
        var top = letters.Min(c => c.Bounds.Y) - 2;
        var right = letters.Max(c => c.Bounds.X + c.Bounds.Width) + 2;
        var bottom = letters.Max(c => c.Bounds.Y + c.Bounds.Height) + 2;
        var cut = page.Select(new NormRect(left / page.Size.Width, top / page.Size.Height,
            (right - left) / page.Size.Width, (bottom - top) / page.Size.Height));
        Untrusted(cut);
    }

    [OcrTheory]
    [InlineData("missing-map")]
    [InlineData("replacement-character")]
    [InlineData("hidden-text")]
    [InlineData("overlapping-text")]
    public void Invalid_mapping_hidden_text_and_conflicting_overlays_are_explicitly_untrusted(string variant)
    {
        using var folder = new TempDir("pdf-text-untrusted");
        var lines = variant == "overlapping-text"
            ? new[] { new PdfTextFixtures.Line("АБВГ"), new PdfTextFixtures.Line("ДЕЖЗ") }
            : [new PdfTextFixtures.Line(Designation, RenderMode: variant == "hidden-text" ? 3 : 0)];
        var overrides = variant == "replacement-character" ? new Dictionary<char, string> { ['У'] = "\uFFFD" } : null;
        var path = PdfTextFixtures.Create(folder.Path, "scan", [new PdfTextFixtures.Page(lines)],
            includeToUnicode: variant != "missing-map", unicodeOverrides: overrides);
        using var pdf = PdfDocument.Open(path);
        Untrusted(pdf.ReadTextPage(0).Select(WholePage));
    }

    [OcrFact]
    public void Plausible_but_false_unicode_mapping_never_claims_independent_trust()
    {
        using var folder = new TempDir("pdf-text-false-map");
        // The embedded glyph is still У; a valid but dishonest CMap says Ч.
        // PDFium cannot establish this semantic mismatch, so a candidate must be
        // validated against visible pixels before the application could rename anything.
        var path = PdfTextFixtures.Create(folder.Path, "scan", [Page()],
            unicodeOverrides: new Dictionary<char, string> { ['У'] = "Ч" });
        using var pdf = PdfDocument.Open(path);
        var selected = pdf.ReadTextPage(0).Select(WholePage);
        Assert.True(selected.CanUseCandidate, selected.Problem);
        Assert.Contains("ЧИЕС.468732.271", selected.Text);
        Assert.DoesNotContain(Designation, selected.Text);
        Assert.True(selected.RequiresIndependentValidation);
    }

    [OcrFact]
    public void Text_snapshot_survives_page_switch_and_disposal_without_native_handles_escaping()
    {
        using var folder = new TempDir("pdf-text-lifetime");
        var path = PdfTextFixtures.Create(folder.Path, "pages",
            [Page(), new PdfTextFixtures.Page([new PdfTextFixtures.Line("Вторая страница")])]);
        using var pdf = PdfDocument.Open(path);
        var first = pdf.ReadTextPage(0);
        var second = pdf.ReadTextPage(1);
        Assert.Equal(new[] { Designation, Name }, first.Select(WholePage).Lines.Select(l => l.Text));
        Assert.Equal("Вторая страница", second.Select(WholePage).Text);
        Parallel.For(0, 8, index =>
        {
            var selected = pdf.ReadTextPage(index % 2).Select(WholePage);
            Assert.True(selected.CanUseCandidate, selected.Problem);
            Assert.Contains(index % 2 == 0 ? Designation : "Вторая страница", selected.Text);
        });
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => pdf.ReadTextPage(0, cancellation.Token));
        }
        pdf.Dispose();
        pdf.Dispose();
        Assert.Equal(new[] { Designation, Name }, first.Select(WholePage).Lines.Select(l => l.Text));
        Assert.Throws<ObjectDisposedException>(() => pdf.ReadTextPage(0));
    }

    private static PdfTextFixtures.Page Page(int rotation = 0, PdfTextFixtures.Box? crop = null) =>
        new([new PdfTextFixtures.Line(Designation), new PdfTextFixtures.Line(Name, Y: 105)], Rotation: rotation, Crop: crop);

    private static PdfTextPoint Rotate(double x, double y, double width, double height, int rotation) => rotation switch
    {
        0 => new(x, y),
        90 => new(height - y, x),
        180 => new(width - x, height - y),
        270 => new(y, width - x),
        _ => throw new ArgumentOutOfRangeException(nameof(rotation)),
    };

    private static void Close(double expected, double actual) => Assert.InRange(Math.Abs(actual - expected), 0, .02);

    private static void Untrusted(PdfTextSelection selection)
    {
        Assert.Equal("Untrusted", selection.Status.ToString());
        Assert.False(selection.CanUseCandidate);
        Assert.False(string.IsNullOrWhiteSpace(selection.Problem));
        Assert.True(selection.RequiresIndependentValidation);
    }

    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
}
