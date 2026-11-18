using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DocumentTextRecognition.Tests.Samples;

/// <summary>
/// Independent vector PDF specimens. A pinned, licensed TrueType font is embedded;
/// CID-to-glyph and ToUnicode mappings are written explicitly, without a system-font
/// fallback or PDFsharp's process-wide font resolver. No OCR or PDF library builds them.
/// </summary>
internal static class PdfTextFixtures
{
    internal sealed record Line(string Text, double X = 40, double Y = 145,
        double FontSize = 20, int RenderMode = 0, bool White = false);

    internal readonly record struct Box(double Left, double Bottom, double Right, double Top);

    internal sealed record Page(IReadOnlyList<Line> Lines, double Width = 320, double Height = 220,
        int Rotation = 0, Box? Crop = null, bool RegistrationGrid = false);

    internal static string Create(string folder, string stem, IReadOnlyList<Page> pages,
        bool includeToUnicode = true, IReadOnlyDictionary<char, string>? unicodeOverrides = null,
        string? fontFile = null)
    {
        if (pages.Count == 0) throw new ArgumentException("At least one fixture page is required.", nameof(pages));
        var fontBytes = File.ReadAllBytes(fontFile ?? GostTypeASamples.FontPath);
        if (fontFile is null && !string.Equals(Convert.ToHexString(SHA256.HashData(fontBytes)), GostTypeASamples.FontSha256,
                StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The pinned fixture font changed.");
        var font = new Font(fontBytes);
        var alphabet = pages.SelectMany(p => p.Lines).SelectMany(l => l.Text).Distinct().Order().ToArray();
        if (alphabet.Any(char.IsSurrogate)) throw new ArgumentException("Fixture source glyphs must be BMP characters.");
        var cids = alphabet.Select((c, i) => (c, cid: (ushort)(i + 1))).ToDictionary(x => x.c, x => x.cid);
        var map = new byte[(alphabet.Length + 1) * 2];
        var widths = new StringBuilder();
        foreach (var c in alphabet)
        {
            var glyph = font.Glyph(c);
            if (glyph == 0) throw new InvalidDataException($"Pinned font has no glyph U+{(int)c:X4}.");
            BinaryPrimitives.WriteUInt16BigEndian(map.AsSpan(cids[c] * 2), glyph);
            widths.Append(cids[c]).Append(" [").Append(Number(font.Advance(glyph))).Append("] ");
        }

        var objects = new List<byte[]>();
        int Add(string body) { objects.Add(Encoding.ASCII.GetBytes(body)); return objects.Count; }
        int Stream(byte[] bytes, string attributes = "")
        {
            var start = Encoding.ASCII.GetBytes($"<< /Length {bytes.Length} {attributes} >>\nstream\n");
            var end = Encoding.ASCII.GetBytes("\nendstream");
            objects.Add([.. start, .. bytes, .. end]);
            return objects.Count;
        }
        var catalog = Add("");
        var pageTree = Add("");
        var fontObject = Add("");
        var descendant = Add("");
        var descriptor = Add("");
        var fontFileObject = Stream(fontBytes, $"/Length1 {fontBytes.Length}");
        var cidMap = Stream(map);
        var unicode = includeToUnicode ? Stream(Encoding.ASCII.GetBytes(ToUnicode(alphabet, cids, unicodeOverrides))) : 0;
        objects[fontObject - 1] = Encoding.ASCII.GetBytes($"<< /Type /Font /Subtype /Type0 /BaseFont /DTR-Vector-Fixture /Encoding /Identity-H /DescendantFonts [{descendant} 0 R]" +
            (includeToUnicode ? $" /ToUnicode {unicode} 0 R" : "") + " >>");
        objects[descendant - 1] = Encoding.ASCII.GetBytes($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /DTR-Vector-Fixture /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor {descriptor} 0 R /DW 1000 /W [{widths}] /CIDToGIDMap {cidMap} 0 R >>");
        objects[descriptor - 1] = Encoding.ASCII.GetBytes($"<< /Type /FontDescriptor /FontName /DTR-Vector-Fixture /Flags 32 /FontBBox [{font.Bounds}] /ItalicAngle {Number(font.ItalicAngle)} /Ascent {Number(font.Ascent)} /Descent {Number(font.Descent)} /CapHeight {Number(font.Ascent)} /StemV 45 /FontFile2 {fontFileObject} 0 R >>");

        var pageIds = new List<int>();
        foreach (var page in pages)
        {
            if (page.Rotation is not (0 or 90 or 180 or 270)) throw new ArgumentOutOfRangeException(nameof(pages));
            var commands = new StringBuilder();
            if (page.RegistrationGrid) AddRegistrationGrid(commands, page);
            foreach (var line in page.Lines)
            {
                commands.Append("q ").Append(line.White ? "1" : "0").Append(" g BT /F0 ")
                    .Append(Number(line.FontSize)).Append(" Tf ").Append(line.RenderMode)
                    .Append(" Tr 1 0 0 1 ").Append(Number(line.X)).Append(' ').Append(Number(line.Y))
                    .Append(" Tm <");
                foreach (var c in line.Text) commands.Append(cids[c].ToString("X4", CultureInfo.InvariantCulture));
                commands.Append("> Tj ET Q\n");
            }
            var content = Stream(Encoding.ASCII.GetBytes(commands.ToString()));
            var crop = page.Crop is { } b
                ? $" /CropBox [{Number(b.Left)} {Number(b.Bottom)} {Number(b.Right)} {Number(b.Top)}]" : "";
            pageIds.Add(Add($"<< /Type /Page /Parent {pageTree} 0 R /MediaBox [0 0 {Number(page.Width)} {Number(page.Height)}]{crop} /Rotate {page.Rotation} /Resources << /Font << /F0 {fontObject} 0 R >> >> /Contents {content} 0 R >>"));
        }
        objects[catalog - 1] = Encoding.ASCII.GetBytes($"<< /Type /Catalog /Pages {pageTree} 0 R >>");
        objects[pageTree - 1] = Encoding.ASCII.GetBytes($"<< /Type /Pages /Count {pageIds.Count} /Kids [{string.Join(" ", pageIds.Select(i => $"{i} 0 R"))}] >>");

        using var pdf = new MemoryStream();
        void Write(string value) => pdf.Write(Encoding.ASCII.GetBytes(value));
        Write("%PDF-1.7\n% vector Unicode fixture\n");
        var offsets = new List<long> { 0 };
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Position);
            Write($"{i + 1} 0 obj\n");
            pdf.Write(objects[i]);
            Write("\nendobj\n");
        }
        var xref = pdf.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) Write(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root {catalog} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, stem + ".pdf");
        File.WriteAllBytes(path, pdf.ToArray());
        return path;
    }

