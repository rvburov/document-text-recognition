using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DocumentTextRecognition.Common;
using DocumentTextRecognition.Core.Rename;
using DocumentTextRecognition.Dialogs;
using DocumentTextRecognition.Services;

namespace DocumentTextRecognition.Pages;

/// <summary>
/// Раздел «Обработка»: две папки, «Запустить», ход работы и результат на одном экране.
/// Таблица результата занимает оставшуюся высоту окна (не ниже 300) и прокручивается внутри
/// своей рамки; после «Запустить» раздел прокручивается так, что ход работы стоит вверху.
/// </summary>
public partial class ProcessingPage : UserControl
{
    private MainWindow _main = null!;
    private RunSession? _session;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly List<ResultRow> _all = [];
    private readonly ObservableCollection<ResultRow> _view = [];
    private CancellationTokenSource? _countCts;
    private bool _loading;
    private DateTime _finishedAt;

    private static readonly string[] Filters = ["Все файлы", "Переименованы", "Имя уже верное", "Пропущены", "Ошибки"];

    public ProcessingPage()
    {
        InitializeComponent();
        Table.ItemsSource = _view;
        _timer.Tick += (_, _) => Pump();
    }

    public bool IsRunning => _session?.IsRunning == true;
    public (int Done, int Total) Progress => (_session?.Processed ?? 0, Math.Max(0, _session?.Found ?? 0));

