using System.Diagnostics;
using OpenCvSharp;
using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Matching;
using DocumentTextRecognition.Core.Pdf;

namespace DocumentTextRecognition.Core.Recognition;

/// <summary>
/// Вырезание области поля со страницы по совмещению с шаблоном и подготовка к OCR.
///
/// Область шаблона (в долях страницы) переводится в координаты страницы обратным
/// преобразованием совмещения, этот фрагмент страницы рисуется заново в
/// <see cref="OcrDpi"/> (а не вырезается из уменьшенной картинки сопоставления) и
/// поворачивается так, как стоит в шаблоне. Затем убираются длинные линии граф, выравнивается
/// фон и добавляются белые поля — так текст читают оба движка.
/// </summary>
internal static class FieldCrop
{
    /// <summary>Разрешение области для распознавания, точек на дюйм страницы.</summary>
    public const int OcrDpi = 300;

    /// <summary>Запас вокруг рамки шаблона, мм: совмещение точно до долей миллиметра не бывает.</summary>
    public const double MarginMm = 1.0;

    /// <summary>Наибольшая сторона картинки области, точек.</summary>
    private const int MaxSide = 5000;

    public static Mat Extract(PdfDocument pdf, int pageIndex, PageSize pageSize, PageFeatures page, PageMatch match, NormRect field,
        Action<string, TimeSpan>? timing = null)
    {
        using var upright = RenderUpright(pdf, pageIndex, pageSize, page, match, field, MarginMm, timing, out _);
        var started = timing is null ? 0 : Stopwatch.GetTimestamp();
        var prepared = Prepare(upright);
        timing?.Invoke("crop.prepare", Stopwatch.GetElapsedTime(started));
        return prepared;
    }

    /// <summary>Положение области на картинке <see cref="RenderUpright"/>: точка шаблона → точка картинки.</summary>
    private readonly record struct UprightMap(double X, double Y, double Scale)
    {
        public Rect ToPixels(NormRect r, double templateWidth, double templateHeight) => new(
            (int)Math.Floor((r.X * templateWidth - X) * Scale), (int)Math.Floor((r.Y * templateHeight - Y) * Scale),
            (int)Math.Ceiling(r.Width * templateWidth * Scale), (int)Math.Ceiling(r.Height * templateHeight * Scale));
    }

