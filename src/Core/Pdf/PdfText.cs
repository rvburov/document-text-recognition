using System.Globalization;
using System.Text;
using DocumentTextRecognition.Core.Imaging;

namespace DocumentTextRecognition.Core.Pdf;

internal readonly record struct PdfTextPoint(double X, double Y);

/// <summary>Пункты отображаемой страницы: начало сверху слева, ось Y вниз, как RenderRegion.</summary>
internal readonly record struct PdfTextRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height);
    public bool HasArea => IsFinite && Width > 0 && Height > 0;
    internal PdfTextPoint[] Corners => [new(X, Y), new(Right, Y), new(Right, Bottom), new(X, Bottom)];
    internal static PdfTextRect Enclose(IEnumerable<PdfTextPoint> points)
    {
        var p = points.ToArray();
        var x = p.Min(v => v.X); var y = p.Min(v => v.Y);
        return new(x, y, p.Max(v => v.X) - x, p.Max(v => v.Y) - y);
    }
}

/// <summary>Область в координатах отображаемой PDF-страницы. Три угла задают аффинный
/// параллелограмм; направление его осей определяет порядок строк после совмещения.</summary>
internal readonly record struct PdfTextRegion(PdfTextPoint TopLeft, PdfTextPoint TopRight, PdfTextPoint BottomLeft)
{
    public static PdfTextRegion FromNormalized(NormRect rect, PageSize size) => FromRect(new(
        rect.X * size.Width, rect.Y * size.Height, rect.Width * size.Width, rect.Height * size.Height));
    public static PdfTextRegion FromRect(PdfTextRect rect) => new(new(rect.X, rect.Y), new(rect.Right, rect.Y), new(rect.X, rect.Bottom));
    internal double Width => double.Hypot(TopRight.X - TopLeft.X, TopRight.Y - TopLeft.Y);
    internal double Height => double.Hypot(BottomLeft.X - TopLeft.X, BottomLeft.Y - TopLeft.Y);
    internal bool IsValid => new[] { TopLeft.X, TopLeft.Y, TopRight.X, TopRight.Y, BottomLeft.X, BottomLeft.Y }.All(double.IsFinite)
        && double.IsFinite(Width) && double.IsFinite(Height) && double.IsFinite(Determinant)
        && Width > 0.001 && Height > 0.001 && Math.Abs(Determinant) > 0.000001;
    private double Determinant => (TopRight.X - TopLeft.X) * (BottomLeft.Y - TopLeft.Y)
        - (BottomLeft.X - TopLeft.X) * (TopRight.Y - TopLeft.Y);
    internal PdfTextPoint ToLocal(PdfTextPoint p)
    {
        var x = p.X - TopLeft.X; var y = p.Y - TopLeft.Y;
        return new(((BottomLeft.Y - TopLeft.Y) * x - (BottomLeft.X - TopLeft.X) * y) / Determinant,
            (-(TopRight.Y - TopLeft.Y) * x + (TopRight.X - TopLeft.X) * y) / Determinant);
    }
}

[Flags]
internal enum PdfTextIssues
{
    None = 0, UnicodeMapError = 1, UnsupportedUnicode = 2, InvalidGeometry = 4,
    InvisibleOrUncertainPaint = 8,
}

