using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DocumentTextRecognition.Common;

namespace DocumentTextRecognition.Setup;

/// <summary>
/// Установщик. Без ключей: окно установки. --update --wait PID: обновление (ждет, пока программа
/// закроется, ставит новую версию и открывает программу снова).
/// </summary>
public static class Program
{
    public static string[] Args { get; private set; } = [];

    [STAThread]
    public static int Main(string[] args)
    {
        Args = args;
        AppLog.Info($"Установщик {AppInfo.Version}, ключи: {string.Join(' ', args)}");
        AppBuilder.Configure<SetupApp>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static int? WaitPid()
    {
        var i = Array.IndexOf(Args, "--wait");
        return i >= 0 && i + 1 < Args.Length && int.TryParse(Args[i + 1], out var pid) ? pid : null;
    }
}

public partial class SetupApp : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new SetupWindow(update: Program.Args.Contains("--update"));
        base.OnFrameworkInitializationCompleted();
    }
}
