using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace DocumentTextRecognition.Core.Ocr;

/// <summary>
/// Где лежат компоненты распознавания и готовы ли они. Все лежит в папке ocr\ рядом с
/// программой: модели PaddleOCR в ocr\paddle, данные Tesseract в ocr\tessdata, Tesseract для
/// Windows в ocr\tesseract. На Linux (только проверки) Tesseract берется из системы.
///
/// Пути к данным передаются в Tesseract и PaddleOCR как строки C, а эти библиотеки на Windows
/// не открывают пути с русскими буквами (имя пользователя «Иванов» в пути к профилю). Поэтому
/// перед запуском такие пути заменяются на короткие (8.3) или данные копируются в
/// %ProgramData%\DocumentTextRecognition, где путь латинский (<see cref="AsciiDirectory"/>).
///
/// Ничего не скачивается: распознавание идет на этом компьютере, сканы никуда не уходят.
/// </summary>
internal static class OcrComponents
{
    /// <summary>Языки Tesseract: русский и английский (латиница в обозначениях и наименованиях).</summary>
    public const string TesseractLanguages = "rus+eng";

    /// <summary>Модели PaddleOCR v5: многоязычный детектор строк и распознавание
    /// восточнославянских языков с латиницей и цифрами.</summary>
    public const string PaddleDetector   = "mobile_zh_det";
    public const string PaddleRecognizer = "eslav_PP_OCRv5_mobile_rec";

    /// <summary>Папка программы: там, где лежит сборка ядра (при запуске одним файлом это
    /// папка, куда .NET распаковал программу).</summary>
    public static string ProgramDir { get; } =
        typeof(OcrComponents).Assembly.Location is { Length: > 0 } location
            ? Path.GetDirectoryName(location) ?? AppContext.BaseDirectory
            : AppContext.BaseDirectory;

    public static string PaddleDir => AsciiDirectory(Path.Combine(ProgramDir, "ocr", "paddle"));

    private static int _resolverSet;

    /// <summary>Подсказать загрузчику, где искать Tesseract: в папке программы (Windows) или в
    /// системе (Linux).</summary>
    public static void RegisterNativeResolver()
    {
        if (Interlocked.Exchange(ref _resolverSet, 1) == 1) return;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(OcrComponents).Assembly, Resolve);
        }
        catch (InvalidOperationException)
        {
            // Уже назначен (повторная загрузка в тестах).
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != TesseractApi.Library) return IntPtr.Zero;
        foreach (var candidate in TesseractCandidates())
            if (NativeLibrary.TryLoad(candidate, out var handle)) return handle;
        return IntPtr.Zero;
    }

    /// <summary>Где искать библиотеку Tesseract на этой ОС, в порядке предпочтения.</summary>
    public static IEnumerable<string> TesseractCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(ProgramDir, "ocr", "tesseract", "tesseract55.dll");
            yield break;
        }
        yield return Path.Combine(ProgramDir, "ocr", "tesseract", "libtesseract.so.5");
        yield return "libtesseract.so.5";
        yield return "libtesseract.so";
    }

    /// <summary>Папка с данными Tesseract, где есть оба языка (латинский путь); null, если нет нигде.</summary>
    public static string? FindTessdata()
    {
        foreach (var dir in TessdataCandidates())
            if (File.Exists(Path.Combine(dir, "rus.traineddata")) && File.Exists(Path.Combine(dir, "eng.traineddata")))
                return AsciiDirectory(dir);
        return null;
    }

    private static IEnumerable<string> TessdataCandidates()
    {
        yield return Path.Combine(ProgramDir, "ocr", "tessdata");
        if (Environment.GetEnvironmentVariable("TESSDATA_PREFIX") is { Length: > 0 } prefix)
        {
            yield return prefix;
            yield return Path.Combine(prefix, "tessdata");
        }
        if (OperatingSystem.IsWindows()) yield break;
        yield return "/usr/share/tesseract-ocr/5/tessdata";
        yield return "/usr/share/tesseract-ocr/4.00/tessdata";
        yield return "/usr/share/tessdata";
    }

    /// <summary>
    /// Путь к папке, который поймут Tesseract и PaddleOCR: сам путь, если в нем только латиница;
    /// иначе короткий путь Windows (8.3); иначе копия папки в %ProgramData%\DocumentTextRecognition
    /// (копируется один раз, пока размер и время файлов не изменились).
    /// </summary>
    public static string AsciiDirectory(string dir)
    {
        if (IsAscii(dir) || !OperatingSystem.IsWindows() || !Directory.Exists(dir)) return dir;
        var shortPath = ShortPath(dir);
        if (shortPath is not null && IsAscii(shortPath)) return shortPath;

        var root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!IsAscii(root)) root = Path.GetPathRoot(Path.GetTempPath()) ?? @"C:\";
        var copy = Path.Combine(root, "DocumentTextRecognition", "ocr", Path.GetFileName(dir));
        CopyIfChanged(dir, copy);
        return copy;
    }

    public static bool IsAscii(string s) => s.All(c => c < 128);

    private static void CopyIfChanged(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            var source = new FileInfo(file);
            var existing = new FileInfo(target);
            if (existing.Exists && existing.Length == source.Length && existing.LastWriteTimeUtc == source.LastWriteTimeUtc)
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            File.SetLastWriteTimeUtc(target, source.LastWriteTimeUtc);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint bufferLength);

    private static string? ShortPath(string path)
    {
        try
        {
            var buffer = new StringBuilder(1024);
            var length = GetShortPathNameW(path, buffer, (uint)buffer.Capacity);
            return length == 0 || length > buffer.Capacity ? null : buffer.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Проверить, что ОБА способа распознавания готовы: библиотеки грузятся, модели и языковые
    /// данные на месте. Возвращает список проблем понятным текстом; пустой значит можно
    /// распознавать. Проверка честная: движки создаются по-настоящему и сразу освобождаются.
    /// </summary>
    public static IReadOnlyList<string> CheckReady()
    {
        RegisterNativeResolver();
        var problems = new List<string>();

        var tessdata = FindTessdata();
        if (tessdata is null)
            problems.Add("Tesseract: нет языковых данных rus.traineddata и eng.traineddata в папке ocr\\tessdata программы.");
        else
        {
            try { using var engine = new TesseractEngine(tessdata); }
            catch (Exception ex) { problems.Add($"Tesseract не запускается: {Explain(ex)}"); }
        }

        try { using var engine = new PaddleEngine(PaddleDir); }
        catch (Exception ex) { problems.Add($"PaddleOCR не запускается: {Explain(ex)}"); }

        return problems;
    }

    internal static string Explain(Exception ex) => ex switch
    {
        DllNotFoundException when OperatingSystem.IsLinux()
            => "не найдена системная библиотека (на Linux: sudo apt install tesseract-ocr)",
        DllNotFoundException when OperatingSystem.IsWindows()
            => "не загружается библиотека: переустановите программу (библиотеки Microsoft Visual C++ " +
               "должны лежать рядом с ней)",
        DllNotFoundException d => d.Message,
        TypeInitializationException { InnerException: { } inner } => Explain(inner),
        _ => ex.Message,
    };
}
