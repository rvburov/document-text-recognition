using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DocumentTextRecognition.Dialogs;

/// <summary>
/// Окна-вопросы шириной 400: текст и две кнопки; отказ слева, действие справа (опасное
/// красной кнопкой). Окна-сообщения с одной кнопкой.
/// </summary>
public static class Ask
{
    public static async Task<bool> Show(Window owner, string title, string text, string cancel, string ok, bool danger = false,
        string? secondText = null)
    {
        var result = false;
        var dialog = Frame(title, 400);
        var okButton = new Button { Content = ok, MinWidth = 100 };
        okButton.Classes.Add(danger ? "danger" : "accent");
        var cancelButton = new Button { Content = cancel, MinWidth = 100 };
        okButton.Click += (_, _) => { result = true; dialog.Close(); };
        cancelButton.Click += (_, _) => dialog.Close();
        dialog.Content = Body(text, secondText, cancelButton, okButton);
        await dialog.ShowDialog(owner);
        return result;
    }

    public static async Task Info(Window owner, string title, string text, string ok = "Понятно", string? secondText = null)
    {
        var dialog = Frame(title, 400);
        var okButton = new Button { Content = ok, MinWidth = 100 };
        okButton.Classes.Add("accent");
        okButton.Click += (_, _) => dialog.Close();
        dialog.Content = Body(text, secondText, okButton);
        await dialog.ShowDialog(owner);
    }

    internal static Window Frame(string title, double width) => new()
    {
        Title = title,
        Width = width,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Background = Brushes.White,
    };

    internal static Control Body(string text, string? second, params Button[] buttons)
    {
        var stack = new StackPanel { Margin = new Thickness(20, 16, 20, 16), Spacing = 10 };
        stack.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 19 });
        if (second is not null) stack.Children.Add(new TextBlock { Text = second, TextWrapping = TextWrapping.Wrap, LineHeight = 19 });
        stack.Children.Add(Buttons(buttons));
        return stack;
    }

    internal static Control Buttons(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var b in buttons) row.Children.Add(b);
        return row;
    }
}
