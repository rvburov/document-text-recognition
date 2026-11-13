using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DocumentTextRecognition.Common;

namespace DocumentTextRecognition.Dialogs;

/// <summary>Удаление программы (из «Установленных приложений»): с выбором, удалять ли настройки.</summary>
public sealed class UninstallWindow : Window
{
    public UninstallWindow()
    {
        Title = AppInfo.Title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.White;

        var removeSettings = new CheckBox { Content = "Удалить также настройки" };
        var cancel = new Button { Content = "Отмена", MinWidth = 100 };
        var remove = new Button { Content = "Удалить", MinWidth = 100 };
        remove.Classes.Add("danger");
        var text = new TextBlock { Text = "Программа будет удалена с этого компьютера. Переименованные файлы не затрагиваются.", TextWrapping = TextWrapping.Wrap };

        cancel.Click += (_, _) => Close();
        remove.Click += (_, _) =>
        {
            try
            {
                Installation.Uninstall(AppContext.BaseDirectory.TrimEnd('\\', '/'), removeSettings.IsChecked == true);
                AppLog.Info("Программа удалена");
            }
            catch (Exception ex)
            {
                AppLog.Error("Удаление не удалось", ex);
            }
            Close();
        };

        var head = new StackPanel { Spacing = 2 };
        head.Children.Add(new TextBlock { Text = AppInfo.Title, Classes = { "group" } });
        head.Children.Add(new TextBlock { Text = $"Версия {AppInfo.Version}", Classes = { "muted" } });

        var stack = new StackPanel { Margin = new Thickness(20, 18, 20, 16), Spacing = 12 };
        stack.Children.Add(head);
        stack.Children.Add(text);
        stack.Children.Add(removeSettings);
        stack.Children.Add(Ask.Buttons(cancel, remove));
        Content = stack;
    }
}
