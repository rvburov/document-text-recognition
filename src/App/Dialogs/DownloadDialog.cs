using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DocumentTextRecognition.Services;

namespace DocumentTextRecognition.Dialogs;

/// <summary>«Скачать файл»: формат Excel, CSV или текст.</summary>
public static class DownloadDialog
{
    public static async Task<ResultExport.Format?> Show(Window owner)
    {
        ResultExport.Format? result = null;
        var dialog = Ask.Frame("Скачать файл", 520);
        var excel = new RadioButton { Content = "Таблица Excel (.xlsx)", GroupName = "fmt", IsChecked = true, Theme = (Avalonia.Styling.ControlTheme?)owner.FindResource("Segment") };
        var csv = new RadioButton { Content = "CSV", GroupName = "fmt", Theme = excel.Theme };
        var text = new RadioButton { Content = "Текст (.txt)", GroupName = "fmt", Theme = excel.Theme };
        var segments = new Border
        {
            Background = (Avalonia.Media.IBrush?)owner.FindResource("Brush.Divider"),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { excel, csv, text } },
        };
        var ok = new Button { Content = "Скачать", MinWidth = 100 };
        ok.Classes.Add("accent");
        var cancel = new Button { Content = "Отмена", MinWidth = 100 };
        ok.Click += (_, _) =>
        {
            result = csv.IsChecked == true ? ResultExport.Format.Csv : text.IsChecked == true ? ResultExport.Format.Text : ResultExport.Format.Excel;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        var stack = new StackPanel { Margin = new Thickness(20, 16, 20, 16), Spacing = 10 };
        stack.Children.Add(ManualNameDialog.Field("Формат", segments));
        stack.Children.Add(new TextBlock
        {
            Classes = { "hint" },
            Text = "В файле все строки результата: было, стало, статус и причина. Откроется стандартное окно сохранения Windows.",
        });
        stack.Children.Add(Ask.Buttons(cancel, ok));
        dialog.Content = stack;
        await dialog.ShowDialog(owner);
        return result;
    }
}