    public void Attach(MainWindow main)
    {
        _main = main;
        Reload();
        TemplatesBox.TextChanged += (_, _) => OnTemplatesChanged();
        SourceBox.TextChanged += (_, _) => OnSourceChanged();
        RecursiveBox.IsCheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _main.Settings.Recursive = RecursiveBox.IsChecked == true;
            _main.SaveSettings();
            CountPdf();
        };
        SearchBox.TextChanged += (_, _) => ApplyFilter();
        StatusBox.SelectionChanged += (_, _) => ApplyFilter();
        Table.SelectionChanged += (_, _) => ShowCard(Table.SelectedItem as ResultRow);
        DropZone.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        DropZone.AddHandler(DragDrop.DropEvent, OnDrop);
        DropZone.AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDropHighlight(false));
        Scroll.PropertyChanged += (_, e) => { if (e.Property == ScrollViewer.ViewportProperty) FitTable(); };
        UpdateStatusItems();
    }

    /// <summary>Подставить папки из настроек (последние выбранные).</summary>
    public void Reload()
    {
        _loading = true;
        TemplatesBox.Text = _main.Settings.TemplatesFolder;
        SourceBox.Text = _main.Settings.SourceFolder;
        RecursiveBox.IsChecked = _main.Settings.Recursive;
        _loading = false;
        OnTemplatesChanged(save: false);
        OnSourceChanged(save: false);
    }

    // ── Папки ─────────────────────────────────────────────────────────────

    private async void OnBrowseTemplates(object? sender, RoutedEventArgs e)
    {
        if (await PickFolder("Папка шаблонов", TemplatesBox.Text) is { } dir) TemplatesBox.Text = dir;
    }

    private async void OnBrowseSource(object? sender, RoutedEventArgs e)
    {
        if (await PickFolder("Папка с PDF", SourceBox.Text) is { } dir) SourceBox.Text = dir;
    }

    private async Task<string?> PickFolder(string title, string? current)
    {
        var start = !string.IsNullOrWhiteSpace(current) && Directory.Exists(current)
            ? await _main.StorageProvider.TryGetFolderFromPathAsync(current) : null;
        var folders = await _main.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title, AllowMultiple = false, SuggestedStartLocation = start,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private void OnTemplatesChanged(bool save = true)
    {
        if (_loading) return;
        var dir = (TemplatesBox.Text ?? "").Trim();
        if (save)
        {
            _main.Settings.TemplatesFolder = dir;
            _main.SaveSettings();
        }
        _main.Templates.SetFolder(dir);
        UpdateHints();
    }

    /// <summary>Шаблоны проверены (раздел «Шаблоны»): сколько распознано.</summary>
    public void OnTemplatesInspected(int ready, int total, bool done)
    {
        var dir = (TemplatesBox.Text ?? "").Trim();
        if (dir.Length == 0) TemplatesNote.Text = "Папка не выбрана";
        else if (!Directory.Exists(dir)) TemplatesNote.Text = "Папка не найдена";
        else if (total == 0) TemplatesNote.Text = "В папке нет PDF-файлов";
        else TemplatesNote.Text = done ? $"Распознано {ready} шаблонов из {total} ·" : $"Шаблонов в папке: {total}, проверяются… ·";
        OpenTemplatesLink.IsVisible = Directory.Exists(dir) && total > 0;
        UpdateHints();
    }

    private void OnOpenTemplates(object? sender, RoutedEventArgs e) => _main.Show("templates");

    private void OnSourceChanged(bool save = true)
    {
        if (_loading) return;
        if (save)
        {
            _main.Settings.SourceFolder = (SourceBox.Text ?? "").Trim();
            _main.SaveSettings();
        }
        CountPdf();
    }

    /// <summary>Число PDF в папке (с подпапками по флажку), в фоне.</summary>
    private void CountPdf()
    {
        _countCts?.Cancel();
        var cts = _countCts = new CancellationTokenSource();
        var dir = (SourceBox.Text ?? "").Trim();
        var recursive = RecursiveBox.IsChecked == true;
        FoundNote.Text = "";
        UpdateHints();
        if (dir.Length == 0) return;
        if (!Directory.Exists(dir))
        {
            FoundNote.Text = "папка не найдена";
            return;
        }
        Task.Run(() =>
        {
            var count = 0;
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true };
                foreach (var f in Directory.EnumerateFiles(dir, "*", options))
                {
                    if (cts.IsCancellationRequested) return;
                    if (f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) count++;
                }
            }
            catch (Exception) { return; }
            Dispatcher.UIThread.Post(() => { if (!cts.IsCancellationRequested) FoundNote.Text = PdfCount(count); });
        });
    }

    internal static string PdfCount(int n)
    {
        var tail = (n % 10, n % 100) switch
        {
            (1, not 11) => "PDF-файл",
            (2 or 3 or 4, not (12 or 13 or 14)) => "PDF-файла",
            _ => "PDF-файлов",
        };
        return $"{n} {tail}";
    }

    private void UpdateHints()
    {
        var templates = (TemplatesBox.Text ?? "").Trim();
        var source = (SourceBox.Text ?? "").Trim();
        StartHint.IsVisible = templates.Length == 0 && source.Length == 0 && _all.Count == 0;
        if (IsRunning) return;
        if (templates.Length == 0 || source.Length == 0)
        {
            RunButton.IsEnabled = false;
            FootText.Text = "Выберите папку шаблонов и папку с PDF";
            EmptyText.Text = "Пока нечего переименовывать. Выберите обе папки: здесь появится таблица, какой файл как переименован и почему пропущен.";
        }
        else
        {
            RunButton.IsEnabled = true;
            FootText.Text = _all.Count > 0 && _finishedAt != default
                ? $"{_finishedAt:dd.MM.yyyy HH:mm} · {source}{(RecursiveBox.IsChecked == true ? " и подпапки" : "")}"
                : "Файлы не удаляются: только переименовываются на месте.";
            EmptyText.Text = "Здесь появится таблица: какой файл как переименован и почему пропущен.";
        }
    }

    private void SetDropHighlight(bool on)
    {
        DropFrame.Stroke = (IBrush?)this.FindResource(on ? "Brush.Accent" : "Brush.Line");
        DropFrame.Fill = on ? (IBrush?)this.FindResource("Brush.AccentLight") : Brushes.Transparent;
        DropText.Text = on ? "Отпустите, чтобы выбрать папку" : "Папку можно перетащить сюда из Проводника";
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var folder = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p is not null && Directory.Exists(p));
        e.DragEffects = folder is null || IsRunning ? DragDropEffects.None : DragDropEffects.Copy;
        SetDropHighlight(folder is not null && !IsRunning);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        SetDropHighlight(false);
        if (IsRunning) return;
        var folder = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p is not null && Directory.Exists(p));
        if (folder is not null) SourceBox.Text = folder;
    }

    private void OnHowTo(object? sender, RoutedEventArgs e) => _ = HowToDialog.Show(_main);

    // ── Запуск и ход работы ───────────────────────────────────────────────

    private async void OnRun(object? sender, RoutedEventArgs e)
    {
        var templates = _main.Settings.TemplatesFolder;
        var source = _main.Settings.SourceFolder;
        if (!Directory.Exists(templates))
        {
            ShowNotice("danger", "OctagonAlert", $"Папка шаблонов не найдена. {templates} недоступна: сетевой диск не подключен или папку переименовали.", "Выбрать другую…", "templates");
            return;
        }
        if (!Directory.Exists(source))
        {
            ShowNotice("danger", "OctagonAlert", $"Папка с PDF не найдена. {source} недоступна.", "Выбрать другую…", "source");
            return;
        }
        var go = await Ask.Show(_main, "Запуск",
            $"Файлы в папке с PDF{(RecursiveBox.IsChecked == true ? " и ее подпапках" : "")} будут переименованы на месте по распознанным обозначению, наименованию и типу документа.",
            "Отмена", "Запустить", secondText: "Содержимое файлов не меняется, ничего не удаляется; файлы, которые не удалось распознать однозначно, остаются с прежними именами.");
        if (!go) return;
        Start();
    }

    /// <summary>Запуск без вопроса (проверки окна).</summary>
    internal void StartWithoutConfirm() => Start();

    private void Start()
    {
        _all.Clear();
        _view.Clear();
        ShowCard(null);
        Notice.IsVisible = false;
        _session = new RunSession();
        SetRunning(true);
        _session.Start(_main.Settings).ContinueWith(_ => Dispatcher.UIThread.Post(Finish), TaskScheduler.Default);
        _timer.Start();
        Dispatcher.UIThread.Post(ScrollToRun, DispatcherPriority.Background);
    }

    private void SetRunning(bool running)
    {
        RunButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        TemplatesBox.IsEnabled = SourceBox.IsEnabled = RecursiveBox.IsEnabled = !running;
        DownloadButton.IsEnabled = !running && _all.Count > 0;
        EmptyResults.IsVisible = !running && _all.Count == 0;
        TableArea.IsVisible = running || _all.Count > 0;
        if (running)
        {
            RunTitle.Text = "Распознавание и переименование… 0%";
            RunTitle.Classes.Remove("success");
            RunTitle.Classes.Remove("warning");
            Bar.Classes.Remove("stopped");
            Bar.Value = 0;
            FootText.Text = $"Папка с PDF: {_main.Settings.SourceFolder}{(_main.Settings.Recursive ? " и подпапки" : "")}";
        }
        FitTable();
    }

    private async void OnStop(object? sender, RoutedEventArgs e)
    {
        if (!IsRunning) return;
        var stop = await Ask.Show(_main, "Остановить",
            "Остановить переименование? Файлы, которые уже в работе, дочитаются и не будут переименованы. Уже переименованные файлы останутся с новыми именами.",
            "Продолжить", "Остановить", danger: true);
        if (!stop || !IsRunning) return;
        _session!.Stop();
        StopButton.IsEnabled = false;
        RunTitle.Text = "Остановка: файлы в работе дочитываются…";
    }

    /// <summary>Остановить и дождаться (закрытие окна во время работы).</summary>
    public async Task StopAndWait()
    {
        _session?.Stop();
        while (IsRunning) await Task.Delay(100);
    }

    private void Pump()
    {
        if (_session is null) return;
        var rows = _session.Drain();
        if (rows.Count > 0)
        {
            _all.AddRange(rows);
            var query = (SearchBox.Text ?? "").Trim();
            foreach (var r in rows)
                if (Fits(r, query, StatusBox.SelectedIndex)) _view.Add(r);
        }
        var found = Math.Max(0, _session.Found);
        CFound.Text = found.ToString();
        CProcessed.Text = _session.Processed.ToString();
        CRenamed.Text = _session.Renamed.ToString();
        CSkipped.Text = _session.Skipped.ToString();
        CErrors.Text = _session.Errors.ToString();
        var part = found > 0 ? _session.Processed / (double)found : 0;
        Bar.Value = part;
        if (_session.IsRunning && !_session.Stopped)
        {
            RunTitle.Text = $"Распознавание и переименование… {Math.Round(part * 100)}%";
            RunRight.Text = Remaining(part) is { } left ? $"осталось около {left} · {Workers()} потока" : $"{Workers()} потока";
        }
        UpdateStatusItems();
    }

    private string Workers() =>
        (_main.Settings.Workers > 0 ? _main.Settings.Workers : Math.Clamp(Environment.ProcessorCount / 2, 1, 4)).ToString();

    private string? Remaining(double part)
    {
        if (_session is null || part < 0.03) return null;
        var left = TimeSpan.FromSeconds(_session.Elapsed.TotalSeconds * (1 - part) / part);
        return left.TotalMinutes >= 1 ? $"{Math.Ceiling(left.TotalMinutes)} мин" : "минуты";
    }

    private void Finish()
    {
        if (_session is null) return;
        _timer.Stop();
        Pump();
        // После окончания строки по алфавиту, как в папке.
        _all.Sort((a, b) => string.Compare(a.Was, b.Was, StringComparison.CurrentCultureIgnoreCase));
        ApplyFilter();
        _finishedAt = DateTime.Now;
        var s = _session;
        SetRunning(false);
        RunRight.Text = $"{_finishedAt:dd.MM.yyyy HH:mm}";
        if (s.Fatal is not null)
        {
            RunTitle.Text = "Не запущено";
            RunTitle.Classes.Add("warning");
            ShowNotice("danger", "OctagonAlert", s.Fatal, s.Fatal.Contains("шаблон") ? "Открыть шаблоны" : null, "open-templates");
        }
        else if (s.Stopped)
        {
            RunTitle.Text = "Остановлено пользователем";
            RunTitle.Classes.Add("warning");
            Bar.Classes.Add("stopped");
            ShowNotice("warning", "TriangleAlert",
                $"Обработано {s.Processed} из {s.Found}. Остальные файлы не тронуты. Запустите снова: уже переименованные файлы будут отмечены «Имя уже верное».");
        }
        else
        {
            RunTitle.Text = "Завершено";
            RunTitle.Classes.Add("success");
            Bar.Value = 1;
            var time = s.Elapsed.TotalMinutes >= 1 ? $"{(int)s.Elapsed.TotalMinutes} мин {s.Elapsed.Seconds} с" : $"{Math.Max(1, s.Elapsed.Seconds)} с";
            var already = s.Already > 0 ? $", у {s.Already} имя уже верное" : "";
            var rest = s.Skipped + s.Errors > 0 ? $" Пропущено {s.Skipped}, ошибок {s.Errors}: эти файлы не тронуты." : "";
            ShowNotice("success", "CheckCheck", $"Завершено за {time}. Переименовано {s.Renamed} из {s.Found}{already}.{rest}");
            _main.Toast($"Готово: переименовано {s.Renamed} из {s.Found}");
        }
        if (s.Fatal is null && _main.Settings.NotifyWhenDone && !_main.IsActive)
            Notifier.Show(_main.NativeHandle, "Распознавание завершено",
                $"Переименовано {s.Renamed} из {s.Found}, пропущено {s.Skipped}, ошибок {s.Errors}.");
        UpdateHints();
        UpdateStatusItems();
    }

    private string? _noticeAction;

    private void ShowNotice(string kind, string icon, string text, string? button = null, string? action = null)
    {
        Notice.Classes.Remove("danger");
        Notice.Classes.Remove("accent");
        if (kind == "danger") Notice.Classes.Add("danger");
        NoticeIcon.Kind = icon;
        NoticeIcon.Foreground = (IBrush?)this.FindResource(kind switch
        {
            "danger" => "Brush.Red", "warning" => "Brush.WarnIcon", _ => "Brush.Green",
        });
        NoticeText.Text = text;
        NoticeButton.Content = button;
        NoticeButton.IsVisible = button is not null;
        _noticeAction = action;
        Notice.IsVisible = true;
        FitTable();
    }

    private async void OnNoticeButton(object? sender, RoutedEventArgs e)
    {
        switch (_noticeAction)
        {
            case "templates":
                if (await PickFolder("Папка шаблонов", null) is { } t) TemplatesBox.Text = t;
                break;
            case "source":
                if (await PickFolder("Папка с PDF", null) is { } s) SourceBox.Text = s;
                break;
            default:
                _main.Show("templates");
                break;
        }
    }

    /// <summary>Ход работы вверху раздела, таблица под ним до низа окна (не ниже 300).</summary>
    private void ScrollToRun()
    {
        FitTable();
        Dispatcher.UIThread.Post(() =>
        {
            var top = RunCard.TranslatePoint(new Point(0, 0), Body)?.Y ?? 0;
            Scroll.Offset = new Vector(0, Math.Max(0, top - 12));
        }, DispatcherPriority.Background);
    }

    private void FitTable()
    {
        if (!TableArea.IsVisible) return;
        var viewport = Scroll.Viewport.Height;
        if (viewport <= 0) return;
        var runTop = RunCard.TranslatePoint(new Point(0, 0), Body)?.Y;
        var tableTop = TableArea.TranslatePoint(new Point(0, 0), Body)?.Y;
        if (runTop is null || tableTop is null) return;
        TableArea.Height = Math.Max(300, viewport - (tableTop.Value - runTop.Value + 12) - 16);
    }

    // ── Таблица, поиск, статусы ───────────────────────────────────────────

    private static bool Fits(ResultRow r, string query, int filter) =>
        r.Matches(query) && filter switch
        {
            1 => r.Kind == StatusKind.Renamed,
            2 => r.Kind == StatusKind.AlreadyNamed,
            3 => r.Kind == StatusKind.Skipped,
            4 => r.Kind == StatusKind.Error,
            _ => true,
        };

    private void ApplyFilter()
    {
        var query = (SearchBox.Text ?? "").Trim();
        var filter = Math.Max(0, StatusBox.SelectedIndex);
        var selected = Table.SelectedItem as ResultRow;
        _view.Clear();
        foreach (var r in _all)
            if (Fits(r, query, filter)) _view.Add(r);
        // Выбранный файл скрыт отбором: карточка переходит на первый видимый файл.
        if (selected is not null)
            Table.SelectedItem = _view.Contains(selected) ? selected : _view.FirstOrDefault();
    }

    private void UpdateStatusItems()
    {
        var index = Math.Max(0, StatusBox.SelectedIndex);
        var counts = new[]
        {
            _all.Count, _all.Count(r => r.Kind == StatusKind.Renamed), _all.Count(r => r.Kind == StatusKind.AlreadyNamed),
            _all.Count(r => r.Kind == StatusKind.Skipped), _all.Count(r => r.Kind == StatusKind.Error),
        };
        var items = Filters.Select((f, i) => _all.Count > 0 ? $"{f} ({counts[i]})" : f).ToList();
        if (StatusBox.Items.Count == items.Count && StatusBox.Items.Cast<object>().Select(o => o.ToString()).SequenceEqual(items)) return;
        _loading = true;
        StatusBox.ItemsSource = items;
        StatusBox.SelectedIndex = index;
        _loading = false;
    }

    private void OnFileTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ResultRow { FullPath: { } path } && File.Exists(path))
        {
            try { WindowsShell.Open(path); }
            catch (Exception ex) { _main.Toast($"Не удалось открыть файл: {ex.Message}"); }
        }
    }

    // ── Карточка файла ────────────────────────────────────────────────────

    private void ShowCard(ResultRow? row)
    {
        FileCard.IsVisible = row is not null;
        if (row is null) return;
        CardTitle.Text = row.Was;
        CardStatus.Text = row.StatusText;
        CardPill.Classes.Clear();
        CardPill.Classes.Add("pill");
        if (row.StatusClass.Length > 0) CardPill.Classes.Add(row.StatusClass);
        CardName.Text = row.IsNamed ? row.NewName : "нет";
        CardReasonRow.IsVisible = !row.IsNamed;
        CardReason.Text = row.Reason;
        CardReason.Foreground = (IBrush?)this.FindResource(row.Kind == StatusKind.Error ? "Brush.Red" : "Brush.Text");
        CardManual.IsVisible = !row.IsNamed;
    }

    private void OnCardOpen(object? sender, RoutedEventArgs e)
    {
        if (Table.SelectedItem is ResultRow { FullPath: { } path } && File.Exists(path)) WindowsShell.Open(path);
        else _main.Toast("Файл не найден");
    }

    private void OnCardShow(object? sender, RoutedEventArgs e)
    {
        if (Table.SelectedItem is ResultRow { FullPath: { } path } && File.Exists(path)) WindowsShell.ShowInFolder(path);
        else _main.Toast("Файл не найден");
    }

    private async void OnCardManual(object? sender, RoutedEventArgs e)
    {
        if (Table.SelectedItem is not ResultRow { FullPath: { } path } row) return;
        var input = await ManualNameDialog.Show(_main, Path.GetFileName(path));
        if (input is null) return;
        var policy = _main.Settings.NumberWhenTaken ? NameTakenPolicy.AddNumber : NameTakenPolicy.Skip;
        var (status, newName, reason, full) = Renamer.Manual(path, input.Value.Designation, input.Value.Name, input.Value.Type, policy);
        if (status == FileStatus.Skipped || newName is null || full is null)
        {
            await Ask.Info(_main, "Имя вручную", $"Файл не переименован: {reason}.");
            return;
        }
        AppLog.Info($"{row.Was}: имя введено вручную, «{newName}»");
        row.ApplyManual(newName, full);
        var index = _view.IndexOf(row);
        if (index >= 0) _view[index] = row;
        Table.SelectedItem = row;
        ShowCard(row);
        UpdateStatusItems();
        _main.Toast("Файл переименован");
    }

    // ── Скачать файл ──────────────────────────────────────────────────────

    private async void OnDownload(object? sender, RoutedEventArgs e)
    {
        var format = await DownloadDialog.Show(_main);
        if (format is null) return;
        var ext = ResultExport.Extension(format.Value);
        var file = await _main.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Скачать файл",
            SuggestedFileName = ResultExport.SuggestedName(DateTime.Now) + ext,
            DefaultExtension = ext.TrimStart('.'),
            FileTypeChoices = [new FilePickerFileType(ext.TrimStart('.').ToUpperInvariant()) { Patterns = ["*" + ext] }],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        try
        {
            ResultExport.Save(path, _all, format.Value);
            _main.Toast("Файл сохранен");
        }
        catch (Exception ex)
        {
            AppLog.Error("Файл результата не сохранен", ex);
            await Ask.Info(_main, "Скачать файл", $"Файл не сохранен: {ex.Message}");
        }
    }
}
