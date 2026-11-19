using System.Security.Cryptography;
using DocumentTextRecognition.Tests.Samples;
using OpenCvSharp;
using SkiaSharp;

namespace DocumentTextRecognition.Tests.Alphabet;

/// <summary>Роль строки в основной надписи: от нее зависит профиль движка и правила разбора.</summary>
internal enum AlphabetRole { Name, Designation }

internal sealed record AlphabetLine(AlphabetRole Role, string Text, double CapHeightMm);

/// <summary>
/// Шрифт прогона. Чертежные шрифты лежат в tests/Core.Tests/Fonts с проверкой SHA-256;
/// <paramref name="SkewX"/> наклоняет прямой шрифт на 15 градусов, как курсив ГОСТ в САПР.
/// </summary>
internal sealed record AlphabetFont(string Key, string File, string? Sha256, float SkewX, bool System = false)
{
    public static readonly IReadOnlyList<AlphabetFont> All =
    [
        new("gost-a-slanted", "GOST2.304-81TypeA-Slanted.ttf", "e83689021a988a15736a70799aa166345499de548664079c58435d54d7cd973e", 0f),
        new("opengost-a-italic", "OpenGOSTtypeA-Regular.ttf", "3caec4e2a004eacf8aea6883781015fc2df9b7c0154e974cec97f22299d7fc0e", -0.268f),
        new("opengost-b-italic", "OpenGOSTtypeB-Regular.ttf", "510f175c6856e7d9fc4ab2388266cf09860ef7e9ab10fc1baa6b64d09e4c63d9", -0.268f),
        new("opengost-a-upright", "OpenGOSTtypeA-Regular.ttf", "3caec4e2a004eacf8aea6883781015fc2df9b7c0154e974cec97f22299d7fc0e", 0f),
        new("dejavu-sans", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", null, 0f, System: true),
    ];

    public string Path
    {
        get
        {
            if (System) return File;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var candidate = System ? File : global::System.IO.Path.Combine(dir.FullName, "tests", "Core.Tests", "Fonts", File);
                if (global::System.IO.File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException("Шрифт прогона алфавита не найден; подмена шрифта запрещена.", File);
        }
    }

    public bool Available => !System || global::System.IO.File.Exists(File);

    public SKTypeface Open()
    {
        var path = Path;
        if (Sha256 is not null && !string.Equals(Convert.ToHexString(SHA256.HashData(global::System.IO.File.ReadAllBytes(path))),
                Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{File}: SHA-256 отличается от записанного в тесте.");
        return SKTypeface.FromFile(path) ?? throw new InvalidDataException($"{File}: шрифт не открывается.");
    }
}

/// <summary>Условия съемки: чистый растр или «скан» с размытием, шумом и JPEG.</summary>
internal sealed record AlphabetCondition(string Key, double Blur, double Noise, int Jpeg)
{
    public static readonly IReadOnlyList<AlphabetCondition> All =
    [
        new("clean", 0, 0, 0),
        new("scan", 0.7, 0.03, 75),
    ];
}

/// <summary>
/// Строки прогона: каждая буква русского алфавита (строчная и прописная), цифры и знаки
/// обозначений встречаются по нескольку раз в начале, середине и конце настоящих слов
/// технических наименований. Обозначения собраны только из букв, допустимых в коде
/// организации по ГОСТ 2.201 (без З, О, Ч, Й, Ъ, Ы, Ь, Ё).
/// </summary>
internal static class AlphabetSamples
{
    public const string RussianLower = "абвгдежзийклмнопрстуфхцчшщъыьэюя";
    public const string RussianUpper = "АБВГДЕЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ";
    public const string Digits = "0123456789";

    public static readonly IReadOnlyList<string> Names =
    [
        "Плата индикации", "Кронштейн опорный", "Захват 19590", "Блок питания", "Щит распределительный",
        "Шкаф управления", "Жгут соединительный", "Фланец переходной", "Втулка распорная", "Хомут стяжной",
        "Цилиндр гидравлический", "Эмаль защитная", "Юбка поршня", "Ящик монтажный", "Подъемник винтовой",
        "Съемник подшипника", "Мягкий элемент", "Вал ведущий", "Вилка включения", "Гайка накидная",
        "Шайба стопорная", "Винт регулировочный", "Болт фундаментный", "Шпилька резьбовая", "Пружина сжатия",
        "Крышка верхняя", "Крышка нижняя", "Обойма", "Ось качания", "Тележка тяговая",
        "Электродвигатель в сборе", "Устройство измерения", "Генератор сигналов", "Усилитель мощности",
        "Фильтр сетевой", "Датчик температуры", "Преобразователь напряжения", "Модуль связи", "Антенна штыревая",
        "Разъем угловой", "Рама сварная", "Основание", "Стойка", "Панель лицевая", "Экран защитный",
        "Чехол", "Щуп", "Ключ гаечный", "Муфта упругая", "Шестерня ведомая", "Колесо зубчатое", "Червяк",
        "Эксцентрик", "Юстировочный винт", "Якорь", "Объектив", "Изолятор опорный", "Рычаг",
        "Выключатель", "Защелка", "Кожух", "Держатель", "Шток", "Ступица", "Цепь приводная", "Люк смотровой",
        "Шлюз", "Тяга", "Форсунка", "Механизм подъема", "Ферма", "Въезд", "Крайний упор", "Войлок",
        "Пульт", "Вентиль", "Сборка крепежных деталей", "Система управления", "Корпус", "Шина",
        "Контроллер теплицы", "Плата отладочная", "Модуль USB-RS485", "Плата CAN", "Гребенка", "Буфер",
        "Щетка", "Ёмкость", "Блок эталонный", "Панель поэтажная",
    ];

    public static readonly IReadOnlyList<string> UpperNames =
    [
        "НАГРЕВАТЕЛЬ 72758", "ШЕСТЕРНЯ ЦИЛИНДРИЧЕСКАЯ", "ЭКРАН ЗАЩИТНЫЙ", "ЮБКА", "КОЖУХ", "ФЛАНЕЦ",
        "ПОДЪЕМНИК", "ЯЩИК", "ЩУП", "КРАЙНИЙ ВОЙЛОК", "РЫЧАГ", "ОСЬ", "ОБЪЕКТИВ", "БУФЕР", "ЗАХВАТ",
    ];

    public static readonly IReadOnlyList<string> Designations =
    [
        "АБВГ.468157.012", "ДЕЖИ.301423.567СБ", "КЛМН.436789.890Э3", "ПРСТ.712345.006ПЭ3",
        "УФХЦ.563251.001ТУ", "ШЩЭЮ.741374.002", "ЯАБВ.426477.019Э4", "ГДЕЖ.305110.248Д1",
        "ИКЛМ.687281.259-02СБ", "НПРС.468789.003ВП", "ТУФХ.123456.700К3", "ЦШЩЭ.909090.111ПЗ",
        "ЮЯАД.654321.987",
    ];

    public static IReadOnlyList<AlphabetLine> Lines()
    {
        var lines = new List<AlphabetLine>();
        lines.AddRange(Names.Select(n => new AlphabetLine(AlphabetRole.Name, n, 3.5)));
        lines.AddRange(UpperNames.Select(n => new AlphabetLine(AlphabetRole.Name, n, 3.5)));
        lines.AddRange(Designations.Select(d => new AlphabetLine(AlphabetRole.Designation, d, 5)));
        // Крупный шрифт обозначения: штрихи длиннее 7 мм не должны пропадать при удалении линий граф.
        lines.AddRange(Designations.Take(5).Select(d => new AlphabetLine(AlphabetRole.Designation, d, 7)));
        return lines;
    }

    /// <summary>Сколько раз каждая буква и цифра встречается в строках прогона.</summary>
    public static Dictionary<char, int> Coverage()
    {
        var text = string.Concat(Lines().Select(l => l.Text));
        return (RussianLower + RussianUpper + Digits).ToDictionary(c => c, c => text.Count(x => x == c));
    }

    /// <summary>Строка как фрагмент основной надписи: шрифт, наклон, высота прописной,
    /// затем «скан» по условиям. BGR, белый фон.</summary>
    public static Mat Render(string text, AlphabetFont font, double capHeightMm, AlphabetCondition condition, int seed, int dpi = 300)
    {
        using var typeface = font.Open();
        using var paint = new SKPaint { Typeface = typeface, TextSize = 100, IsAntialias = true, Color = SKColors.Black, TextSkewX = font.SkewX };
        var bounds = new SKRect();
        paint.MeasureText("Н", ref bounds);
        paint.TextSize = (float)(100 * capHeightMm * dpi / 25.4 / bounds.Height);
        var r = new SKRect();
        paint.MeasureText(text, ref r);
        var pad = (float)(2.5 * dpi / 25.4);
        var width = (int)Math.Ceiling(r.Width + 2 * pad);
        var height = (int)Math.Ceiling(r.Height + 2 * pad);
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(SKColors.White);
        surface.Canvas.DrawText(text, pad - r.Left, pad - r.Top, paint);
        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var bgr = Cv2.ImDecode(png.ToArray(), ImreadModes.Color);
        if (condition.Blur > 0) Cv2.GaussianBlur(bgr, bgr, new Size(0, 0), condition.Blur);
        if (condition.Noise > 0)
        {
            using var noise = new Mat(bgr.Size(), MatType.CV_16SC3);
            new RNG((ulong)seed).Fill(noise, DistributionType.Normal, Scalar.All(0), Scalar.All(condition.Noise * 255));
            using var wide = new Mat();
            bgr.ConvertTo(wide, MatType.CV_16SC3);
            Cv2.Add(wide, noise, wide);
            wide.ConvertTo(bgr, MatType.CV_8UC3);
        }
        if (condition.Jpeg > 0)
        {
            Cv2.ImEncode(".jpg", bgr, out var jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, condition.Jpeg));
            using var decoded = Cv2.ImDecode(jpeg, ImreadModes.Color);
            decoded.CopyTo(bgr);
        }
        return bgr;
    }
}
