using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DocumentTextRecognition.Common;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Dialogs;
using DocumentTextRecognition.Services;

namespace DocumentTextRecognition.Pages;

/// <summary>
/// «О программе» и обновление, как в программах организации: при запуске программа смотрит в
/// папку обновлений на сетевом диске; если там версия новее, предлагает установить. Все про
/// обновление здесь, в настройках его нет.
/// </summary>
public partial class AboutPage : UserControl
{
    private MainWindow _main = null!;
    private Version? _available;
    private CancellationTokenSource? _copy;
    private bool _loading;

    public AboutPage() => InitializeComponent();

    public void Attach(MainWindow main)
    {
        _main = main;
        var built = File.GetLastWriteTime(typeof(AboutPage).Assembly.Location is { Length: > 0 } l ? l : AppContext.BaseDirectory);
        VersionLine.Text = $"Версия {AppInfo.Version} · {built:dd.MM.yyyy} · Windows x64";
        ProgramDir.Text = AppContext.BaseDirectory.TrimEnd('\\', '/');
        DataDir.Text = AppInfo.DataDir;
        CheckSwitch.IsCheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _main.Settings.CheckUpdates = CheckSwitch.IsChecked == true;
            _main.SaveSettings();
        };
        Reload();
        _ = CheckComponents();
    }

    public void Reload()
    {
        _loading = true;
        CheckSwitch.IsChecked = _main.Settings.CheckUpdates;
        _loading = false;
        FolderText.Text = _main.Settings.UpdateFolder.Length > 0 ? _main.Settings.UpdateFolder : "не задана";
        ShowState();
    }

    private async Task CheckComponents()
    {
        var problems = await Task.Run(() =>
        {
            try { return OcrComponents.CheckReady(); }
            catch (Exception ex) { return (IReadOnlyList<string>)[OcrComponents.Explain(ex)]; }
        });
        ComponentsPill.Classes.Set("success", problems.Count == 0);
        ComponentsPill.Classes.Set("danger", problems.Count > 0);
        ComponentsText.Text = problems.Count == 0 ? "Tesseract 5.5 · PaddleOCR 3.3 · готовы" : "не готовы";
        ComponentsProblem.Text = string.Join("\n", problems);
        ComponentsProblem.IsVisible = problems.Count > 0;
        if (problems.Count > 0) AppLog.Error("Компоненты распознавания: " + string.Join("; ", problems));
    }

    /// <summary>Проверка при запуске: если в папке обновлений версия новее, спросить.</summary>
    public async Task CheckOnStartup()
    {
        if (!_main.Settings.CheckUpdates || _main.Settings.UpdateFolder.Length == 0) return;
        await Check(quiet: true);
        if (!UpdateSource.IsNewer(_available, AppInfo.Version)) return;
        var install = await Ask.Show(_main, "Обновление",
            $"Доступна версия {AppInfo.Short(_available)} (сейчас {AppInfo.Version}).", "Позже", "Установить",
            secondText: "Программа закроется, установит новую версию и откроется снова. Настройки сохранятся.");
        if (install) await Install();
    }

    private async Task Check(bool quiet)
    {
        var folder = _main.Settings.UpdateFolder;
        StateTitle.Text = "Проверка…";
        try
        {
            _available = await Task.Run(() => UpdateSource.Available(folder));
            if (_available is null && !Directory.Exists(folder))
            {
                ShowState($"Папка обновлений недоступна: {folder} не открывается. Работает текущая версия.");
                return;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Проверка обновлений не удалась", ex);
            ShowState($"Папка обновлений недоступна: {ex.Message}");
            return;
        }
        ShowState();
        if (!quiet && !UpdateSource.IsNewer(_available, AppInfo.Version)) _main.Toast("Обновлений нет");
    }

    private void ShowState(string? problem = null)
    {
        CopyRow.IsVisible = false;
        StateRow.IsVisible = true;
        var newer = UpdateSource.IsNewer(_available, AppInfo.Version);
        InstallButton.IsVisible = newer;
        CheckButton.IsVisible = !newer;
        StateTitle.Classes.Set("success", !newer && problem is null && _main.Settings.UpdateFolder.Length > 0);
        if (problem is not null)
        {
            StateTitle.Text = "Обновление не проверено";
            StateHint.Text = problem;
        }
        else if (_main.Settings.UpdateFolder.Length == 0)
        {
            StateTitle.Text = $"Установлена версия {AppInfo.Version}";
            StateHint.Text = "Папка обновлений не задана: укажите ее ниже.";
        }
        else if (newer)
        {
            StateTitle.Text = $"Доступна версия {AppInfo.Short(_available)}";
            StateHint.Text = $"Сейчас установлена {AppInfo.Version}. Найдена в папке обновлений {DateTime.Now:dd.MM.yyyy}.";
        }
        else
        {
            StateTitle.Text = $"Установлена последняя версия {AppInfo.Version}";
            StateHint.Text = _available is null ? "В папке обновлений нет установщика." : "Проверено только что.";
        }
    }

    private async void OnCheck(object? sender, RoutedEventArgs e) => await Check(quiet: false);

    private async void OnInstall(object? sender, RoutedEventArgs e) => await Install();

    /// <summary>Скопировать установщик с ходом и запустить обновление; программа закрывается.</summary>
    private async Task Install()
    {
        if (_main.Processing.IsRunning)
        {
            await Ask.Info(_main, "Обновление", "Идет переименование. Дождитесь окончания или нажмите «Стоп», затем установите обновление.");
            return;
        }
        _main.Show("about");
        StateRow.IsVisible = false;
        CopyRow.IsVisible = true;
        _copy = new CancellationTokenSource();
        var progress = new Progress<(long Done, long Total)>(p =>
        {
            var part = p.Total > 0 ? p.Done / (double)p.Total : 0;
            CopyBar.Value = part;
            CopyTitle.Text = $"Копирование обновления… {Math.Round(part * 100)}%";
            CopySize.Text = $"{p.Done / 1048576} из {p.Total / 1048576} МБ";
        });
        try
        {
            var setup = await Updater.CopySetup(_main.Settings.UpdateFolder, progress, _copy.Token);
            Updater.Launch(setup);
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }
        catch (OperationCanceledException)
        {
            ShowState();
        }
        catch (Exception ex)
        {
            AppLog.Error("Обновление не скопировано", ex);
            ShowState($"Обновление не установлено: {ex.Message}");
        }
    }

    private void OnCancelCopy(object? sender, RoutedEventArgs e) => _copy?.Cancel();

    private async void OnChangeFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await _main.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Папка обновлений" });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } dir) return;
        _main.Settings.UpdateFolder = dir;
        _main.SaveSettings();
        FolderText.Text = dir;
        await Check(quiet: true);
    }
}
