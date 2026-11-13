using System.Text;

namespace DocumentTextRecognition.Common;

/// <summary>
/// Журнал работы программы: что делала программа и ошибки, без содержимого файлов. Файл на
/// день в папке «Журнал» данных пользователя; хранятся последние 30 файлов.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static bool _cleaned;

    public static string TodayFile => Path.Combine(AppInfo.LogDir, $"{DateTime.Now:yyyy-MM-dd}.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppInfo.LogDir);
                File.AppendAllText(TodayFile, $"{DateTime.Now:HH:mm:ss} {level} {message}{Environment.NewLine}", Encoding.UTF8);
                if (!_cleaned)
                {
                    _cleaned = true;
                    foreach (var old in Directory.GetFiles(AppInfo.LogDir, "*.log").OrderDescending().Skip(30))
                        File.Delete(old);
                }
            }
        }
        catch (Exception)
        {
            // Журнал не должен ронять программу.
        }
    }
}
