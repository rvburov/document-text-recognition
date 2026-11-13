using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using DocumentTextRecognition.Common;
using DocumentTextRecognition.Dialogs;

namespace DocumentTextRecognition;

public partial class MainWindow : Window
{
    /// <summary>Настройки программы (одни на окно, сохраняются при изменении).</summary>
    public AppSettings Settings { get; private set; } = AppSettings.Load();

    private DispatcherTimer? _toastTimer;
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Версия {AppInfo.Version}";
        ApplyFontScale();
        Templates.Attach(this);
        Processing.Attach(this);
        SettingsView.Attach(this);
        About.Attach(this);
        Opened += async (_, _) => await About.CheckOnStartup();
        Closing += OnClosing;
    }

    public void SaveSettings() => Settings.Save();

    public void ReplaceSettings(AppSettings settings)
    {
        Settings = settings;
        Settings.Save();
        ApplyFontScale();
        Processing.Reload();
        SettingsView.Reload();
        About.Reload();
    }

    /// <summary>Размер шрифта: все окно масштабируется пропорционально.</summary>
    public void ApplyFontScale()
    {
        var scale = Math.Clamp(Settings.FontScale, 100, 150) / 100.0;
        Zoom.LayoutTransform = scale == 1.0 ? null : new ScaleTransform(scale, scale);
    }

    public void SetTemplatesCount(int? ready) => TemplatesCount.Text = ready is > 0 ? ready.ToString() : "";

    private void OnNav(object? sender, RoutedEventArgs e) => Show(sender == NavTemplates ? "templates"
        : sender == NavSettings ? "settings" : sender == NavAbout ? "about" : "processing");

    public void Show(string page)
    {
        Processing.IsVisible = page == "processing";
        Templates.IsVisible = page == "templates";
        SettingsView.IsVisible = page == "settings";
        About.IsVisible = page == "about";
        NavProcessing.IsChecked = page == "processing";
        NavTemplates.IsChecked = page == "templates";
        NavSettings.IsChecked = page == "settings";
        NavAbout.IsChecked = page == "about";
        if (page == "templates") Templates.Refresh(force: false);
    }

    /// <summary>Короткое сообщение вверху окна на две секунды.</summary>
    public void Toast(string text)
    {
        ToastText.Text = text;
        ToastBox.IsVisible = true;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        _toastTimer.Tick += (_, _) => { ToastBox.IsVisible = false; _toastTimer?.Stop(); };
        _toastTimer.Start();
    }

    /// <summary>Закрытие во время работы: спросить, остановить прогон.</summary>
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || !Processing.IsRunning) return;
        e.Cancel = true;
        var (done, total) = Processing.Progress;
        var close = await Ask.Show(this, "Закрыть программу",
            $"Идет переименование: обработано {done} из {total}. Закрыть программу? Работа остановится так же, как по «Стоп».",
            "Не закрывать", "Закрыть", danger: true);
        if (!close) return;
        await Processing.StopAndWait();
        _closeConfirmed = true;
        Close();
    }

    public IntPtr NativeHandle => TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
}
