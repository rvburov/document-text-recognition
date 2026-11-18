using System.Security.Cryptography;
using OpenCvSharp;
using SkiaSharp;

namespace DocumentTextRecognition.Tests.Samples;

/// <summary>Independent fixtures using the upstream Type A slanted outlines, never a system-font substitute.</summary>
internal static partial class GostTypeASamples
{
    internal const string FontFile = "GOST2.304-81TypeA-Slanted.ttf";
    internal const string FontSha256 = "e83689021a988a15736a70799aa166345499de548664079c58435d54d7cd973e";

    internal static string FontPath
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "tests", "Core.Tests", "Fonts", FontFile);
                if (File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException("Pinned GOST Type A font is required; font substitution is forbidden.", FontFile);
        }
    }

    private static SKTypeface OpenFont()
    {
        var path = FontPath;
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), FontSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The GOST Type A fixture font SHA256 differs from the pinned upstream release.");
        return SKTypeface.FromFile(path) ?? throw new InvalidDataException("The GOST Type A font could not be opened.");
    }

    private static SKPaint TextPaint(SKTypeface font, double capHeightMm, int dpi)
    {
        var paint = new SKPaint { Typeface = font, TextSize = 100, IsAntialias = true, Color = SKColors.Black };
        // Measure actual cap outlines; the old DejaVu-specific 0.72 ratio is not applicable.
        var bounds = new SKRect();
        paint.MeasureText("Н", ref bounds);
        if (bounds.Height <= 0) { paint.Dispose(); throw new InvalidDataException("Font has no Cyrillic cap-height glyph."); }
        paint.TextSize = (float)(100 * capHeightMm * dpi / 25.4 / bounds.Height);
        return paint;
    }

    internal static Mat RenderField(string text, double capHeightMm = 3.5, int dpi = 300)
    {
        using var font = OpenFont();
        using var paint = TextPaint(font, capHeightMm, dpi);
        var lines = text.Split('\n');
        var bounds = lines.Select(line => { var r = new SKRect(); paint.MeasureText(line, ref r); return r; }).ToArray();
        var padding = (float)(3 * dpi / 25.4);
        var step = (float)(capHeightMm * 1.8 * dpi / 25.4);
        var left = Math.Min(0, bounds.Min(r => r.Left));
        var right = bounds.Max(r => r.Right);
        var top = bounds.Min(r => r.Top);
        var bottom = bounds.Select((r, i) => r.Bottom + step * i).Max();
        var width = (int)Math.Ceiling(right - left + 2 * padding);
        var height = (int)Math.Ceiling(bottom - top + 2 * padding);
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(SKColors.White);
        for (var i = 0; i < lines.Length; i++)
            surface.Canvas.DrawText(lines[i], padding - left, padding - top + i * step, paint);
        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return Cv2.ImDecode(png.ToArray(), ImreadModes.Grayscale);
    }
}
