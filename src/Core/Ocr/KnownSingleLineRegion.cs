using OpenCvSharp;

namespace DocumentTextRecognition.Core.Ocr;

/// <summary>
/// Экспериментальная геометрическая проверка уже вырезанного поля. Она не распознает текст
/// и не доказывает, что ответ верен: прямое чтение дополнительно подтверждает второй движок.
/// Все компоненты, включая точки и скобки, остаются внутри возвращаемой области.
/// </summary>
internal static class KnownSingleLineRegion
{
    internal static unsafe bool TryGetBounds(Mat gray, out Rect expanded, CancellationToken ct = default)
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

        // Мелкая вторая строка не попадает в main и может сливаться с первой в проекции.
        // С обеих сторон учитываем ВСЕ компоненты, включая буквы высотой 4-5 px.
        // Сверху допускается одиночная короткая диакритика строго над одной буквой
        // (например, й), но не ряд мелких букв. Снизу допустим лишь след до 2x2 px.
        var above = pieces.Where(p => p.Box.Top + p.Box.Height / 2.0 < middle - height * .5).ToArray();
        if (above.Length > 1) return false;
        if (above.Length == 1)
        {
            var mark = above[0].Box;
            var isolatedPoint = mark.Width <= 2 && mark.Height <= 2;
            var singleAccent = mark.Height <= height * .25 && mark.Width <= Math.Ceiling(height * .5) &&
                main.Any(p => mark.Left >= p.Box.Left && mark.Right <= p.Box.Right &&
                    p.Box.Top >= mark.Bottom && p.Box.Top - mark.Bottom <= height * .25);
            if (!isolatedPoint && !singleAccent) return false;
        }
        var below = pieces.Where(p => p.Box.Top + p.Box.Height / 2.0 > middle + height * .5).ToArray();
        if (below.Length > 1 || below.Any(p => p.Box.Width > 2 || p.Box.Height > 2)) return false;

        // Небольшой разрыв допустим для точек/диакритики; две полосы текста не объединяем.
        var bands = 0;
        var lastInkRow = int.MinValue / 2;
        var inkData = (byte*)ink.Data;
        var inkStride = ink.Step();
        for (var y = bounds.Top; y < bounds.Bottom; y++)
        {
            if ((y & 127) == 0) ct.ThrowIfCancellationRequested();
            var row = new ReadOnlySpan<byte>(inkData + y * inkStride, ink.Width);
            if (row.IndexOfAnyExcept((byte)0) < 0) continue;
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
