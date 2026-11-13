using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace DocumentTextRecognition.Ui;

/// <summary>
/// Значок из макета: контур на сетке 24 × 24, выводится размером 16 с линией 1,9 (в единицах
/// сетки), цвет как у текста вокруг. «Play» и «Stop» залитые, того же веса.
/// </summary>
public sealed class Icon : Viewbox
{
    public static readonly StyledProperty<string> KindProperty =
        AvaloniaProperty.Register<Icon, string>(nameof(Kind), "");

    /// <summary>Цвет значка (по умолчанию цвет текста вокруг).</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<Icon>();

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public string Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    private readonly Path _path = new() { StrokeThickness = 1.9, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round };

    public Icon()
    {
        Width = 16;
        Height = 16;
        Stretch = Stretch.Uniform;
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(_path);
        Child = canvas;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty || change.Property == TextElement.ForegroundProperty) Update();
    }

    protected override void OnAttachedToLogicalTree(Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        Update();
    }

    private void Update()
    {
        var brush = GetValue(TextElement.ForegroundProperty);
        if (this.TryFindResource("IconFill." + Kind, out var filled) && filled is Geometry fg)
        {
            _path.Data = fg;
            _path.Fill = brush;
            _path.Stroke = null;
        }
        else if (this.TryFindResource("Icon." + Kind, out var stroked) && stroked is Geometry sg)
        {
            _path.Data = sg;
            _path.Stroke = brush;
            _path.Fill = null;
        }
    }
}
