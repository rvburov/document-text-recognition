using System.Security.Cryptography;
using System.Text.Json;
using SkiaSharp;

// The Python driver supplies immutable source text and checks pixels/hashes afterwards.
if (args.Length != 1) throw new ArgumentException("Usage: GostCorpus request.json");
var request = JsonSerializer.Deserialize<Request>(File.ReadAllText(args[0]))
    ?? throw new InvalidDataException("Missing render request.");
if (!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(request.FontFile)))
        .Equals(request.FontSha256, StringComparison.OrdinalIgnoreCase))
    throw new InvalidDataException("Pinned font checksum differs.");
using var typeface = SKTypeface.FromFile(request.FontFile)
    ?? throw new InvalidDataException("Pinned font failed to load.");
foreach (var item in request.Lines)
{
    using var paint = new SKPaint { Typeface = typeface, TextSize = 100, IsAntialias = true, Color = SKColors.Black };
    if (paint.GetGlyphs(item.Text).Any(g => g == 0)) throw new InvalidDataException("Missing source glyph: " + item.Id);
    var cap = new SKRect();
    paint.MeasureText("Н", ref cap);
    paint.TextSize = (float)(100 * item.CapHeightMm * item.Dpi / 25.4 / cap.Height);
    var bounds = new SKRect();
    paint.MeasureText(item.Text, ref bounds);
    var width = (int)Math.Ceiling(bounds.Width + 2 * item.Padding);
    var height = (int)Math.Ceiling(bounds.Height + 2 * item.Padding);
    using var surface = SKSurface.Create(new SKImageInfo(width, height))
        ?? throw new InvalidOperationException("Could not allocate fixture surface.");
    surface.Canvas.Clear(SKColors.White);
    surface.Canvas.DrawText(item.Text, item.Padding - bounds.Left, item.Padding - bounds.Top, paint);
    using var image = surface.Snapshot();
    using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
    using var file = File.Create(item.File);
    encoded.SaveTo(file);
}
Console.WriteLine($"Skia rendered {request.Lines.Count} lines with the pinned font.");

sealed record Request(string FontFile, string FontSha256, IReadOnlyList<Line> Lines);
sealed record Line(string Id, string Text, string File, int Dpi, double CapHeightMm, int Padding);
