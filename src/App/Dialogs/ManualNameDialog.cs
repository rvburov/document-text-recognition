using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Core.Rename;

namespace DocumentTextRecognition.Dialogs;

/// <summary>
/// «Ввести имя вручную» для пропущенного файла или файла с ошибкой: обозначение, наименование и
/// тип документа; имя проверяется по тем же правилам, что у распознанного.
/// </summary>
public static class ManualNameDialog
{
    /// <summary>Возвращает (обозначение, наименование, тип) или null, если отменено.</summary>
    public static async Task<(string Designation, string Name, string Type)?> Show(Window owner, string fileName)
    {
        (string, string, string)? result = null;
        var dialog = Ask.Frame("Имя вручную", 520);
        var designation = new TextBox { Watermark = "Например, АБВГ.301121.005" };
        var name = new TextBox { Watermark = "Например, Корпус" };
        var type = new TextBox { Text = DocTypes.WhenMissing };
        var preview = new TextBlock { Classes = { "hint" } };
        var ok = new Button { Content = "Переименовать", MinWidth = 120 };
        ok.Classes.Add("accent");
        var cancel = new Button { Content = "Отмена", MinWidth = 100 };

        void Update()
        {
            var (file, problem) = FileNames.Build(new FieldValues(designation.Text ?? "", name.Text ?? "", type.Text ?? ""));
            preview.Text = file is not null ? $"Файл «{fileName}» получит имя «{file}»." : $"Имя пока не годится: {problem}.";
            preview.Foreground = file is not null ? (IBrush?)owner.FindResource("Brush.Muted") : (IBrush?)owner.FindResource("Brush.Red");
            ok.IsEnabled = file is not null;
        }
        designation.TextChanged += (_, _) => Update();
        name.TextChanged += (_, _) => Update();
        type.TextChanged += (_, _) => Update();
        ok.Click += (_, _) => { result = (designation.Text!.Trim(), name.Text!.Trim(), type.Text!.Trim()); dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();

        var stack = new StackPanel { Margin = new Thickness(20, 16, 20, 16), Spacing = 10 };
        stack.Children.Add(Field("Обозначение", designation));
        stack.Children.Add(Field("Наименование", name));
        stack.Children.Add(Field("Тип документа", type));
        stack.Children.Add(preview);
        stack.Children.Add(Ask.Buttons(cancel, ok));
        dialog.Content = stack;
        Update();
        dialog.Opened += (_, _) => designation.Focus();
        await dialog.ShowDialog(owner);
        return result;
    }

    internal static Control Field(string label, Control input)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.Medium });
        stack.Children.Add(input);
        return stack;
    }
}
