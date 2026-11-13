using Avalonia;
using DocumentTextRecognition.Common;
using DocumentTextRecognition.Core.Ocr;

namespace DocumentTextRecognition;

/// <summary>
/// Точка входа. Режимы:
///   без ключей      окно программы;
///   --uninstall     удаление (запускается из «Установленных приложений»);
///   --check         проверка компонентов распознавания без окна (код выхода 0, если все готово).
/// </summary>
public static class Program
{
    public static string[] Args { get; private set; } = [];

    [STAThread]
    public static int Main(string[] args)
    {
        Args = args;
        if (args.Contains("--check")) return Check();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Error("Программа закрылась из-за ошибки", e.ExceptionObject as Exception);
        AppLog.Info($"Запуск {AppInfo.Title} {AppInfo.Version}");
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            AppLog.Error("Программа закрылась из-за ошибки", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    /// <summary>Проверка компонентов распознавания: оба способа должны запуститься.</summary>
    private static int Check()
    {
        var problems = OcrComponents.CheckReady();
        Console.WriteLine(problems.Count == 0
            ? "Компоненты распознавания готовы: Tesseract и PaddleOCR."
            : string.Join(Environment.NewLine, problems));
        return problems.Count == 0 ? 0 : 1;
    }
}
