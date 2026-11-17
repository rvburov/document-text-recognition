using OpenCvSharp;

namespace DocumentTextRecognition.Core.Imaging;

/// <summary>Назначение области шаблона — по цвету рамки.</summary>
internal enum FieldRole
{
    /// <summary>Синяя рамка: десятичное обозначение.</summary>
    Designation,
    /// <summary>Зеленая рамка: наименование.</summary>
    Name,
    /// <summary>Желтая рамка: тип документа.</summary>
    Type,
    /// <summary>Красная рамка: обозначение, наименование и тип документа вместе.</summary>
    Combined,
}

/// <summary>Прямоугольник в долях страницы (0..1) — не зависит от разрешения картинки.</summary>
internal readonly record struct NormRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public static NormRect FromPixels(Rect r, int imageWidth, int imageHeight) =>
        new(r.X / (double)imageWidth, r.Y / (double)imageHeight, r.Width / (double)imageWidth, r.Height / (double)imageHeight);

    public Rect2d ToPixels(double imageWidth, double imageHeight) =>
        new(X * imageWidth, Y * imageHeight, Width * imageWidth, Height * imageHeight);

    public bool Intersects(NormRect o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;
}

/// <summary>Найденная рамка: назначение, внутренняя область (без линии рамки) и толщина линии.</summary>
internal sealed record ColorFrame(FieldRole Role, NormRect Inner, Rect Outer, int Stroke);

/// <summary>Итог разбора рамок шаблона: рамки либо понятная причина, почему шаблон не годится.</summary>
internal sealed record FrameLayout(IReadOnlyList<ColorFrame> Frames, string? Problem)
{
    public bool IsValid => Problem is null;
}

/// <summary>
/// Поиск цветных рамок на странице шаблона.
///
/// Цвет определяется по тону (H в HSV), а не по точному RGB: рамка из PDF-редактора, рамка
/// после печати и сканирования и рамка в JPEG дают разные оттенки одного цвета. Диапазоны
/// тона (шкала OpenCV 0..180, градусы ×2):
/// <list type="bullet">
/// <item>красный: 0..11 и 165..180 (0..22° и 330..360°), от малинового до красного;</item>
/// <item>желтый: 18..35 (36..70°), от золотистого до лимонного;</item>
/// <item>зеленый: 40..85 (80..170°);</item>
/// <item>синий: 95..130 (190..260°), от голубого до ультрамарина.</item>
/// </list>
/// Промежутки (оранжевый 12..17, салатовый 36..39, бирюзовый 86..94, фиолетовый 131..164) дают «непонятный цвет»:
/// рамка такого цвета не назначается никакой роли. Цветной точкой считается насыщенность
/// не ниже 80 и яркость не ниже 70 из 255: серый скан, желтизна бумаги и черный текст
/// (S &lt; 60 даже с цветной каймой JPEG) в маску не попадают.
///
/// Рамкой считается цветной контур, у которого все четыре стороны прямоугольника закрыты
/// линией не меньше чем на 85%, а внутри почти нет цвета (заливка закрывала бы текст).
/// Подписи синей ручкой, круглые печати и пятна под это не подходят и просто игнорируются.
/// </summary>
internal static class ColorFrames
{
    public const int MinSaturation = 80;

    /// <summary>Причина «рамок нет» — страница без разметки.</summary>
    public const string NoFrames = "не найдено цветных рамок";
    public const int MinValue = 70;

    /// <summary>Доля точек рамки одного цвета, чтобы цвет считался определенным.</summary>
    public const double DominantShare = 0.8;

    /// <summary>Тон точки → роль; null — цвет вне трех диапазонов.</summary>
    public static FieldRole? RoleOfHue(int hue) => hue switch
    {
        <= 11 or >= 165 => FieldRole.Combined,
        >= 18 and <= 35 => FieldRole.Type,
        >= 40 and <= 85 => FieldRole.Name,
        >= 95 and <= 130 => FieldRole.Designation,
        _ => null,
    };

    /// <summary>Маска цветных (насыщенных) точек картинки BGR.</summary>
    public static Mat ColoredMask(Mat bgr, out Mat hsv)
    {
        hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        var mask = new Mat();
        Cv2.InRange(hsv, new Scalar(0, MinSaturation, MinValue), new Scalar(180, 255, 255), mask);
        return mask;
    }