    /// <summary>Область шаблона с запасом <paramref name="marginMm"/>, нарисованная заново в
    /// <see cref="OcrDpi"/> и повернутая так, как стоит в шаблоне. BGR, без обработки.</summary>
    private static Mat RenderUpright(PdfDocument pdf, int pageIndex, PageSize pageSize, PageFeatures page, PageMatch match,
        NormRect field, double marginMm, Action<string, TimeSpan>? timing, out UprightMap placement)
    {
        var t = match.Template.Features;
        var m = match.Transform;

        // Обратное преобразование: точка шаблона → точка картинки страницы.
        var det = m[0] * m[4] - m[1] * m[3];
        double ia = m[4] / det, ib = -m[1] / det, id = -m[3] / det, ie = m[0] / det;
        double ic = -(ia * m[2] + ib * m[5]), iff = -(id * m[2] + ie * m[5]);

        var rect = field.ToPixels(t.Width, t.Height);
        var margin = marginMm / 25.4 * 72 * t.Scale;
        double fx = rect.X - margin, fy = rect.Y - margin, fw = rect.Width + 2 * margin, fh = rect.Height + 2 * margin;

        var scaleT = Math.Sqrt(m[0] * m[0] + m[3] * m[3]);    // точек шаблона на точку страницы
        var r = OcrDpi / 72.0;                                  // точек результата на пункт PDF
        var k = r / (scaleT * page.Scale);                      // точек результата на точку шаблона
        if (fw * k > MaxSide || fh * k > MaxSide) k = MaxSide / Math.Max(fw, fh);
        var outW = Math.Max(8, (int)Math.Round(fw * k));
        var outH = Math.Max(8, (int)Math.Round(fh * k));
        placement = new UprightMap(fx, fy, k);

        // Углы области в пунктах страницы → охватывающий прямоугольник для отрисовки.
        (double X, double Y) ToPoints(double tx, double ty) =>
            ((ia * tx + ib * ty + ic) / page.Scale, (id * tx + ie * ty + iff) / page.Scale);
        var corners = new[] { ToPoints(fx, fy), ToPoints(fx + fw, fy), ToPoints(fx, fy + fh), ToPoints(fx + fw, fy + fh) };
        var rx = Math.Max(0, corners.Min(c => c.X) - 2);
        var ry = Math.Max(0, corners.Min(c => c.Y) - 2);
        var rr = Math.Min(pageSize.Width, corners.Max(c => c.X) + 2);
        var rb = Math.Min(pageSize.Height, corners.Max(c => c.Y) + 2);
        if (rr - rx < 1 || rb - ry < 1) throw new InvalidOperationException("область поля вне страницы");

        var started = timing is null ? 0 : Stopwatch.GetTimestamp();
        using var region = pdf.RenderRegion(pageIndex, r, rx, ry, rr - rx, rb - ry);
        timing?.Invoke("crop.render", Stopwatch.GetElapsedTime(started));

        // Точка результата (ox, oy) → точка шаблона → точка страницы → пункты → точка фрагмента.
        var q = r / (page.Scale * k);
        using var map = new Mat(2, 3, MatType.CV_64FC1);
        map.Set(0, 0, q * ia);
        map.Set(0, 1, q * ib);
        map.Set(0, 2, r / page.Scale * (ia * fx + ib * fy + ic) - r * rx);
        map.Set(1, 0, q * id);
        map.Set(1, 1, q * ie);
        map.Set(1, 2, r / page.Scale * (id * fx + ie * fy + iff) - r * ry);

        var upright = new Mat();
        Cv2.WarpAffine(region, upright, map, new Size(outW, outH),
            InterpolationFlags.Linear | InterpolationFlags.WarpInverseMap, BorderTypes.Constant, Scalar.All(255));
        return upright;
    }

    /// <summary>Ширина полосы вокруг вырезки, в которой ищется продолжение текста, мм.</summary>
    public const double ClipProbeMm = 4.0;