    private static string ToUnicode(char[] alphabet, Dictionary<char, ushort> cids, IReadOnlyDictionary<char, string>? overrides)
    {
        var text = new StringBuilder("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /DTR-Unicode def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        foreach (var group in alphabet.Chunk(80))
        {
            text.Append(group.Length).Append(" beginbfchar\n");
            foreach (var c in group)
            {
                var mapped = overrides is not null && overrides.TryGetValue(c, out var replacement) ? replacement : c.ToString();
                text.Append('<').Append(cids[c].ToString("X4", CultureInfo.InvariantCulture)).Append("> <")
                    .Append(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(mapped))).Append(">\n");
            }
            text.Append("endbfchar\n");
        }
        return text.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n").ToString();
    }

    private static string Number(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);

    // Shared geometry only: no expected field values or OCR answers appear here. The
    // asymmetric little shapes give ORB unambiguous anchors after quarter-page turns.
    // Used by A4 Reader fixtures; the original small text-only specimens are unchanged.
    private static void AddRegistrationGrid(StringBuilder commands, Page page)
    {
        commands.Append("q 0 G 0.7 w 25 25 ").Append(Number(page.Width - 50)).Append(' ')
            .Append(Number(page.Height - 50)).Append(" re S\n");
        commands.Append("210 25 m 210 200 l 570 200 l S\n");
        foreach (var y in new[] { 30, 80, 130, 180 })
            commands.Append("25 ").Append(y).Append(" m 570 ").Append(y).Append(" l S\n");
        for (var row = 0; row < 12; row++)
        for (var col = 0; col < 8; col++)
        {
            var x = 38 + col * 65;
            var y = 240 + row * 45;
            var width = 12 + (row * 7 + col * 11) % 29;
            var height = 8 + (row * 13 + col * 3) % 23;
            commands.Append(x).Append(' ').Append(y).Append(' ').Append(width).Append(' ').Append(height)
                .Append(" re S\n").Append(x).Append(' ').Append(y).Append(" m ")
                .Append(x + width).Append(' ').Append(y + height).Append(" l S\n");
            if ((row + 2 * col) % 3 == 0)
                commands.Append(x + 3).Append(' ').Append(y + 3).Append(" 3 3 re f\n");
        }
        for (var row = 0; row < 5; row++)
        for (var col = 0; col < 5; col++)
            commands.Append(35 + col * 32).Append(' ').Append(40 + row * 30).Append(' ')
                .Append(7 + row * 2 + col).Append(" 9 re S\n");
        commands.Append("Q\n");
    }

