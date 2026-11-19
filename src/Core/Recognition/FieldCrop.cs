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
        var t = match.Template.Features;
        var m = match.Transform;

        // Обратное преобразование: точка шаблона → точка картинки страницы.
        var det = m[0] * m[4] - m[1] * m[3];
        double ia = m[4] / det, ib = -m[1] / det, id = -m[3] / det, ie = m[0] / det;
        double ic = -(ia * m[2] + ib * m[5]), iff = -(id * m[2] + ie * m[5]);

        var rect = field.ToPixels(t.Width, t.Height);
        var margin = MarginMm / 25.4 * 72 * t.Scale;
        double fx = rect.X - margin, fy = rect.Y - margin, fw = rect.Width + 2 * margin, fh = rect.Height + 2 * margin;

        var scaleT = Math.Sqrt(m[0] * m[0] + m[3] * m[3]);    // точек шаблона на точку страницы
        var r = OcrDpi / 72.0;                                  // точек результата на пункт PDF
        var k = r / (scaleT * page.Scale);                      // точек результата на точку шаблона
        if (fw * k > MaxSide || fh * k > MaxSide) k = MaxSide / Math.Max(fw, fh);
        var outW = Math.Max(8, (int)Math.Round(fw * k));
        var outH = Math.Max(8, (int)Math.Round(fh * k));

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

        using var upright = new Mat();
        Cv2.WarpAffine(region, upright, map, new Size(outW, outH),
            InterpolationFlags.Linear | InterpolationFlags.WarpInverseMap, BorderTypes.Constant, Scalar.All(255));
        started = timing is null ? 0 : Stopwatch.GetTimestamp();
        var prepared = Prepare(upright);
        timing?.Invoke("crop.prepare", Stopwatch.GetElapsedTime(started));
        return prepared;
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
