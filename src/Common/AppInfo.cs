using System.Reflection;

namespace DocumentTextRecognition.Common;

/// <summary>Имя программы, версия и папки на компьютере пользователя.</summary>
public static class AppInfo
{
    public const string Title = "Распознавание текста документов";

    /// <summary>Файл программы в папке установки.</summary>
    public const string ExeName = "DocumentTextRecognition.exe";

    /// <summary>Установщик и файл обновления: один файл в папке обновлений.</summary>
    public const string SetupName = Title + "-Setup.exe";

    /// <summary>Ключ в «Установленных приложениях» (HKCU\...\Uninstall).</summary>
    public const string UninstallKey = "DocumentTextRecognition";

    public const string Publisher = "Roman Burov";

    /// <summary>Версия «1.2.3» из сборки, которая запущена.</summary>
    public static string Version { get; } = Short(typeof(AppInfo).Assembly.GetName().Version);

    public static string Short(Version? v) => v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";

    /// <summary>Папка установки: %LOCALAPPDATA%\Распознавание текста документов (права
    /// администратора не нужны).</summary>
    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Title);

    /// <summary>Настройки и журнал: %APPDATA%\Распознавание текста документов. Переопределяется
    /// переменной DTR_DATA_DIR (для проверок).</summary>
    public static string DataDir =>
        Environment.GetEnvironmentVariable("DTR_DATA_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Title);

    public static string LogDir => Path.Combine(DataDir, "Журнал");
}
