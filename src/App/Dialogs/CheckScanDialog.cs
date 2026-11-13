using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Core.Rename;
using DocumentTextRecognition.Core.Templates;

namespace DocumentTextRecognition.Dialogs;

/// <summary>
/// «Проверить скан»: выбрать скан и посмотреть, какой шаблон подошел и что распознается.
/// Файл не переименовывается: это только проверка разметки.
/// </summary>
public static class CheckScanDialog
{
    public static async Task Show(Window owner, string templatesFolder, string startFolder)
    {
        var dialog = Ask.Frame("Проверка шаблона на скане", 520);
        var path = new TextBox { Classes = { "path" }, Watermark = "Выберите скан PDF…", IsReadOnly = true };
        var browse = new Button { Content = "Обзор…" };
        var check = new Button { Content = "Проверить", MinWidth = 100, IsEnabled = false };
        check.Classes.Add("accent");
        var close = new Button { Content = "Закрыть", MinWidth = 100 };
        var results = new StackPanel();
        var card = new Border { Classes = { "card" }, Child = results, IsVisible = false };
        var status = new TextBlock { Classes = { "hint" }, Text = "Файл не переименовывается: это только проверка разметки." };

        void Row(string key, string value)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*"), MinHeight = 34 };
            grid.Children.Add(new TextBlock { Text = key, VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" } });
            var v = new TextBlock { Text = value, VerticalAlignment = VerticalAlignment.Center, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            Grid.SetColumn(v, 1);
            grid.Children.Add(v);
            results.Children.Add(grid);
        }

        browse.Click += async (_, _) =>
        {
            var start = Directory.Exists(startFolder) ? await owner.StorageProvider.TryGetFolderFromPathAsync(startFolder) : null;
            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Скан для проверки",
                AllowMultiple = false,
                SuggestedStartLocation = start,
                FileTypeFilter = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }],
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } local)
            {
                path.Text = local;
                check.IsEnabled = true;
            }
        };

        check.Click += async (_, _) =>
        {
            var file = path.Text ?? "";
            check.IsEnabled = browse.IsEnabled = false;
            status.Text = "Идет распознавание…";
            results.Children.Clear();
            card.IsVisible = false;
            var (values, problem, template) = await Task.Run(() => Recognize(templatesFolder, file));
            if (values is not null)
            {
                Row("Подошел шаблон", template ?? "");
                Row("Обозначение", values.Designation);
                Row("Наименование", values.Name);
                Row("Тип документа", values.Type);
                var (name, nameProblem) = FileNames.Build(values);
                Row("Станет", name ?? $"не годится: {nameProblem}");
            }
            else
            {
                if (template is not null) Row("Подошел шаблон", template);
                Row("Не распознан", problem ?? "");
            }
            card.IsVisible = true;
            status.Text = "Файл не переименовывается: это только проверка разметки.";
            check.IsEnabled = browse.IsEnabled = true;
        };
        close.Click += (_, _) => dialog.Close();

        var pathRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        browse.Margin = new Thickness(8, 0, 0, 0);
        pathRow.Children.Add(path);
        Grid.SetColumn(browse, 1);
        pathRow.Children.Add(browse);

        var stack = new StackPanel { Margin = new Thickness(20, 16, 20, 16), Spacing = 10 };
        stack.Children.Add(pathRow);
        stack.Children.Add(card);
        stack.Children.Add(status);
        stack.Children.Add(Ask.Buttons(close, check));
        dialog.Content = stack;
        await dialog.ShowDialog(owner);
    }

    private static (FieldValues? Values, string? Problem, string? Template) Recognize(string templatesFolder, string file)
    {
        try
        {
            if (!Directory.Exists(templatesFolder)) return (null, "папка шаблонов не найдена", null);
            var templates = TemplateLibrary.LoadFolder(templatesFolder).Where(l => l.Template is not null).Select(l => l.Template!).ToList();
            try
            {
                if (templates.Count == 0) return (null, "нет ни одного распознанного шаблона", null);
                OcrComponents.RegisterNativeResolver();
                var tessdata = OcrComponents.FindTessdata() ?? throw new FileNotFoundException("нет языковых данных Tesseract");
                using var pair = OcrPair.CreateReal(tessdata);
                var result = DocumentReader.Read(file, templates, pair, CancellationToken.None);
                return (result.Values, result.Problem, result.Template);
            }
            finally
            {
                foreach (var t in templates) t.Dispose();
            }
        }
        catch (Exception ex)
        {
            return (null, $"ошибка проверки: {OcrComponents.Explain(ex)}", null);
        }
    }
}
