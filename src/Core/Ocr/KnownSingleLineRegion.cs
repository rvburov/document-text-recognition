using OpenCvSharp;

namespace DocumentTextRecognition.Core.Ocr;

/// <summary>
/// Экспериментальная геометрическая проверка уже вырезанного поля. Она не распознает текст
/// и не доказывает, что ответ верен: прямое чтение дополнительно подтверждает второй движок.
/// Все компоненты, включая точки и скобки, остаются внутри возвращаемой области.
/// </summary>
internal static class KnownSingleLineRegion
{
    internal static bool TryGetBounds(Mat gray, out Rect expanded, CancellationToken ct = default)
    {
        expanded = default;
        ct.ThrowIfCancellationRequested();
        if (gray.Empty() || gray.Type() != MatType.CV_8UC1) return false;
        using var ink = new Mat();
        Cv2.Threshold(gray, ink, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centers = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(ink, labels, stats, centers,
            PixelConnectivity.Connectivity8, MatType.CV_32S);
        ct.ThrowIfCancellationRequested();
        var pieces = new List<(Rect Box, int Area)>();
        for (var i = 1; i < count; i++)
            pieces.Add((new Rect(stats.At<int>(i, 0), stats.At<int>(i, 1),
                stats.At<int>(i, 2), stats.At<int>(i, 3)), stats.At<int>(i, 4)));
        if (pieces.Count == 0) return false;

        var bounds = Rect.FromLTRB(pieces.Min(p => p.Box.Left), pieces.Min(p => p.Box.Top),
            pieces.Max(p => p.Box.Right), pieces.Max(p => p.Box.Bottom));
        var glyphs = pieces.Where(p => p.Box.Height >= 6 && p.Box.Width >= 2 && p.Area >= 8).ToArray();
        if (glyphs.Length < 3) return false;
        var heights = glyphs.Select(p => p.Box.Height).Order().ToArray();
        var height = (double)heights[(int)((heights.Length - 1) * .75)];
        var main = glyphs.Where(p => p.Box.Height >= height * .55).ToArray();
        if (main.Length < 3 || bounds.Width < height * 1.5 || bounds.Height > height * 1.7) return false;
        var middle = main.Select(p => p.Box.Top + p.Box.Height / 2.0).Order().ElementAt(main.Length / 2);
        if (main.Any(p => Math.Abs(p.Box.Top + p.Box.Height / 2.0 - middle) > height * .45)) return false;

        // Небольшой разрыв допустим для точек/диакритики; две полосы текста не объединяем.
        var bands = 0;
        var lastInkRow = int.MinValue / 2;
        for (var y = bounds.Top; y < bounds.Bottom; y++)
        {
            if ((y & 127) == 0) ct.ThrowIfCancellationRequested();
            using var row = ink.Row(y);
            if (Cv2.CountNonZero(row) == 0) continue;
            if (y - lastInkRow - 1 > Math.Max(2, (int)(height * .25))) bands++;
            lastInkRow = y;
        }
        if (bands != 1) return false;
        var density = pieces.Sum(p => (double)p.Area) / ((double)bounds.Width * bounds.Height);
        if (density < .025 || density > .65) return false;

        // Формула расширения DB при штатном UnclipRatio=2.0. Тесная рамка без расширения
        // увеличивает нормализованную ширину тензора и может ухудшить чтение пунктуации.
        var distance = (int)Math.Round(PaddleEngine.UnclipRatio * bounds.Width * (double)bounds.Height /
            (2.0 * (bounds.Width + bounds.Height)));
        expanded = Rect.FromLTRB(Math.Max(0, bounds.Left - distance), Math.Max(0, bounds.Top - distance),
            Math.Min(gray.Width, bounds.Right + distance), Math.Min(gray.Height, bounds.Bottom + distance));
        return true;
    }
}