/// <summary>Исходный индекс PDFium и Unicode сохраняются даже у поврежденного символа.
/// IsGenerated отмечает пробелы и переносы, добавленные PDFium, а не автором документа.</summary>
internal sealed record PdfTextCharacter(int Index, uint Unicode, PdfTextRect Bounds, bool IsGenerated,
    bool UnicodeMapError, int RenderMode, uint? FillAlpha, uint? StrokeAlpha, double FontSize,
    uint FillRgb = 0, uint StrokeRgb = 0)
{
    public string Text => Rune.IsValid((int)Unicode) ? new Rune((int)Unicode).ToString() : "\uFFFD";
    public bool IsWhitespace => Rune.IsValid((int)Unicode) && Rune.IsWhiteSpace(new Rune((int)Unicode));
    public PdfTextIssues Issues
    {
        get
        {
            var issues = UnicodeMapError ? PdfTextIssues.UnicodeMapError : PdfTextIssues.None;
            if (!Rune.IsValid((int)Unicode) || Unicode is 0 or 0xFFFD ||
                Rune.IsValid((int)Unicode) && Rune.GetUnicodeCategory(new Rune((int)Unicode)) is
                    UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Format ||
                Rune.IsValid((int)Unicode) && Rune.GetUnicodeCategory(new Rune((int)Unicode)) == UnicodeCategory.Control &&
                    !(IsGenerated && Unicode is 9 or 10 or 13))
                issues |= PdfTextIssues.UnsupportedUnicode;
            if (!IsWhitespace)
            {
                if (!Bounds.HasArea || !double.IsFinite(FontSize) || FontSize == 0) issues |= PdfTextIssues.InvalidGeometry;
                // Clipped, translucent and near-white text can be an invisible OCR layer.
                // A conservative refusal here leaves the normal raster OCR available.
                var visibleFill = FillAlpha == 255 && !NearWhite(FillRgb);
                var visibleStroke = StrokeAlpha == 255 && !NearWhite(StrokeRgb);
                var opaque = RenderMode switch { 0 => visibleFill, 1 => visibleStroke, 2 => visibleFill && visibleStroke, _ => false };
                if (!opaque) issues |= PdfTextIssues.InvisibleOrUncertainPaint;
            }
            return issues;
        }
    }
    private static bool NearWhite(uint rgb) => ((rgb >> 16) & 255) >= 245 && ((rgb >> 8) & 255) >= 245 && (rgb & 255) >= 245;
}

internal enum PdfTextStatus { Empty, Candidate, Untrusted }
internal sealed record PdfTextLine(string Text, IReadOnlyList<PdfTextCharacter> Characters);
internal sealed record PdfTextSelection(PdfTextStatus Status, string? Problem, IReadOnlyList<PdfTextLine> Lines,
    IReadOnlyList<PdfTextCharacter> Characters)
{
    public string Text => string.Join("\n", Lines.Select(l => l.Text));
    public bool CanUseCandidate => Status == PdfTextStatus.Candidate;
    // Neither Unicode nor geometry can prove that a plausible ToUnicode map describes
    // the rendered glyphs. A separate raster recognizer must always confirm the candidate.
    public bool RequiresIndependentValidation => true;
}

/// <summary>Управляемый снимок страницы, не содержащий нативных указателей.
/// Кандидат не считается достоверным текстом: даже корректная CMap может лгать.</summary>
internal sealed record PdfTextPage(int PageIndex, PageSize Size, IReadOnlyList<PdfTextCharacter> Characters, string? Problem = null)
{
    private const int MaxRegionCharacters = 4096;
    private const int MaxGeometryComparisons = 1_000_000;
    public PdfTextSelection Select(NormRect roi) => Select(PdfTextRegion.FromNormalized(roi, Size));

