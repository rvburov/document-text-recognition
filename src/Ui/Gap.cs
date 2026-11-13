using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace DocumentTextRecognition.Ui;

/// <summary>
/// Отступы между столбцами и строками сетки (в Avalonia 11.2 у Grid их нет): ui:Gap.Columns="8"
/// добавляет отступ слева каждому элементу не в первом столбце, ui:Gap.Rows сверху каждому не в
/// первой строке. Для UniformGrid столбец считается по порядку элементов.
/// </summary>
public static class Gap
{
    public static readonly AttachedProperty<double> ColumnsProperty =
        AvaloniaProperty.RegisterAttached<Panel, double>("Columns", typeof(Gap));

    public static readonly AttachedProperty<double> RowsProperty =
        AvaloniaProperty.RegisterAttached<Panel, double>("Rows", typeof(Gap));

    /// <summary>Отступ элемента до добавления промежутка (чтобы пересчет был повторяемым).</summary>
    private static readonly AttachedProperty<Thickness?> BaseMarginProperty =
        AvaloniaProperty.RegisterAttached<Control, Thickness?>("BaseMargin", typeof(Gap));

    static Gap()
    {
        ColumnsProperty.Changed.AddClassHandler<Panel>((p, _) => Hook(p));
        RowsProperty.Changed.AddClassHandler<Panel>((p, _) => Hook(p));
    }

    public static double GetColumns(Panel p) => p.GetValue(ColumnsProperty);
    public static void SetColumns(Panel p, double v) => p.SetValue(ColumnsProperty, v);
    public static double GetRows(Panel p) => p.GetValue(RowsProperty);
    public static void SetRows(Panel p, double v) => p.SetValue(RowsProperty, v);

    private static void Hook(Panel panel)
    {
        panel.AttachedToVisualTree -= OnAttached;
        panel.AttachedToVisualTree += OnAttached;
        panel.Children.CollectionChanged -= (_, _) => Apply(panel);
        panel.Children.CollectionChanged += (_, _) => Apply(panel);
        Apply(panel);
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Panel p) Apply(p);
    }

    private static void Apply(Panel panel)
    {
        var cols = GetColumns(panel);
        var rows = GetRows(panel);
        var uniformColumns = panel is UniformGrid u ? Math.Max(1, u.Columns) : 0;
        for (var i = 0; i < panel.Children.Count; i++)
        {
            if (panel.Children[i] is not Control child) continue;
            var column = uniformColumns > 0 ? i % uniformColumns : Grid.GetColumn(child);
            var row = uniformColumns > 0 ? i / uniformColumns : Grid.GetRow(child);
            var m = child.GetValue(BaseMarginProperty) ?? child.Margin;
            child.SetValue(BaseMarginProperty, m);
            child.Margin = new Thickness(m.Left + (column > 0 ? cols : 0), m.Top + (row > 0 ? rows : 0), m.Right, m.Bottom);
        }
    }
}
