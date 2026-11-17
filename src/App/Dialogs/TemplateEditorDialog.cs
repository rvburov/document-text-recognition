using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DocumentTextRecognition.Common;
using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Pdf;
using DocumentTextRecognition.Core.Templates;
using DocumentTextRecognition.Services;

namespace DocumentTextRecognition.Dialogs;

/// <summary>Редактор областей PDF: сохраняет координаты в соседний файл разметки.</summary>
internal sealed class TemplateEditorDialog : Window
{
    private readonly string _file;
    private readonly string _sourceFolder;
    private readonly TemplateRegionSurface _surface = new() { Name = "RegionSurface" };
    private readonly ComboBox _pages = new() { Name = "PageSelector", MinWidth = 120 };
    private readonly ComboBox _zoom = new() { Name = "ZoomSelector", Width = 105, ItemsSource = new[] { "25%", "50%", "75%", "100%", "150%", "200%", "300%" }, SelectedIndex = 2 };
    private readonly CheckBox _typeAbsent = new() { Name = "TypeAbsent", Content = "Тип документа отсутствует: Спецификация" };
    private readonly ListBox _coordinates = new() { Name = "RegionCoordinates", Height = 100 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 22 };
    private readonly TextBlock _hint = new() { Classes = { "hint" }, TextWrapping = TextWrapping.Wrap };
    private readonly Button _save = new() { Name = "SaveTemplate", Content = "Сохранить шаблон", Classes = { "accent" } };
    private readonly Button _check = new() { Name = "CheckTemplate", Content = "Сохранить и проверить…" };
    private readonly Button _delete = new() { Name = "DeleteRegion", Content = "Удалить область", IsEnabled = false };
    private readonly WrapPanel _tools = new();
    private PageSize[] _sizes = [];
    private Bitmap? _bitmap;
    private int _page;
    private bool _dirty;
    private bool _busy;
    private bool _syncing;
    private bool _allowClose;
    private bool _askingClose;
    private bool _loaded;
    private bool _saved;
    private string? _fingerprint;

    internal TemplateEditorDialog(string file, string sourceFolder = "")
    {
        _file = file;
        _sourceFolder = sourceFolder;
        Title = "Редактор шаблона: " + Path.GetFileName(file);
        Width = 1160;
        Height = 720;
        MinWidth = 800;
        MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        ShowInTaskbar = false;

        var grid = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto") };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(new TextBlock { Text = Path.GetFileName(file), FontSize = 16, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var pages = Labelled("Страница", _pages);
        Grid.SetColumn(pages, 1);
        header.Children.Add(pages);
        var zoom = Labelled("Масштаб", _zoom);
        Grid.SetColumn(zoom, 2);
        header.Children.Add(zoom);
        grid.Children.Add(header);

        AddTool("Выбрать / переместить", null);
        AddTool("Синяя · Обозначение", FieldRole.Designation);
        AddTool("Зеленая · Наименование", FieldRole.Name);
        AddTool("Желтая · Тип", FieldRole.Type);
        AddTool("Красная · Все поля", FieldRole.Combined);
        Grid.SetRow(_tools, 1);
        grid.Children.Add(_tools);
        _hint.Margin = new Thickness(0, 5, 0, 8);
        Grid.SetRow(_hint, 2);
        grid.Children.Add(_hint);

        var scroll = new ScrollViewer
        {
            Name = "PageScroll", HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = _surface,
            Background = this.FindResource("Brush.Viewer") as IBrush,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        Grid.SetRow(scroll, 3);
        grid.Children.Add(scroll);

        var details = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        var detailsHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        detailsHeader.Children.Add(new TextBlock { Text = "Области: X, Y от левого верхнего угла; ширина × высота, мм", Classes = { "hint" }, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_delete, 1);
        detailsHeader.Children.Add(_delete);
        details.Children.Add(detailsHeader);
        Grid.SetRow(_coordinates, 1);
        details.Children.Add(_coordinates);
        Grid.SetRow(_typeAbsent, 2);
        details.Children.Add(_typeAbsent);
        Grid.SetRow(details, 4);
        grid.Children.Add(details);

        var cancel = new Button { Content = "Закрыть" };
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 10, 0, 0) };
        _status.Margin = new Thickness(0, 0, 12, 0);
        footer.Children.Add(_status);
        var buttons = Ask.Buttons(cancel, _check, _save);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 5);
        grid.Children.Add(footer);
        Content = grid;