    /// <summary>
    /// Не обрезает ли область текст поля. Оба движка читают одну и ту же вырезку, поэтому
    /// буква, оставшаяся за краем области, дает согласованное, но неверное значение
    /// («Плата индикаци», первая строка наименования без второй). Вокруг вырезки рисуется
    /// полоса <see cref="ClipProbeMm"/>: если в ней есть знак, перерезанный краем, или
    /// продолжение той же строки (следующая строка) на расстоянии межбуквенного (межстрочного)
    /// промежутка и их не разделяет линия графы, текст выходит за область. Текст других
    /// областей шаблона (тип под наименованием) не учитывается. null — текст целиком в области.
    /// </summary>
    public static string? ClippedText(PdfDocument pdf, int pageIndex, PageSize pageSize, PageFeatures page, PageMatch match,
        NormRect field, IEnumerable<NormRect> otherFields)
    {
        var t = match.Template.Features;
        using var upright = RenderUpright(pdf, pageIndex, pageSize, page, match, field, MarginMm + ClipProbeMm, null, out var placement);
        using var gray = PageImage.ToGray(upright);
        using var background = new Mat();
        using (var k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(31, 31)))
            Cv2.Dilate(gray, background, k);
        using var flat = new Mat();
        Cv2.Divide(gray, background, flat, 255);
        using var ink = PageImage.InkMask(flat);
        var pxPerMm = placement.Scale * t.Scale * 72 / 25.4;
        var minLine = Math.Max((int)(7 * pxPerMm), (int)Math.Ceiling(1.3 * TextHeight(ink)));
        using var lines = PageImage.LineMask(ink, minLine);
        using var removed = new Mat();
        using (var k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5)))
            Cv2.Dilate(lines, removed, k);
        AddLineStubs(ink, removed);
        using var text = new Mat();
        using (var notRemoved = new Mat())
        {
            Cv2.BitwiseNot(removed, notRemoved);
            Cv2.BitwiseAnd(ink, notRemoved, text);
        }
        var bounds = new Rect(0, 0, text.Width, text.Height);
        foreach (var other in otherFields)
        {
            var o = placement.ToPixels(other, t.Width, t.Height).Intersect(bounds);
            if (o.Width > 0 && o.Height > 0) text[o].SetTo(Scalar.All(0));
        }

        var probe = (int)Math.Round(ClipProbeMm * pxPerMm);
        var inner = new Rect(probe, probe, text.Width - 2 * probe, text.Height - 2 * probe);
        if (inner.Width <= 0 || inner.Height <= 0) return null;
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(text, labels, stats, centroids, PixelConnectivity.Connectivity8);
        var speck = 0.3 * pxPerMm;
        var inside = new List<Rect>();
        var outside = new List<Rect>();
        for (var i = 1; i < count; i++)
        {
            var b = new Rect(stats.At<int>(i, (int)ConnectedComponentsTypes.Left), stats.At<int>(i, (int)ConnectedComponentsTypes.Top),
                stats.At<int>(i, (int)ConnectedComponentsTypes.Width), stats.At<int>(i, (int)ConnectedComponentsTypes.Height));
            if (b.Width < speck && b.Height < speck) continue;
            var within = b.X >= inner.X && b.Y >= inner.Y && b.Right <= inner.Right && b.Bottom <= inner.Bottom;
            var touches = b.IntersectsWith(inner);
            // Знак, перерезанный краем вырезки (поле + 1 мм): OCR видит его обрубок.
            if (touches && !within && b.Height >= 1.0 * pxPerMm) return "текст выходит за область поля или в нее попал край соседнего текста";
            if (within) inside.Add(b);
            else if (!touches) outside.Add(b);
        }
        if (inside.Count == 0) return null;
        var height = inside.Select(b => b.Height).OrderBy(h => h).ElementAt(inside.Count / 2);
        foreach (var o in outside)
        foreach (var b in inside)
        {
            var verticalOverlap = Math.Min(o.Bottom, b.Bottom) - Math.Max(o.Y, b.Y);
            var horizontalOverlap = Math.Min(o.Right, b.Right) - Math.Max(o.X, b.X);
            var sameLine = verticalOverlap >= 0.5 * Math.Min(o.Height, b.Height);
            var gapX = Math.Max(o.X - b.Right, b.X - o.Right);
            var gapY = Math.Max(o.Y - b.Bottom, b.Y - o.Bottom);
            var continues = sameLine && gapX >= 0 && gapX <= 0.9 * height
                || horizontalOverlap > 0 && gapY >= 0 && gapY <= 0.8 * height && o.Height >= 0.5 * height;
            if (!continues) continue;
            // Линия графы между ними: это соседняя графа, а не продолжение.
            var between = sameLine
                ? new Rect(Math.Min(o.Right, b.Right), Math.Max(o.Y, b.Y), Math.Max(1, Math.Abs(gapX)), Math.Max(1, verticalOverlap))
                : new Rect(Math.Max(o.X, b.X), Math.Min(o.Bottom, b.Bottom), Math.Max(1, horizontalOverlap), Math.Max(1, Math.Abs(gapY)));
            between = between.Intersect(bounds);
            if (between.Width > 0 && between.Height > 0 && Cv2.CountNonZero(lines[between]) > 0) continue;
            return "текст выходит за область поля";
        }
        return null;
    }

    /// <summary>
    /// Подготовка области к OCR: оттенки серого, выравнивание фона (деление на размытый фон),
    /// линии граф длиннее 7 мм — в белый, белые поля по краям.
    /// </summary>
    public static Mat Prepare(Mat bgr)
    {
        using var gray = PageImage.ToGray(bgr);

        // Фон: максимум в окне крупнее штриха буквы — текст исчезает, остается бумага.
        using var background = new Mat();
        using (var k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(31, 31)))
            Cv2.Dilate(gray, background, k);
        using var flat = new Mat();
        Cv2.Divide(gray, background, flat, 255);

        using var ink = PageImage.InkMask(flat);
        // Линия графы длиннее и 7 мм, и самой высокой буквы: крупный шрифт обозначения
        // (7-10 мм по ГОСТ 2.304) не теряет вертикальные штрихи Б, В, Г, П, 1.
        var minLine = Math.Max((int)(7 / 25.4 * OcrDpi), (int)Math.Ceiling(1.3 * TextHeight(ink)));
        using var lines = PageImage.LineMask(ink, minLine);
        using var grown = new Mat();
        using (var k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5)))
            Cv2.Dilate(lines, grown, k);
        AddLineStubs(ink, grown);
        flat.SetTo(new Scalar(255), grown);

        var padded = new Mat();
        Cv2.CopyMakeBorder(flat, padded, 24, 24, 24, 24, BorderTypes.Constant, Scalar.All(255));
        return padded;
    }

    /// <summary>Высота крупных знаков области, точек: по связным пятнам, похожим на буквы
    /// (не линии во всю область и не точки). 0 — знаков нет.</summary>
    internal static double TextHeight(Mat ink)
    {
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(ink, labels, stats, centroids, PixelConnectivity.Connectivity8);
        var minSize = 1.2 / 25.4 * OcrDpi;
        var heights = new List<int>();
        for (var i = 1; i < count; i++)
        {
            var w = stats.At<int>(i, (int)ConnectedComponentsTypes.Width);
            var h = stats.At<int>(i, (int)ConnectedComponentsTypes.Height);
            if (h < minSize || h > 0.85 * ink.Height || w > 0.85 * ink.Width) continue;
            if (w * 6 < h && h > 2 * (7 / 25.4 * OcrDpi)) continue; // отрезок линии, а не «1»
            heights.Add(h);
        }
        if (heights.Count == 0) return 0;
        heights.Sort();
        return heights[(int)(0.8 * (heights.Count - 1))];
    }

    /// <summary>
    /// Обрезки линий соседних граф (область чуть шире графы): тонкий горизонтальный штрих,
    /// который касается левого или правого края области или примыкал к удаленной линии.
    /// Иначе движки читают их как «-», «—», «т». Добавляются в маску удаляемого.
    /// </summary>
    private static void AddLineStubs(Mat ink, Mat removed)
    {
        using var residual = new Mat();
        using (var notRemoved = new Mat())
        {
            Cv2.BitwiseNot(removed, notRemoved);
            Cv2.BitwiseAnd(ink, notRemoved, residual);
        }
        using var near = new Mat();
        using (var k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3)))
            Cv2.Dilate(removed, near, k);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(residual, labels, stats, centroids, PixelConnectivity.Connectivity8);
        var thin = 0.7 / 25.4 * OcrDpi;
        for (var i = 1; i < count; i++)
        {
            var x = stats.At<int>(i, (int)ConnectedComponentsTypes.Left);
            var w = stats.At<int>(i, (int)ConnectedComponentsTypes.Width);
            var h = stats.At<int>(i, (int)ConnectedComponentsTypes.Height);
            // Только горизонтальные обрезки: вертикальный тонкий штрих у края может быть «1».
            if (h > thin || w < 2 * h) continue;
            using var component = new Mat();
            Cv2.Compare(labels, i, component, CmpTypes.EQ);
            var atEdge = x == 0 || x + w >= ink.Width;
            if (!atEdge)
            {
                using var touch = new Mat();
                Cv2.BitwiseAnd(component, near, touch);
                if (Cv2.CountNonZero(touch) == 0) continue;
            }
            // С тем же запасом, что и линии: иначе остается серый ореол сглаживания.
            using (var k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5)))
                Cv2.Dilate(component, component, k);
            removed.SetTo(new Scalar(255), component);
        }
    }
}