    /// <summary>Найти и проверить рамки на странице шаблона (BGR).</summary>
    public static FrameLayout Detect(Mat bgr)
    {
        using var mask = ColoredMask(bgr, out var hsv);
        using (hsv)
        {
            // Сжатие и сканирование рвут тонкую линию — сшиваем разрывы в пару точек.
            using var closed = new Mat();
            using (var k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5)))
                Cv2.MorphologyEx(mask, closed, MorphTypes.Close, k);

            Cv2.FindContours(closed, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            var minSide = Math.Max(12, Math.Min(bgr.Width, bgr.Height) / 100);

            var frames = new List<ColorFrame>();
            var problems = new List<string>();
            foreach (var contour in contours)
            {
                var box = Cv2.BoundingRect(contour);
                if (box.Width < minSide || box.Height < minSide) continue;
                // Разноцветные рамки могут касаться: общая маска тогда дает один внешний
                // контур (часто Г-образный). Восстанавливаем прямоугольники по каждому цвету.
                // Общую сторону проверяем по полной маске: соседняя рамка могла перекрасить ее.
                var separate = SeparateTouching(hsv, closed, box, minSide);
                if (separate.Select(f => f.Role).Distinct().Count() > 1)
                {
                    foreach (var candidate in separate)
                    {
                        if (candidate.Role is not { } candidateRole)
                            problems.Add("рамка непонятного цвета (нужен синий, зеленый, желтый или красный)");
                        else AddFrame(candidateRole, candidate.Box, candidate.Stroke);
                    }
                    continue;
                }
                if (!LooksLikeFrame(closed, box, out var stroke, out var filled))
                {
                    if (filled && box.Width > minSide * 2 && box.Height > minSide)
                        problems.Add("закрашенная цветом область (рамка должна быть контуром, заливка закрывает текст)");
                    continue;
                }

                var role = DominantRole(hsv, mask, box, out var share);
                if (role is null)
                {
                    problems.Add(share < 0
                        ? "рамка непонятного цвета (нужен синий, зеленый, желтый или красный)"
                        : "цвет рамки неоднозначен (смесь цветов)");
                    continue;
                }

                AddFrame(role.Value, box, stroke);
            }

            void AddFrame(FieldRole role, Rect box, int stroke)
            {
                var inner = new Rect(box.X + stroke, box.Y + stroke, box.Width - 2 * stroke, box.Height - 2 * stroke);
                if (inner.Width < 4 || inner.Height < 4) return;
                frames.Add(new ColorFrame(role, NormRect.FromPixels(inner, bgr.Width, bgr.Height), box, stroke));

                if (CrossesText(bgr, closed, box, stroke))
                    problems.Add($"{Describe(role)} рамка проходит по тексту (рамка не должна закрывать буквы и цифры)");
            }

            return Validate(frames, problems, bgr.Width, bgr.Height);
        }
    }

    public static string Describe(FieldRole role) => role switch
    {
        FieldRole.Designation => "синяя",
        FieldRole.Name => "зеленая",
        FieldRole.Type => "желтая",
        _ => "красная",
    };

    private static FrameLayout Validate(List<ColorFrame> frames, List<string> problems, int width, int height)
    {
        if (problems.Count > 0) return new FrameLayout(frames, problems[0]);
        if (frames.Count == 0) return new FrameLayout(frames, NoFrames);

        var blue = frames.Count(f => f.Role == FieldRole.Designation);
        var green = frames.Count(f => f.Role == FieldRole.Name);
        var yellow = frames.Count(f => f.Role == FieldRole.Type);
        var red = frames.Count(f => f.Role == FieldRole.Combined);

        string? problem = null;
        if (blue > 1) problem = $"синих рамок {blue}, должна быть одна";
        else if (green > 1) problem = $"зеленых рамок {green}, должна быть одна";
        else if (yellow > 1) problem = $"желтых рамок {yellow}, должна быть одна";
        else if (red > 1) problem = $"красных рамок {red}, должна быть одна";
        else if (red == 1 && blue + green + yellow > 0)
            problem = "есть и красная рамка, и синяя, зеленая или желтая: непонятно, какую разметку использовать";
        else if (red == 0 && blue == 0) problem = "нет синей рамки (обозначение)";
        else if (red == 0 && green == 0) problem = "нет зеленой рамки (наименование)";
        else if (red == 0 && yellow == 0) problem = "нет желтой рамки (тип документа)";

        if (problem is null)
        {
            for (var i = 0; i < frames.Count && problem is null; i++)
                for (var j = i + 1; j < frames.Count && problem is null; j++)
                    // Линии соседних рамок могут совпадать, области текста — нет.
                    if (frames[i].Inner.Intersects(frames[j].Inner))
                        problem = "рамки перекрываются";
        }
        if (problem is null && frames.Any(f => f.Inner.Width * f.Inner.Height > 0.5))
            problem = "рамка занимает больше половины страницы";

        return new FrameLayout(frames, problem);
    }

    /// <summary>Все четыре стороны закрыты линией, внутри почти пусто. Толщина — по средней
    /// глубине линии от края.</summary>
    private static bool LooksLikeFrame(Mat mask, Rect box, out int stroke, out bool filled)
    {
        stroke = 0;
        filled = false;
        using var roi = new Mat(mask, box);
        var sides = SideCoverage(roi);

        int w = box.Width, h = box.Height;

        // Толщина: медиана длины сплошной линии от левого и верхнего края.
        var runs = new List<int>();
        for (var y = h / 4; y < h * 3 / 4; y += Math.Max(1, h / 20))
        {
            var run = 0;
            while (run < w / 2 && roi.At<byte>(y, run) != 0) run++;
            if (run > 0) runs.Add(run);
        }
        for (var x = w / 4; x < w * 3 / 4; x += Math.Max(1, w / 20))
        {
            var run = 0;
            while (run < h / 2 && roi.At<byte>(run, x) != 0) run++;
            if (run > 0) runs.Add(run);
        }
        runs.Sort();
        stroke = runs.Count == 0 ? 2 : runs[runs.Count / 2] + 1;

        var inner = new Rect(stroke + 1, stroke + 1, w - 2 * stroke - 2, h - 2 * stroke - 2);
        var fill = 1.0;
        if (inner.Width > 0 && inner.Height > 0)
        {
            using var innerRoi = new Mat(roi, inner);
            fill = Cv2.CountNonZero(innerRoi) / (double)(inner.Width * inner.Height);
        }
        filled = fill > 0.5;

        return sides.All(s => s >= 0.85) && fill < 0.15;
    }

    private static double[] SideCoverage(Mat roi)
    {
        int w = roi.Width, h = roi.Height;
        var band = Math.Clamp((int)(Math.Min(w, h) * 0.12), 3, Math.Min(25, Math.Min(w, h)));

        double Coverage(Func<int, (int x, int y)> origin, int length, bool horizontal, int depthSign)
        {
            var covered = 0;
            for (var i = 0; i < length; i++)
            {
                var (x0, y0) = origin(i);
                for (var d = 0; d < band; d++)
                {
                    var x = horizontal ? x0 : x0 + d * depthSign;
                    var y = horizontal ? y0 + d * depthSign : y0;
                    if (roi.At<byte>(y, x) != 0) { covered++; break; }
                }
            }
            return covered / (double)length;
        }

        var top = Coverage(i => (i, 0), w, true, 1);
        var bottom = Coverage(i => (i, h - 1), w, true, -1);
        var left = Coverage(i => (0, i), h, false, 1);
        var right = Coverage(i => (w - 1, i), h, false, -1);

        return [top, bottom, left, right];
    }

    private sealed record FrameCandidate(FieldRole? Role, Rect Box, int Stroke);

    /// <summary>Кандидаты разных цветов внутри одного связного контура. У каждого не менее
    /// трех собственных сторон либо две противоположные; остальные могут быть общими. Неизвестные цвета
    /// сохраняются для диагностики, а не приписываются ближайшей известной роли.</summary>
    private static List<FrameCandidate> SeparateTouching(Mat hsv, Mat colored, Rect component, int minSide)
    {
        using var roi = new Mat(hsv, component);
        var masks = Enumerable.Range(0, 5).Select(_ => new Mat(component.Height, component.Width, MatType.CV_8UC1, Scalar.All(0))).ToArray();
        try
        {
            int width = roi.Width, height = roi.Height;
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var pixel = roi.At<Vec3b>(y, x);
                    if (pixel.Item1 < MinSaturation || pixel.Item2 < MinValue) continue;
                    var role = RoleOfHue(pixel.Item0);
                    masks[role is null ? 4 : (int)role.Value].Set(y, x, (byte)255);
                }

            var candidates = new List<FrameCandidate>();
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
            for (var color = 0; color < masks.Length; color++)
            {
                using var closed = new Mat();
                Cv2.MorphologyEx(masks[color], closed, MorphTypes.Close, kernel);
                Cv2.FindContours(closed, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
                var boxes = contours.Select(c => Cv2.BoundingRect(c)).ToList();
                // У среднего поля верхняя и нижняя стороны могут быть перекрашены соседями:
                // оставшиеся боковые линии тогда образуют два отдельных контура.
                if (contours.Length > 1) boxes.Add(Cv2.BoundingRect(contours.SelectMany(c => c).ToArray()));
                foreach (var local in boxes.Distinct())
                {
                    if (local.Width < minSide || local.Height < minSide) continue;
                    using var ownBorder = new Mat(closed, local);
                    var sides = SideCoverage(ownBorder);
                    if (sides.Count(s => s >= 0.85) < 3 &&
                        !(sides[0] >= 0.85 && sides[1] >= 0.85) &&
                        !(sides[2] >= 0.85 && sides[3] >= 0.85)) continue;
                    var box = new Rect(component.X + local.X, component.Y + local.Y, local.Width, local.Height);
                    if (!LooksLikeFrame(colored, box, out var stroke, out _)) continue;
                    candidates.Add(new FrameCandidate(color == 4 ? null : (FieldRole)color, box, stroke));
                }
            }
            return candidates;
        }
        finally
        {
            foreach (var mask in masks) mask.Dispose();
        }
    }

    /// <summary>Роль по преобладающему тону точек рамки. share &lt; 0 — преобладает «непонятный»
    /// цвет; null при share ≥ 0 — цвета смешаны.</summary>
    private static FieldRole? DominantRole(Mat hsv, Mat mask, Rect box, out double share)
    {
        var counts = new int[5]; // Designation, Name, Type, Combined, нет роли
        using var hsvRoi = new Mat(hsv, box);
        using var maskRoi = new Mat(mask, box);
        for (var y = 0; y < box.Height; y++)
            for (var x = 0; x < box.Width; x++)
            {
                if (maskRoi.At<byte>(y, x) == 0) continue;
                var hue = hsvRoi.At<Vec3b>(y, x).Item0;
                var role = RoleOfHue(hue);
                counts[role is null ? 4 : (int)role.Value]++;
            }
        var total = counts.Sum();
        share = 0;
        if (total == 0) return null;
        var best = Array.IndexOf(counts, counts.Max());
        share = counts[best] / (double)total;
        if (best == 4)
        {
            share = -1;
            return null;
        }
        return share >= DominantShare ? (FieldRole)best : null;
    }

    /// <summary>
    /// Рамка проходит по тексту: по обе стороны линии в одном и том же месте есть темные
    /// точки, которые не являются длинными линиями таблицы, — буква разрезана рамкой.
    /// </summary>
    private static bool CrossesText(Mat bgr, Mat colored, Rect box, int stroke)
    {
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        using var dark = new Mat();
        Cv2.Threshold(gray, dark, 128, 255, ThresholdTypes.BinaryInv);
        // Кайма линии после JPEG (темная, но уже не насыщенная) — тоже рамка, не текст.
        using var coloredWide = new Mat();
        using (var k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5)))
            Cv2.Dilate(colored, coloredWide, k);
        using var notColored = new Mat();
        Cv2.BitwiseNot(coloredWide, notColored);
        Cv2.BitwiseAnd(dark, notColored, dark);
        using var text = PageImage.RemoveLines(dark, Math.Max(30, Math.Max(bgr.Width, bgr.Height) / 40));

        var gap = 3;
        var reach = Math.Max(3, stroke);
        int hits = 0, length = 0;

        bool Dark(int x, int y) => x >= 0 && y >= 0 && x < text.Width && y < text.Height && text.At<byte>(y, x) != 0;
        bool AnyRun(int x, int y, int dx, int dy)
        {
            for (var d = 0; d < reach; d++)
                if (Dark(x + dx * d, y + dy * d)) return true;
            return false;
        }

        for (var x = box.Left + stroke; x < box.Right - stroke; x++)
        {
            length += 2;
            if (AnyRun(x, box.Top - gap, 0, -1) && AnyRun(x, box.Top + stroke + gap, 0, 1)) hits++;
            if (AnyRun(x, box.Bottom + gap, 0, 1) && AnyRun(x, box.Bottom - stroke - gap, 0, -1)) hits++;
        }
        for (var y = box.Top + stroke; y < box.Bottom - stroke; y++)
        {
            length += 2;
            if (AnyRun(box.Left - gap, y, -1, 0) && AnyRun(box.Left + stroke + gap, y, 1, 0)) hits++;
            if (AnyRun(box.Right + gap, y, 1, 0) && AnyRun(box.Right - stroke - gap, y, -1, 0)) hits++;
        }
        // Порог — в точках, а не в доле периметра: буква, разрезанная рамкой, дает пересечение
        // лишь на высоту строки, а рамка может быть длинной.
        return hits > Math.Max(4, length * 0.005);
    }
}