        _surface.Changed += () => { _dirty = true; RefreshRegions(); };
        _surface.SelectionChanged += RefreshSelection;
        _coordinates.SelectionChanged += (_, _) => { if (!_syncing) _surface.Select(_coordinates.SelectedIndex); };
        _delete.Click += (_, _) => _surface.DeleteSelected();
        _pages.SelectionChanged += async (_, _) => await ChangePage();
        _zoom.SelectionChanged += (_, _) => ApplyZoom();
        _typeAbsent.IsCheckedChanged += (_, _) => { if (!_syncing) { _dirty = true; RefreshRegions(); } };
        _save.Click += async (_, _) => { if (await SaveAsync()) { _allowClose = true; Close(_saved); } };
        _check.Click += async (_, _) =>
        {
            if (await SaveAsync())
                await CheckScanDialog.Show(this, Path.GetDirectoryName(_file)!, _sourceFolder, _file);
        };
        cancel.Click += (_, _) => Close(_saved);
        Opened += async (_, _) => await LoadAsync();
        Closing += OnClosing;
        Closed += (_, _) => { _surface.SetPage(null, []); _bitmap?.Dispose(); };
        SetBusy(true);
    }

    public static async Task<bool> Show(Window owner, string file, string sourceFolder = "") =>
        await new TemplateEditorDialog(file, sourceFolder).ShowDialog<bool>(owner);

    private static Control Labelled(string label, Control input) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(12, 0, 0, 0),
        Children = { new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }, input },
    };

    private void AddTool(string title, FieldRole? role)
    {
        var button = new Button { Content = title, Margin = new Thickness(0, 0, 6, 4), Name = role is null ? "SelectRegion" : "Draw" + role };
        if (role is { } r)
            button.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children =
                {
                    new Border { Width = 12, Height = 12, Background = this.FindResource(TemplateRegionSurface.BrushKey(r)) as IBrush, CornerRadius = new CornerRadius(2) },
                    new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center },
                },
            };
        button.Click += (_, _) =>
        {
            _surface.DrawingRole = role;
            UpdateHint();
            _surface.Focus();
        };
        _tools.Children.Add(button);
    }

    private async Task LoadAsync()
    {
        try
        {
            _fingerprint = await Task.Run(() => TemplateMarkupStore.GetFingerprint(_file));
            var data = await Task.Run(() =>
            {
                using var pdf = PdfDocument.Open(_file);
                var sizes = Enumerable.Range(0, pdf.PageCount).Select(pdf.GetPageSize).ToArray();
                var manual = TemplateMarkupStore.Load(_file);
                if (manual.Markup is { } markup && manual.Problem is null)
                    return (Sizes: sizes, Page: markup.PageIndex, Regions: markup.Regions, Absent: markup.TypeAbsent, Problem: (string?)null);
                if (manual.Exists)
                    return (Sizes: sizes, Page: 0, Regions: (IReadOnlyList<TemplateRegion>)Array.Empty<TemplateRegion>(), Absent: false, Problem: manual.Problem);
                var load = TemplateLibrary.Load(_file);
                using (load.Template)
                {
                    // Старая разметка из PDF импортируется в редактируемые координаты.
                    var page = load.Template?.PageIndex ?? 0;
                    IReadOnlyList<ColorFrame> frames;
                    if (load.Template is not null) frames = load.Template.Frames;
                    else
                    {
                        using var render = pdf.RenderPage(page, TemplateLibrary.FrameLongSide);
                        frames = ColorFrames.Detect(render).Frames;
                    }
                    return (Sizes: sizes, Page: page, Regions: (IReadOnlyList<TemplateRegion>)frames.Select(f => new TemplateRegion(f.Role, f.Inner)).ToArray(), Absent: false, Problem: (string?)null);
                }
            });
            _sizes = data.Sizes;
            _page = data.Page;
            _syncing = true;
            _pages.ItemsSource = Enumerable.Range(1, _sizes.Length).Select(n => $"{n} из {_sizes.Length}").ToArray();
            _pages.SelectedIndex = _page;
            _typeAbsent.IsChecked = data.Absent;
            _syncing = false;
            await RenderPage(data.Regions);
            if (_fingerprint != await Task.Run(() => TemplateMarkupStore.GetFingerprint(_file)))
                throw new IOException("PDF изменился во время открытия: закройте редактор и откройте его снова");
            _loaded = true;
            _dirty = false;
            if (data.Problem is not null) SetStatus("Разметка не загружена: " + data.Problem + ". Выделите области заново.", true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось открыть редактор шаблона", ex);
            SetStatus("Не удалось открыть PDF: " + ex.Message, true);
        }
        finally { SetBusy(false); }
    }

    private async Task RenderPage(IReadOnlyList<TemplateRegion> regions)
    {
        var page = _page;
        var png = await Task.Run(() => TemplateCatalog.RenderPng(_file, page, 2400));
        if (png is null) throw new IOException("страница PDF не открывается");
        using var stream = new MemoryStream(png);
        var image = new Bitmap(stream);
        var old = _bitmap;
        _bitmap = image;
        _surface.SetPage(image, regions);
        old?.Dispose();
        ApplyZoom();
        RefreshRegions();
    }

    private async Task ChangePage()
    {
        if (_syncing || !_loaded || _pages.SelectedIndex < 0 || _pages.SelectedIndex == _page) return;
        var target = _pages.SelectedIndex;
        SetBusy(true);
        try
        {
            if (_surface.Regions.Count > 0 && !await Ask.Show(this, "Другая страница шаблона",
                    "Один шаблон хранит области одной страницы. При переходе выделения текущей страницы будут очищены. Сохраненная разметка изменится только после нажатия «Сохранить».", "Остаться", "Перейти"))
            {
                _syncing = true;
                _pages.SelectedIndex = _page;
                _syncing = false;
                return;
            }
            var oldPage = _page;
            _page = target;
            try { await RenderPage([]); }
            catch { _page = oldPage; _syncing = true; _pages.SelectedIndex = _page; _syncing = false; throw; }
            _dirty = true;
        }
        catch (Exception ex) { SetStatus("Не удалось открыть страницу: " + ex.Message, true); }
        finally { SetBusy(false); }
    }

    private void ApplyZoom()
    {
        if (_sizes.Length == 0) return;
        double[] scales = [0.25, 0.5, 0.75, 1, 1.5, 2, 3];
        var scale = scales[Math.Clamp(_zoom.SelectedIndex, 0, scales.Length - 1)] * 96 / 72;
        _surface.Width = _sizes[_page].Width * scale;
        _surface.Height = _sizes[_page].Height * scale;
        _surface.InvalidateVisual();
    }

    private void RefreshRegions()
    {
        if (_sizes.Length == 0) return;
        _syncing = true;
        _coordinates.ItemsSource = _surface.Regions.Select(r =>
        {
            var b = r.Bounds;
            var s = _sizes[_page];
            return $"{TemplateRegionSurface.Label(r.Role)}  ·  стр. {_page + 1}  ·  X {b.X * s.WidthMm:F1}  Y {b.Y * s.HeightMm:F1}  ·  {b.Width * s.WidthMm:F1} × {b.Height * s.HeightMm:F1} мм";
        }).ToArray();
        _coordinates.SelectedIndex = _surface.SelectedIndex;
        _syncing = false;
        var problem = TemplateMarkupStore.Validate(_surface.Regions, _typeAbsent.IsChecked == true);
        SetStatus(problem ?? "Области готовы к сохранению.", problem is not null);
        UpdateEnabled();
        UpdateHint();
    }

    private void RefreshSelection() => RefreshRegions();

    private void UpdateHint() => _hint.Text = _surface.DrawingRole is { } role
        ? $"{TemplateRegionSurface.Label(role)}: протяните мышью прямоугольник вокруг текста. Новое выделение заменит прежнюю область этого поля."
        : "Выберите цвет и выделите текст. Для перемещения тяните область, для изменения размера тяните угловой маркер. Цвет служит подсказкой; сохраняются координаты.";

    private async Task<bool> SaveAsync()
    {
        if (_busy || !_loaded) return false;
        var regions = _surface.Regions;
        var absent = _typeAbsent.IsChecked == true;
        SetBusy(true);
        try
        {
            var error = await Task.Run(() => TemplateMarkupStore.Save(_file, _page, regions, absent, _fingerprint));
            if (error is not null) { SetStatus("Не удалось сохранить: " + error, true); return false; }
            _dirty = false;
            _saved = true;
            SetStatus("Разметка сохранена рядом с PDF.", false);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось сохранить разметку шаблона", ex);
            SetStatus("Не удалось сохранить: " + ex.Message, true);
            return false;
        }
        finally { SetBusy(false); }
    }

    private void SetStatus(string text, bool error)
    {
        _status.Text = text;
        _status.Foreground = this.FindResource(error ? "Brush.Red" : "Brush.Green") as IBrush;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _surface.IsEnabled = !busy && _loaded;
        _pages.IsEnabled = !busy && _loaded;
        _zoom.IsEnabled = !busy && _loaded;
        _tools.IsEnabled = !busy && _loaded;
        _typeAbsent.IsEnabled = !busy && _loaded;
        UpdateEnabled();
    }

    private void UpdateEnabled()
    {
        var valid = _loaded && _bitmap is not null && TemplateMarkupStore.Validate(_surface.Regions, _typeAbsent.IsChecked == true) is null;
        _save.IsEnabled = _check.IsEnabled = !_busy && valid;
        _delete.IsEnabled = !_busy && _surface.SelectedIndex >= 0;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        if (_busy) { e.Cancel = true; return; }
        if (!_dirty) return;
        e.Cancel = true;
        if (_askingClose) return;
        _askingClose = true;
        try
        {
            if (await Ask.Show(this, "Несохраненная разметка", "Закрыть редактор и потерять несохраненные изменения?", "Продолжить редактирование", "Закрыть"))
            {
                _allowClose = true;
                Close(_saved);
            }
        }
        finally { _askingClose = false; }
    }
}
