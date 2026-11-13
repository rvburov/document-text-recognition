using System.Diagnostics;

namespace DocumentTextRecognition.Common;

/// <summary>
/// Папка обновлений на сетевом диске: там лежит один файл «Распознавание текста
/// документов-Setup.exe». Версия читается прямо из файла (сведения о версии Windows), без
/// копирования. Интернет не нужен.
/// </summary>
public static class UpdateSource
{
    public static string SetupPath(string folder) => Path.Combine(folder, AppInfo.SetupName);

    /// <summary>Версия установщика в папке; null, если папки или файла нет.</summary>
    public static Version? Available(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        var path = SetupPath(folder);
        if (!File.Exists(path)) return null;
        var info = FileVersionInfo.GetVersionInfo(path);
        return Parse(info.ProductVersion) ?? Parse(info.FileVersion);
    }

    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var core = text.Split('+', '-', ' ')[0];
        return System.Version.TryParse(core, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
    }

    /// <summary>Версия в папке новее текущей.</summary>
    public static bool IsNewer(Version? available, string current) =>
        available is not null && Parse(current) is { } now && available > now;
}
