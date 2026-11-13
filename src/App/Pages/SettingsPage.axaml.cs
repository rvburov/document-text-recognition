using Avalonia.Controls;
using Avalonia.Interactivity;
using DocumentTextRecognition.Common;
using DocumentTextRecognition.Dialogs;

namespace DocumentTextRecognition.Pages;

/// <summary>Раздел «Настройки»: хранятся в самой программе, в профиле пользователя Windows.</summary>
public partial class SettingsPage : UserControl
{
    private MainWindow _main = null!;
    private bool _loading;
    private static readonly int[] WorkerChoices = [0, 1, 2, 4, 8];

    public SettingsPage() => InitializeComponent();

    public void Attach(MainWindow main)
    {
        _main = main;
        var auto = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        WorkersBox.ItemsSource = WorkerChoices.Select(w => w == 0 ? $"Авто ({auto})" : w.ToString()).ToList();
        Reload();
        WorkersBox.SelectionChanged += (_, _) => Save(s => s.Workers = WorkerChoices[Math.Max(0, WorkersBox.SelectedIndex)]);
        TakenBox.SelectionChanged += (_, _) => Save(s => s.NumberWhenTaken = TakenBox.SelectedIndex == 1);
        RecursiveSwitch.IsCheckedChanged += (_, _) =>
        {
            Save(s => s.Recursive = RecursiveSwitch.IsChecked == true);
            if (!_loading) _main.Processing.Reload();
        };
        NotifySwitch.IsCheckedChanged += (_, _) => Save(s => s.NotifyWhenDone = NotifySwitch.IsChecked == true);
    }

    public void Reload()
    {
        _loading = true;
        var s = _main.Settings;
        WorkersBox.SelectedIndex = Math.Max(0, Array.IndexOf(WorkerChoices, s.Workers));
        TakenBox.SelectedIndex = s.NumberWhenTaken ? 1 : 0;
        RecursiveSwitch.IsChecked = s.Recursive;
        NotifySwitch.IsChecked = s.NotifyWhenDone;
        foreach (var r in FontSegments.Children.OfType<RadioButton>())
            r.IsChecked = (string?)r.Tag == s.FontScale.ToString();
        _loading = false;
    }

    private void Save(Action<AppSettings> change)
    {
        if (_loading) return;
        change(_main.Settings);
        _main.SaveSettings();
    }

    private void OnFontScale(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out var scale))
        {
            Save(s => s.FontScale = scale);
            _main.ApplyFontScale();
        }
    }

    private void OnOpenLog(object? sender, RoutedEventArgs e) => WindowsShell.OpenFolder(AppInfo.LogDir);

    private async void OnReset(object? sender, RoutedEventArgs e)
    {
        var reset = await Ask.Show(_main, "Сбросить настройки",
            "Сбросить все настройки, в том числе выбранные папки и папку обновлений? Файлы и шаблоны не затрагиваются.",
            "Отмена", "Сбросить", danger: true);
        if (!reset) return;
        _main.ReplaceSettings(AppSettings.Reset());
        _main.Toast("Настройки сброшены");
    }
}
