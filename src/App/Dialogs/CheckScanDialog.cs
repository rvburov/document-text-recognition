using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using System.Globalization;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Core.Rename;
using DocumentTextRecognition.Core.Templates;

namespace DocumentTextRecognition.Dialogs;

/// <summary>Проверка скана: выбранный шаблон, фрагменты полей и ответы обоих OCR. Только чтение.</summary>
public static class CheckScanDialog
{
    public static async Task Show(Window owner, string templatesFolder, string startFolder, string? selectedTemplatePath = null)
    {
        var dialog = Ask.Frame("Проверка шаблона на скане", 820);
        dialog.SizeToContent = SizeToContent.Manual;
        dialog.Height = 720;
        dialog.MinWidth = 620;
        dialog.MinHeight = 440;
        dialog.CanResize = true;
        var path = new TextBox { Classes = { "path" }, Watermark = "Выберите скан PDF…", IsReadOnly = true };
        var browse = new Button { Content = "Обзор…" };
        var check = new Button { Content = "Проверить", MinWidth = 100, IsEnabled = false };
        check.Classes.Add("accent");
        var close = new Button { Content = "Закрыть", MinWidth = 100 };
        var results = new StackPanel { Spacing = 10 };
        var card = new Border { Classes = { "card" }, Child = results, IsVisible = false };
        const string readOnly = "Проверка только читает документ. Предлагаемое имя показано ниже.";
        var status = Text(readOnly);
        status.Classes.Add("hint");
        var bitmaps = new List<Bitmap>();
        using var cancellation = new CancellationTokenSource();
        Task<CheckReport>? running = null;
        var closed = false;

        void ClearResults()
        {
            results.Children.Clear();
            foreach (var bitmap in bitmaps) bitmap.Dispose();
            bitmaps.Clear();
        }

        void Row(string key, string value)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("160,*"), MinHeight = 30 };
            grid.Children.Add(new TextBlock { Text = key, VerticalAlignment = VerticalAlignment.Top, Classes = { "muted" } });
            var v = Text(value);
            Grid.SetColumn(v, 1);
            grid.Children.Add(v);
            results.Children.Add(grid);
        }

        void ShowDiagnostic(RecognitionDiagnostic item)
        {
            var page = item.PageNumber > 0 ? $"Стр. {item.PageNumber} · " : "";
            if (item.Field is not { } field)
            {
                results.Children.Add(Text($"{page}{item.Stage}: {item.Message}"));
                return;
            }
            var content = new StackPanel { Spacing = 8 };
            using (var stream = new MemoryStream(field.CropPng))
            {
                var bitmap = new Bitmap(stream);
                bitmaps.Add(bitmap);
                content.Children.Add(new Image { Source = bitmap, MaxHeight = 180, Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Left });
            }
            if (field.First.MinConfidence is not null || field.First.RequiredConfidence is not null ||
                field.Second.MinConfidence is not null || field.Second.RequiredConfidence is not null)
            {
                var explanation = Text("Шкалы уверенности движков различаются; это не вероятность правильного ответа.");
                explanation.Classes.Add("hint");
                content.Children.Add(explanation);
            }
            foreach (var engine in new[] { field.First, field.Second })
            {
                content.Children.Add(new TextBlock { Text = engine.Engine, FontWeight = FontWeight.SemiBold });
                content.Children.Add(new SelectableTextBlock
                {
                    Text = string.IsNullOrEmpty(engine.RawText) ? "Текст не найден" : engine.RawText,
                    TextWrapping = TextWrapping.Wrap,
                });
                if (DescribeConfidence(engine) is { } confidence)
                    content.Children.Add(Text(confidence));
                content.Children.Add(Text(engine.Problem is { } problem
                    ? $"Проверка поля: {problem}"
                    : $"После проверки: {(engine.ParsedText.Length == 0 ? "пустое поле" : engine.ParsedText)}"));
            }
            results.Children.Add(new Expander
            {
                Header = $"{page}{item.Message}", Content = content, IsExpanded = true,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            });
        }

        browse.Click += async (_, _) =>
        {
            try
            {
                var start = Directory.Exists(startFolder) ? await owner.StorageProvider.TryGetFolderFromPathAsync(startFolder) : null;
                var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Скан для проверки",
                    AllowMultiple = false,
                    SuggestedStartLocation = start,
                    FileTypeFilter = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }],
                });
                if (!closed && files.Count > 0 && files[0].TryGetLocalPath() is { } local)
                {
                    path.Text = local;
                    check.IsEnabled = true;
                }
            }
            catch (Exception ex) { if (!closed) status.Text = $"Не удалось выбрать PDF: {ex.Message}"; }
        };

        check.Click += async (_, _) =>
        {
            var file = path.Text ?? "";
            check.IsEnabled = browse.IsEnabled = false;
            status.Text = "Идет распознавание…";
            ClearResults();
            card.IsVisible = false;
            try
            {
                running = Task.Run(() => Recognize(templatesFolder, file, selectedTemplatePath, cancellation.Token));
                var report = await running;
                if (closed) return;
                var result = report.Result;
                if (result.Template is not null) Row("Подошел шаблон", result.Template);
                if (result.Values is { } values)
                {
                    Row("Обозначение", values.Designation);
                    Row("Наименование", values.Name);
                    Row("Тип документа", values.Type);
                    var (name, nameProblem) = FileNames.Build(values);
                    Row("Предлагаемое имя", name ?? $"не годится: {nameProblem}");
                }
                else Row("Не распознан", result.Problem ?? "Причина не указана");
                foreach (var diagnostic in report.Diagnostics) ShowDiagnostic(diagnostic);
                card.IsVisible = true;
                status.Text = readOnly;
            }
            catch (Exception ex) { if (!closed) status.Text = $"Ошибка проверки: {OcrComponents.Explain(ex)}"; }
            finally { if (!closed) check.IsEnabled = browse.IsEnabled = true; }
        };
        close.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) =>
        {
            closed = true;
            cancellation.Cancel();
            ClearResults();
        };

        var pathRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        browse.Margin = new Thickness(8, 0, 0, 0);
        pathRow.Children.Add(path);
        Grid.SetColumn(browse, 1);
        pathRow.Children.Add(browse);

        var body = new Grid { Margin = new Thickness(20, 16), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto") };
        var selected = Text(selectedTemplatePath is null
            ? "Шаблон подбирается из папки шаблонов."
            : $"Проверяется шаблон: {Path.GetFileName(selectedTemplatePath)}");
        selected.Margin = new Thickness(0, 0, 0, 10);
        body.Children.Add(selected);
        Grid.SetRow(pathRow, 1);
        body.Children.Add(pathRow);
        var scroll = new ScrollViewer { Content = card, Margin = new Thickness(0, 12, 0, 12),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 2);
        body.Children.Add(scroll);
        Grid.SetRow(status, 3);
        body.Children.Add(status);
        var buttons = Ask.Buttons(close, check);
        Grid.SetRow(buttons, 4);
        body.Children.Add(buttons);
        dialog.Content = body;
        await dialog.ShowDialog(owner);
        if (running is not null) await running;
    }

    private static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    internal static string? DescribeConfidence(EngineDiagnostic engine)
    {
        if (engine.MinConfidence is null && engine.RequiredConfidence is null) return null;
        var score = engine.MinConfidence;
        var threshold = engine.RequiredConfidence;
        var format = "0.##";
        if (IsScore(score) && IsScore(threshold) && score != threshold &&
            Percent(score!.Value, format) == Percent(threshold!.Value, format))
            format = "0.##########";
        var actual = string.IsNullOrWhiteSpace(engine.RawText) ? "нет распознанных строк"
            : IsScore(score) ? Percent(score!.Value, format) : "оценка не получена";
        var required = IsScore(threshold) ? $"; требуется не менее {Percent(threshold!.Value, format)}" : "";
        return $"Уверенность (минимум по строкам): {actual}{required}.";

        static bool IsScore(double? value) => value is { } number && double.IsFinite(number) && number is >= 0 and <= 1;
        static string Percent(double value, string format) =>
            (value * 100).ToString(format, CultureInfo.GetCultureInfo("ru-RU")) + " %";
    }

    internal sealed record CheckReport(DocumentResult Result, IReadOnlyList<RecognitionDiagnostic> Diagnostics);

    internal static CheckReport Recognize(string templatesFolder, string file, string? selectedTemplatePath = null,
        CancellationToken ct = default)
    {
        var diagnostics = new List<RecognitionDiagnostic>();
        var templates = new List<DocTemplate>();
        CheckReport Fail(string problem) => new(DocumentResult.Fail(problem), diagnostics);
        try
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<TemplateLoad> loads;
            if (selectedTemplatePath is not null)
                loads = [TemplateLibrary.Load(selectedTemplatePath)];
            else
            {
                if (!Directory.Exists(templatesFolder)) return Fail("папка шаблонов не найдена");
                loads = TemplateLibrary.LoadFolder(templatesFolder, ct);
            }
            foreach (var load in loads)
            {
                if (load.Template is { } template) templates.Add(template);
                else diagnostics.Add(new("Области шаблона", 0, $"{load.FileName}: {load.Problem}"));
            }
            if (templates.Count == 0)
                return Fail(selectedTemplatePath is not null
                    ? $"Шаблон не готов: {loads[0].Problem}"
                    : "нет ни одного пригодного шаблона; откройте редактор и сохраните области");
            ct.ThrowIfCancellationRequested();
            OcrComponents.RegisterNativeResolver();
            var tessdata = OcrComponents.FindTessdata() ?? throw new FileNotFoundException("нет языковых данных Tesseract");
            using var pair = OcrPair.CreateReal(tessdata);
            var result = DocumentReader.Read(file, templates, pair, ct, diagnostics.Add);
            return new CheckReport(result, diagnostics);
        }
        catch (OperationCanceledException) { return Fail("Проверка остановлена."); }
        catch (Exception ex) { return Fail($"ошибка проверки: {OcrComponents.Explain(ex)}"); }
        finally { foreach (var template in templates) template.Dispose(); }
    }
}
