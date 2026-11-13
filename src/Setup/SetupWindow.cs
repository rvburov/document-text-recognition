using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DocumentTextRecognition.Common;

namespace DocumentTextRecognition.Setup;

/// <summary>
/// Окно установщика по макету (экран «Установка и обновление»): папка программы, ярлыки, папка
/// обновлений, затем ход копирования и итог. В режиме обновления сразу ставит новую версию.
/// </summary>
public sealed class SetupWindow : Window
{
    private readonly bool _update;
    private readonly StackPanel _body = new() { Spacing = 12 };
    private readonly CheckBox _startMenu = new() { Content = "Ярлык в меню «Пуск»", IsChecked = true };
    private readonly CheckBox _desktop = new() { Content = "Ярлык на рабочем столе", IsChecked = true };
    private readonly TextBox _updateFolder = new() { Classes = { "path" } };
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1 };
    private readonly TextBlock _progressText = new() { FontWeight = FontWeight.SemiBold };
    private readonly CancellationTokenSource _cts = new();

    private static string SetupPath => Environment.ProcessPath ?? "";
    private static string InstallDir => AppInfo.InstallDir;

    public SetupWindow(bool update)
    {
        _update = update;
        Title = AppInfo.Title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.White;
        Content = new StackPanel
        {
            Margin = new Thickness(20, 18, 20, 16),
            Spacing = 14,
            Children = { Head(), _body },
        };
        if (update) Opened += async (_, _) => await RunUpdate();
        else ShowStart();
    }

    private static Control Head()
    {
        var head = new StackPanel { Spacing = 2 };
        head.Children.Add(new TextBlock { Text = AppInfo.Title, FontSize = 15, FontWeight = FontWeight.SemiBold });
        head.Children.Add(new TextBlock { Text = $"Версия {AppInfo.Version}", Classes = { "muted" } });
        return head;
    }

    private static Button Button(string text, string? cls = null)
    {
        var b = new Button { Content = text, MinWidth = 110 };
        if (cls is not null) b.Classes.Add(cls);
        return b;
    }

    private static Control Row(params Control[] buttons) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0),
        Children = { },
    }.With(p => { foreach (var b in buttons) p.Children.Add(b); });

    private static TextBlock Text(string text, string? cls = null)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (cls is not null) t.Classes.Add(cls);
        return t;
    }

    private static Control Field(string label, Control input) => new StackPanel
    {
        Spacing = 4, Children = { new TextBlock { Text = label, FontWeight = FontWeight.Medium }, input },
    };

    /// <summary>Папка, откуда запущен установщик: если это не временная папка, она и есть папка обновлений.</summary>
    private static string LaunchFolder()
    {
        var dir = Path.GetDirectoryName(SetupPath) ?? "";
        var temp = Path.GetFullPath(Path.GetTempPath());
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return dir.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || dir.StartsWith(downloads, StringComparison.OrdinalIgnoreCase) ? "" : dir;
    }

    private void ShowStart()
    {
        var settings = AppSettings.Load();
        _updateFolder.Text = settings.UpdateFolder.Length > 0 ? settings.UpdateFolder : LaunchFolder();
        long size = 0;
        string? problem = null;
        try { size = Installation.UnpackedSize(SetupPath); }
        catch (Exception ex) { problem = ex.Message; }

        var install = Button("Установить", "accent");
        var cancel = Button("Отмена");
        cancel.Click += (_, _) => Close();
        install.Click += async (_, _) => await RunInstall();
        install.IsEnabled = problem is null;

        _body.Children.Clear();
        _body.Children.Add(Text("Программа установится только для вас, права администратора не нужны."));
        _body.Children.Add(Field("Папка программы", Text(@"%LOCALAPPDATA%\" + AppInfo.Title, "muted")));
        _body.Children.Add(_startMenu);
        _body.Children.Add(_desktop);
        _body.Children.Add(Field("Папка обновлений", _updateFolder));
        _body.Children.Add(Text(problem is not null
            ? $"Установщик поврежден: {problem}. Скопируйте его заново из папки обновлений."
            : $"Около {Math.Max(1, size / 1048576)} МБ: программа и компоненты распознавания текста. Windows может предупредить «Неизвестный издатель»: нажмите «Подробнее», затем «Выполнить в любом случае».",
            "hint"));
        _body.Children.Add(Row(cancel, install));
    }

    private void ShowProgress(string note)
    {
        var cancel = Button("Отмена");
        cancel.Click += (_, _) => _cts.Cancel();
        cancel.IsVisible = !_update;
        _progressText.Text = "Копирование файлов… 0%";
        _body.Children.Clear();
        _body.Children.Add(_progressText);
        _body.Children.Add(_bar);
        _body.Children.Add(Text(note, "hint"));
        _body.Children.Add(Row(cancel));
    }

    private Action<double, string> Progress() => (part, dir) => Dispatcher.UIThread.Post(() =>
    {
        _bar.Value = part;
        _progressText.Text = $"Копирование файлов… {Math.Round(part * 100)}% {dir}";
    });

    private async Task RunInstall()
    {
        var updateFolder = (_updateFolder.Text ?? "").Trim();
        bool startMenu = _startMenu.IsChecked == true, desktop = _desktop.IsChecked == true;
        ShowProgress("Создаются ярлыки, программа появится в «Приложения → Установленные приложения».");
        try
        {
            await Task.Run(() =>
            {
                Installation.Install(SetupPath, InstallDir, Progress(), _cts.Token);
                Installation.Register(InstallDir, AppInfo.Version, startMenu, desktop);
            });
            var settings = AppSettings.Load();
            settings.UpdateFolder = updateFolder;
            settings.Save();
            ShowDone(startMenu, desktop);
        }
        catch (OperationCanceledException)
        {
            ShowStart();
        }
        catch (Exception ex)
        {
            AppLog.Error("Установка не удалась", ex);
            ShowError($"Программа не установлена: {ex.Message}");
        }
    }

    private void ShowDone(bool startMenu, bool desktop)
    {
        var where = (startMenu, desktop) switch
        {
            (true, true) => " в меню «Пуск» и на рабочем столе",
            (true, false) => " в меню «Пуск»",
            (false, true) => " на рабочем столе",
            _ => "",
        };
        var close = Button("Закрыть");
        var open = Button("Открыть программу", "accent");
        close.Click += (_, _) => Close();
        open.Click += (_, _) => { LaunchApp(); Close(); };
        _body.Children.Clear();
        _body.Children.Add(Text(where.Length > 0 ? $"Программа установлена. Ярлык «{AppInfo.Title}»{where}." : "Программа установлена."));
        _body.Children.Add(Row(close, open));
    }

    private void ShowError(string message)
    {
        var close = Button("Закрыть");
        close.Click += (_, _) => Close();
        _body.Children.Clear();
        _body.Children.Add(Text(message));
        _body.Children.Add(Row(close));
    }

    /// <summary>Обновление: дождаться закрытия программы, поставить новую версию, открыть программу.</summary>
    private async Task RunUpdate()
    {
        ShowProgress("Программа закрылась и откроется сама через несколько секунд. Если замена не удастся, откроется прежняя версия и скажет почему.");
        _progressText.Text = $"Установка версии {AppInfo.Version}…";
        string? failure = null;
        try
        {
            await Task.Run(() =>
            {
                if (Program.WaitPid() is { } pid) Installation.WaitForExit(pid);
                var hadDesktop = OperatingSystem.IsWindows() && File.Exists(WindowsShell.DesktopShortcut);
                var hadStart = !OperatingSystem.IsWindows() || File.Exists(WindowsShell.StartMenuShortcut);
                Installation.Install(SetupPath, InstallDir, Progress(), CancellationToken.None);
                Installation.Register(InstallDir, AppInfo.Version, hadStart, hadDesktop);
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("Обновление не установлено", ex);
            failure = ex.Message;
        }
        LaunchApp(failure);
        Close();
    }

    private static void LaunchApp(string? updateFailure = null)
    {
        var exe = Path.Combine(InstallDir, AppInfo.ExeName);
        if (!File.Exists(exe)) return;
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = InstallDir };
        if (updateFailure is not null)
        {
            info.ArgumentList.Add("--update-failed");
            info.ArgumentList.Add(updateFailure);
        }
        Process.Start(info);
    }
}

internal static class ControlExtensions
{
    public static T With<T>(this T control, Action<T> setup)
    {
        setup(control);
        return control;
    }
}
