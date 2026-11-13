using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DocumentTextRecognition.Common;
using DocumentTextRecognition.Dialogs;
using DocumentTextRecognition.Services;

namespace DocumentTextRecognition.Pages;

/// <summary>
/// Раздел «Шаблоны»: слева поиск, список PDF из папки шаблонов (прокрутка внутри рамки),
/// сведения о выбранном и кнопки; справа страница шаблона во всю высоту.
/// </summary>
public partial class TemplatesPage : UserControl
{
    private MainWindow _main = null!;
    private string _folder = "";
    private List<TemplateItem> _items = [];
    private readonly ObservableCollection<TemplateItem> _view = [];
    private CancellationTokenSource? _cts;
    private int _previewToken;

    public TemplatesPage()
    {
        InitializeComponent();
        List.ItemsSource = _view;
        List.SelectionChanged += (_, _) => Select(List.SelectedItem as TemplateItem);
        SearchBox.TextChanged += (_, _) => Filter();
    }

    public void Attach(MainWindow main) => _main = main;

    /// <summary>Папка шаблонов изменилась (раздел «Обработка»): перечитать в фоне.</summary>
    public void SetFolder(string folder)
    {
        if (folder == _folder && _items.Count > 0) return;
        _folder = folder;
        Refresh(force: true);
    }

    public void Refresh(bool force)
    {
        if (!force && _items.Count > 0) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        FolderText.Text = _folder.Length > 0 ? $"Папка шаблонов: {_folder}" : "Папка шаблонов не выбрана";
        _items = TemplateCatalog.List(_folder);
        Filter();
        var folderOk = _folder.Length > 0 && Directory.Exists(_folder);
        Empty.IsVisible = !folderOk || _items.Count == 0;
        Split.IsVisible = !Empty.IsVisible;
        EmptyTitle.Text = _folder.Length == 0 ? "Папка шаблонов не выбрана" : !folderOk ? "Папка шаблонов не найдена" : "В папке шаблонов нет PDF-файлов";
        Report(done: _items.Count == 0);
        if (_items.Count > 0 && List.SelectedItem is null) List.SelectedIndex = 0;

        var items = _items;
        Task.Run(() =>
        {
            foreach (var item in items)
            {
                if (cts.IsCancellationRequested) return;
                try { TemplateCatalog.Inspect(item); }
                catch (Exception ex)
                {
                    item.Recognized = false;
                    item.Sub = $"Не распознан: {ex.Message}";
                    AppLog.Error($"Шаблон {item.FileName} не проверен", ex);
                }
                Dispatcher.UIThread.Post(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    item.Changed();
                    if (List.SelectedItem == item) ShowInfo(item);
                    Report(done: items.All(i => i.Recognized is not null));
                });
            }
        });
    }

    private void Report(bool done)
    {
        var ready = _items.Count(i => i.Recognized == true);
        _main.SetTemplatesCount(done ? ready : null);
        _main.Processing.OnTemplatesInspected(ready, _items.Count, done);
    }

    private void Filter()
    {
        var query = (SearchBox.Text ?? "").Trim();
        var selected = List.SelectedItem;
        _view.Clear();
        foreach (var i in _items)
            if (query.Length == 0 || i.FileName.Contains(query, StringComparison.CurrentCultureIgnoreCase)) _view.Add(i);
        // Выбранный скрыт поиском: выбирается первый видимый.
        List.SelectedItem = selected is TemplateItem t && _view.Contains(t) ? t : _view.FirstOrDefault();
    }

    private void Select(TemplateItem? item)
    {
        ShowInfo(item);
        Preview.Source = null;
        PreviewNote.Text = item is null ? "" : "Загружается…";
        if (item is null) return;
        var token = ++_previewToken;
        var (file, page) = (item.FilePath, item.PageIndex);
        Task.Run(() => TemplateCatalog.RenderPng(file, page)).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (token != _previewToken) return;
            if (t.Result is { } png)
            {
                using var ms = new MemoryStream(png);
                Preview.Source = new Bitmap(ms);
                PreviewNote.Text = "";
            }
            else PreviewNote.Text = "Страница не открывается";
        }));
    }

    private void ShowInfo(TemplateItem? item)
    {
        InfoFile.Text = item?.FileName ?? "";
        InfoSize.Text = item?.PageSize ?? "";
        InfoFrames.Text = item is null ? "" : item.Recognized is null ? "проверяется…" : item.Frames;
        OpenButton.IsEnabled = item is not null;
        CheckButton.IsEnabled = item?.Recognized == true;
    }

    private void OnRefresh(object? sender, RoutedEventArgs e)
    {
        Refresh(force: true);
        _main.Toast("Шаблоны перечитываются");
    }

    private void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (Directory.Exists(_folder)) WindowsShell.OpenFolder(_folder);
        else _main.Toast("Папка шаблонов не найдена");
    }

    private void OnHowTo(object? sender, RoutedEventArgs e) => _ = HowToDialog.Show(_main);

    private async void OnChooseFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await _main.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Папка шаблонов" });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } dir)
        {
            _main.Settings.TemplatesFolder = dir;
            _main.SaveSettings();
            _main.Processing.Reload();
        }
    }

    private void OnOpenTemplate(object? sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is TemplateItem item && File.Exists(item.FilePath)) WindowsShell.Open(item.FilePath);
    }

    private async void OnCheckScan(object? sender, RoutedEventArgs e) =>
        await CheckScanDialog.Show(_main, _folder, _main.Settings.SourceFolder);
}
