using OpenCvSharp;
using DocumentTextRecognition.Core.Imaging;

namespace DocumentTextRecognition.Core.Matching;

/// <summary>
/// Страница, подготовленная к сопоставлению: серая картинка с длинной стороной
/// <see cref="LongSide"/>, маска линий (рамка листа, графы основной надписи, таблицы) и
/// ключевые точки ORB. Одинаково строится для шаблона и для обрабатываемого скана.
/// </summary>
internal sealed class PageFeatures : IDisposable
{
    /// <summary>Длинная сторона картинки для сопоставления, точек.</summary>
    public const int LongSide = 1600;

    /// <summary>Сколько ключевых точек искать на странице.</summary>
    public const int FeatureCount = 5000;

    public Mat Gray { get; }

    /// <summary>Маска линий — считается по требованию (нужна шаблонам; у сканов линии
    /// ищутся после переноса в координаты шаблона).</summary>
    public Mat Lines => _lines.Value;
    private readonly Lazy<Mat> _lines;

    /// <summary>Маска линий с допуском для проверки совпадения. У шаблона одна на все
    /// документы и потоки; после создания больше не изменяется.</summary>
    public Mat GrownLines => _grownLines.Value;
    private readonly Lazy<Mat> _grownLines;
    public KeyPoint[] KeyPoints { get; }
    public Mat Descriptors { get; }

    public int Width => Gray.Width;
    public int Height => Gray.Height;

    /// <summary>Сколько точек картинки на пункт страницы PDF.</summary>
    public double Scale { get; }

    private PageFeatures(Mat gray, KeyPoint[] keyPoints, Mat descriptors, double scale)
    {
        Gray = gray;
        KeyPoints = keyPoints;
        Descriptors = descriptors;
        Scale = scale;
        _lines = new(() => LineMaskOf(Gray), LazyThreadSafetyMode.ExecutionAndPublication);
        _grownLines = new(() => GrowLines(Lines), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Подготовить серую картинку страницы (длинная сторона — <see cref="LongSide"/>).
    /// <paramref name="scale"/> — точек на пункт PDF.</summary>
    public static PageFeatures Build(Mat gray, double scale)
    {
        using var orb = ORB.Create(FeatureCount, 1.2f, 8, 31, 0, 2, ORBScoreType.Harris, 31, 20);
        var descriptors = new Mat();
        orb.DetectAndCompute(gray, null, out var keyPoints, descriptors);
        return new PageFeatures(gray, keyPoints, descriptors, scale);
    }

    /// <summary>Маска длинных линий страницы. Длина линии — от 1/40 длинной стороны.</summary>
    public static Mat LineMaskOf(Mat gray)
    {
        using var ink = PageImage.InkMask(gray);
        return PageImage.LineMask(ink, Math.Max(20, Math.Max(gray.Width, gray.Height) / 40));
    }

    public static Mat GrowLines(Mat lines)
    {
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect,
            new Size(2 * TemplateMatcher.LineTolerance + 1, 2 * TemplateMatcher.LineTolerance + 1));
        var grown = new Mat();
        Cv2.Dilate(lines, grown, kernel);
        return grown;
    }

    public void Dispose()
    {
        Gray.Dispose();
        if (_lines.IsValueCreated) _lines.Value.Dispose();
        if (_grownLines.IsValueCreated) _grownLines.Value.Dispose();
        Descriptors.Dispose();
    }
}