    public PdfTextSelection Select(PdfTextRegion region)
    {
        if (Problem is not null) return Refuse(Problem, []);
        if (!region.IsValid) return Refuse("некорректные координаты текстовой области PDF", []);
        // An unmapped visible glyph with no usable location cannot safely be ruled out of this ROI.
        var relevant = Characters.Where(c => !c.IsWhitespace || c.Issues != PdfTextIssues.None).ToArray();
        if (relevant.Any(c => !c.Bounds.HasArea))
            return Refuse("у символа PDF нет надежной геометрии", []);
        var mapped = relevant.Select(c =>
            new Located(c, PdfTextRect.Enclose(c.Bounds.Corners.Select(region.ToLocal)))).ToArray();
        var selected = mapped.Where(c => c.Local.Right > 0 && c.Local.X < 1 && c.Local.Bottom > 0 && c.Local.Y < 1).ToArray();
        if (selected.Length == 0) return new(PdfTextStatus.Empty, "в области нет текстового слоя", [], []);
        var chars = selected.Select(c => c.Character).OrderBy(c => c.Index).ToArray();
        if (selected.Length > MaxRegionCharacters) return Refuse("слишком много символов PDF в одной области", chars);
        var tolerance = Math.Min(0.001, 0.003 / Math.Min(region.Width, region.Height));
        if (selected.Any(c => c.Local.X < -tolerance || c.Local.Y < -tolerance || c.Local.Right > 1 + tolerance || c.Local.Bottom > 1 + tolerance))
            return Refuse("граница области обрезает символ PDF", chars);
        var issues = chars.Aggregate(PdfTextIssues.None, (a, c) => a | c.Issues);
        if (issues != PdfTextIssues.None) return Refuse("текстовый слой PDF не прошел проверку: " + issues, chars);

        // Cropping between complete glyphs must not silently turn a suffix of a word or
        // designation into the complete field. Generated whitespace remains a boundary.
        var indices = chars.Select(c => c.Index).ToHashSet();
        var sourceOrder = Characters.OrderBy(c => c.Index).ToArray();
        var run = new List<PdfTextCharacter>();
        foreach (var c in sourceOrder.Append(new(-1, 32, default, true, false, -1, null, null, 0)))
        {
            if (!c.IsWhitespace) { run.Add(c); continue; }
            if (run.Any(c => indices.Contains(c.Index)) && run.Any(c => !indices.Contains(c.Index)))
                return Refuse("область содержит только часть слова или обозначения PDF", chars);
            run.Clear();
        }

        // A rectangle can also clip between whole words or nearby complete lines.
        // Do not present that shortened label as a complete field. Distant table
        // columns are not joined; ambiguous close columns fall back to raster OCR.
        var comparisons = 0;
        var extent = PdfTextRect.Enclose(selected.SelectMany(c => c.Local.Corners));
        var maxSelectedHeight = selected.Max(c => c.Local.Height) * region.Height;
        foreach (var outside in mapped.Where(c => !indices.Contains(c.Character.Index)))
        {
            var maximumHeight = Math.Max(outside.Local.Height * region.Height, maxSelectedHeight);
            var horizontalGap = Math.Max(0, Math.Max(extent.X - outside.Local.Right, outside.Local.X - extent.Right)) * region.Width;
            var verticalGap = Math.Max(0, Math.Max(extent.Y - outside.Local.Bottom, outside.Local.Y - extent.Bottom)) * region.Height;
            if (horizontalGap > maximumHeight || verticalGap > maximumHeight) continue;
            foreach (var inside in selected)
            {
                if (++comparisons > MaxGeometryComparisons)
                    return Refuse("слишком сложная геометрия текстового слоя PDF", chars);
                var height = Math.Max(outside.Local.Height, inside.Local.Height) * region.Height;
                var xGap = Math.Max(0, Math.Max(inside.Local.X - outside.Local.Right, outside.Local.X - inside.Local.Right)) * region.Width;
                if ((outside.Local.Right <= 0 || outside.Local.X >= 1) &&
                    Math.Abs(outside.Local.Bottom - inside.Local.Bottom) * region.Height <= 0.4 * height && xGap < 0.75 * height)
                    return Refuse("область обрезает продолжение строки PDF", chars);
                var yGap = Math.Max(0, Math.Max(inside.Local.Y - outside.Local.Bottom, outside.Local.Y - inside.Local.Bottom)) * region.Height;
                if ((outside.Local.Bottom <= 0 || outside.Local.Y >= 1) && yGap < 0.75 * height && xGap < 0.75 * height)
                    return Refuse("область обрезает соседнюю строку PDF", chars);
            }
        }

        var byX = selected.OrderBy(c => c.Local.X).ToArray();
        for (var i = 0; i < byX.Length; i++)
        for (var j = i + 1; j < byX.Length && byX[j].Local.X < byX[i].Local.Right; j++)
        {
            if (++comparisons > MaxGeometryComparisons)
                return Refuse("слишком сложная геометрия текстового слоя PDF", chars);
            var a = byX[i].Local; var b = byX[j].Local;
            var area = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X)) *
                Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y));
            if (area > 0.65 * Math.Min(a.Width * a.Height, b.Width * b.Height) &&
                !IsAdjacentBaselinePunctuation(byX[i], byX[j]) && !IsAdjacentBaselinePunctuation(byX[j], byX[i]))
                return Refuse("символы текстового слоя PDF накладываются друг на друга", chars);
        }

        var groups = new List<TextLine>();
        foreach (var c in selected.OrderBy(c => c.Local.Bottom).ThenBy(c => c.Local.X))
        {
            TextLine? line = null;
            foreach (var group in groups)
            {
                if (++comparisons > MaxGeometryComparisons)
                    return Refuse("слишком сложная геометрия текстового слоя PDF", chars);
                if (Math.Abs(group.MeanBottom - c.Local.Bottom) > 0.4 * Math.Max(group.MaxHeight, c.Local.Height)) continue;
                line = group;
                break;
            }
            if (line is null) groups.Add(new(c)); else line.Add(c);
        }
        var orderedLines = groups.OrderBy(g => g.Top).Select(g => g.Characters.OrderBy(c => c.Local.X).ToArray()).ToArray();
        // PDFium normally exposes left-to-right text in source order. Reordering
        // independent objects must not silently discard their word boundaries.
        if (orderedLines.Any(line => line.Zip(line.Skip(1)).Any(pair => pair.First.Character.Index >= pair.Second.Character.Index)))
            return Refuse("порядок символов PDF не соответствует расположению строки", chars);
        var spaces = sourceOrder.Where(c => c.IsWhitespace && c.Unicode is not (10 or 13)).ToArray();
        var spaceIndices = spaces.Select(c => c.Index).ToArray();
        var lines = orderedLines.Select(g => BuildLine(g, spaces, spaceIndices)).ToArray();
        return new(PdfTextStatus.Candidate, null, Array.AsReadOnly(lines), Array.AsReadOnly(chars));
    }

    private static PdfTextLine BuildLine(Located[] ordered, PdfTextCharacter[] spaces, int[] spaceIndices)
    {
        var text = new StringBuilder();
        var all = new List<PdfTextCharacter>();
        for (var i = 0; i < ordered.Length; i++)
        {
            var c = ordered[i].Character;
            if (i > 0)
            {
                var previous = ordered[i - 1].Character;
                var start = Array.BinarySearch(spaceIndices, previous.Index + 1);
                if (start < 0) start = ~start;
                if (start < spaces.Length && spaces[start].Index < c.Index)
                {
                    text.Append(' ');
                    for (var j = start; j < spaces.Length && spaces[j].Index < c.Index; j++) all.Add(spaces[j]);
                }
            }
            text.Append(c.Text); all.Add(c);
        }
        return new(text.ToString(), all.AsReadOnly());
    }

    private static PdfTextSelection Refuse(string problem, IReadOnlyList<PdfTextCharacter> characters) =>
        new(PdfTextStatus.Untrusted, problem, [], Array.AsReadOnly(characters.ToArray()));
    // Italic glyph envelopes contain white corners: the ordinary following dot in
    // "С.468" can lie completely inside that envelope without touching its ink.
    // Preserve only this narrow, source-adjacent baseline punctuation case; two
    // full glyphs or duplicated dots still refuse. Raster confirmation is mandatory.
    private static bool IsAdjacentBaselinePunctuation(Located letter, Located punctuation)
    {
        var a = letter.Local; var b = punctuation.Local;
        return punctuation.Character.Unicode is '.' or ',' &&
            punctuation.Character.Index == letter.Character.Index + 1 &&
            Rune.IsValid((int)letter.Character.Unicode) && Rune.IsLetterOrDigit(new Rune((int)letter.Character.Unicode)) &&
            b.Height <= a.Height * 0.25 && b.Width <= a.Width * 0.25 &&
            b.X >= a.X + a.Width * 0.5 && Math.Abs(b.Bottom - a.Bottom) <= a.Height * 0.15;
    }
    private sealed record Located(PdfTextCharacter Character, PdfTextRect Local);
    private sealed class TextLine(Located first)
    {
        public List<Located> Characters { get; } = [first];
        private double _bottomSum = first.Local.Bottom;
        public double MeanBottom => _bottomSum / Characters.Count;
        public double MaxHeight { get; private set; } = first.Local.Height;
        public double Top { get; private set; } = first.Local.Y;
        public void Add(Located character)
        {
            Characters.Add(character);
            _bottomSum += character.Local.Bottom;
            MaxHeight = Math.Max(MaxHeight, character.Local.Height);
            Top = Math.Min(Top, character.Local.Y);
        }
    }
}