    /// <summary>Only the SFNT tables needed for this pinned fixture font. No native calls.</summary>
    private sealed class Font
    {
        private readonly byte[] _bytes;
        private readonly Dictionary<string, int> _tables = [];
        private readonly int _cmap;
        private readonly int _metrics;
        private readonly int _units;
        internal double Ascent { get; }
        internal double Descent { get; }
        internal string Bounds { get; }
        internal double ItalicAngle { get; }

        internal Font(byte[] bytes)
        {
            _bytes = bytes;
            var count = U16(4);
            for (var i = 0; i < count; i++)
            {
                var offset = 12 + i * 16;
                _tables[Encoding.ASCII.GetString(bytes, offset, 4)] = checked((int)U32(offset + 8));
            }
            var head = _tables["head"];
            var hhea = _tables["hhea"];
            _units = U16(head + 18);
            _metrics = U16(hhea + 34);
            Ascent = Scale(I16(hhea + 4));
            Descent = Scale(I16(hhea + 6));
            ItalicAngle = _tables.TryGetValue("post", out var post)
                ? BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(post + 4, 4)) / 65536d : 0;
            Bounds = string.Join(" ", Enumerable.Range(0, 4).Select(i => Number(Scale(I16(head + 36 + 2 * i)))));
            var cmap = _tables["cmap"];
            for (var i = 0; i < U16(cmap + 2); i++)
            {
                var record = cmap + 4 + i * 8;
                var candidate = cmap + checked((int)U32(record + 4));
                if (U16(candidate) == 4 && (U16(record) == 0 || U16(record) == 3 && U16(record + 2) == 1))
                    _cmap = candidate;
            }
            if (_cmap == 0) throw new InvalidDataException("Pinned fixture font has no Unicode cmap format 4.");
        }

        internal ushort Glyph(char value)
        {
            var segments = U16(_cmap + 6) / 2;
            var ends = _cmap + 14;
            var starts = ends + segments * 2 + 2;
            var deltas = starts + segments * 2;
            var ranges = deltas + segments * 2;
            for (var i = 0; i < segments; i++)
            {
                if (value > U16(ends + i * 2)) continue;
                var start = U16(starts + i * 2);
                if (value < start) return 0;
                var delta = I16(deltas + i * 2);
                var range = U16(ranges + i * 2);
                if (range == 0) return unchecked((ushort)(value + delta));
                var glyph = U16(ranges + i * 2 + range + 2 * (value - start));
                return glyph == 0 ? (ushort)0 : unchecked((ushort)(glyph + delta));
            }
            return 0;
        }

        internal double Advance(ushort glyph) => Scale(U16(_tables["hmtx"] + 4 * Math.Min(glyph, _metrics - 1)));
        private double Scale(int value) => value * 1000d / _units;
        private ushort U16(int offset) => BinaryPrimitives.ReadUInt16BigEndian(_bytes.AsSpan(offset, 2));
        private short I16(int offset) => BinaryPrimitives.ReadInt16BigEndian(_bytes.AsSpan(offset, 2));
        private uint U32(int offset) => BinaryPrimitives.ReadUInt32BigEndian(_bytes.AsSpan(offset, 4));
    }
}
