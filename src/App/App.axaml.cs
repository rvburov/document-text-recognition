using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace DocumentTextRecognition;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = Program.Args.Contains("--uninstall")
                ? new Dialogs.UninstallWindow()
                : new MainWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
