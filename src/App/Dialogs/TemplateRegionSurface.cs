using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Templates;

namespace DocumentTextRecognition.Dialogs;

/// <summary>Разметка в координатах страницы. Масштаб влияет только на рисование и попадание мышью.</summary>
internal sealed class TemplateRegionSurface : Control
{
    private Bitmap? _image;
    private readonly List<TemplateRegion> _regions = [];
    private Point _anchor;
    private NormRect _original;
    private int _dragCorner = -1;
    private bool _dragging;
    private bool _drawing;
    private IReadOnlyList<TemplateRegion>? _beforeDrag;
    private int _beforeSelectedIndex;
    private IPointer? _capturedPointer;

    public event Action? Changed;
    public event Action? SelectionChanged;
    public IReadOnlyList<TemplateRegion> Regions => _regions.ToArray();
    public FieldRole? DrawingRole { get; set; }
    public int SelectedIndex { get; private set; } = -1;
    public bool IsDragging => _dragging;

    public TemplateRegionSurface()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    public void SetPage(Bitmap? image, IReadOnlyList<TemplateRegion> regions)
    {
        _image = image;
        _regions.Clear();
        _regions.AddRange(regions);
        SelectedIndex = -1;
        _dragging = false;
        DrawingRole = null;
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    public void Select(int index)
    {
        SelectedIndex = index >= 0 && index < _regions.Count ? index : -1;
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    public void DeleteSelected()
    {
        if (SelectedIndex < 0) return;
        _regions.RemoveAt(SelectedIndex);
        Select(-1);
        Changed?.Invoke();
    }

    internal static string Label(FieldRole role) => role switch
    {
        FieldRole.Designation => "Обозначение",
        FieldRole.Name => "Наименование",
        FieldRole.Type => "Тип документа",
        _ => "Все поля вместе",
    };

    internal static string BrushKey(FieldRole role) => role switch
    {
        FieldRole.Designation => "Brush.FrameDesignation",
        FieldRole.Name => "Brush.FrameName",
        FieldRole.Type => "Brush.FrameType",
        _ => "Brush.FrameAll",
    };

    internal static Color ColorOf(FieldRole role) =>
        (Application.Current?.FindResource(BrushKey(role)) as ISolidColorBrush)?.Color ?? Colors.Blue;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var page = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.White, page);
        if (_image is not null) context.DrawImage(_image, page);
        for (var i = 0; i < _regions.Count; i++)
        {
            var region = _regions[i];
            var color = ColorOf(region.Role);
            var r = Pixels(region.Bounds);
            var pen = new Pen(new SolidColorBrush(color), i == SelectedIndex ? 3 : 2);
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(24, color.R, color.G, color.B)), pen, r);
            if (i == SelectedIndex)
                foreach (var p in Corners(r))
                    context.DrawRectangle(Brushes.White, pen, new Rect(p.X - 4, p.Y - 4, 8, 8));
        }
    }

    private Rect Pixels(NormRect r) => new(r.X * Bounds.Width, r.Y * Bounds.Height, r.Width * Bounds.Width, r.Height * Bounds.Height);
    private static Point[] Corners(Rect r) => [r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft];
    private Point Normalized(Point p) => new(Math.Clamp(p.X / Math.Max(1, Bounds.Width), 0, 1), Math.Clamp(p.Y / Math.Max(1, Bounds.Height), 0, 1));

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !IsEnabled) return;
        Focus();
        var position = e.GetPosition(this);
        _anchor = Normalized(position);
        _beforeDrag = Regions;
        _beforeSelectedIndex = SelectedIndex;
        _drawing = DrawingRole is not null;
        _dragCorner = -1;
        if (_drawing)
        {
            var role = DrawingRole!.Value;
            _regions.RemoveAll(r => r.Role == role);
            _regions.Add(new TemplateRegion(role, new NormRect(_anchor.X, _anchor.Y, 0, 0)));
            SelectedIndex = _regions.Count - 1;
        }
        else
        {
            if (SelectedIndex >= 0)
            {
                var corners = Corners(Pixels(_regions[SelectedIndex].Bounds));
                for (var i = 0; i < corners.Length; i++)
                    if (Math.Abs(corners[i].X - position.X) <= 10 && Math.Abs(corners[i].Y - position.Y) <= 10) { _dragCorner = i; break; }
            }
            if (_dragCorner < 0)
            {
                SelectedIndex = _regions.FindLastIndex(r => Pixels(r.Bounds).Contains(position));
                if (SelectedIndex < 0) { Select(-1); return; }
            }
            _original = _regions[SelectedIndex].Bounds;
        }
        _dragging = true;
        _capturedPointer = e.Pointer;
        e.Pointer.Capture(this);
        InvalidateVisual();
        SelectionChanged?.Invoke();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging || SelectedIndex < 0) return;
        var point = Normalized(e.GetPosition(this));
        NormRect rect;
        if (_drawing) rect = Between(_anchor, point);
        else if (_dragCorner >= 0)
        {
            var opposite = _dragCorner switch
            {
                0 => new Point(_original.Right, _original.Bottom),
                1 => new Point(_original.X, _original.Bottom),
                2 => new Point(_original.X, _original.Y),
                _ => new Point(_original.Right, _original.Y),
            };
            rect = Between(opposite, point);
        }
        else rect = MoveWithinPage(_original, point.X - _anchor.X, point.Y - _anchor.Y);
        _regions[SelectedIndex] = _regions[SelectedIndex] with { Bounds = rect };
        InvalidateVisual();
        SelectionChanged?.Invoke();
        e.Handled = true;
    }

    internal static NormRect MoveWithinPage(NormRect rect, double dx, double dy) =>
        rect with { X = Math.Clamp(rect.X + dx, 0, 1 - rect.Width), Y = Math.Clamp(rect.Y + dy, 0, 1 - rect.Height) };

    private static NormRect Between(Point a, Point b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        _capturedPointer = null;
        e.Pointer.Capture(null);
        if (SelectedIndex >= 0 && (_regions[SelectedIndex].Bounds.Width * Bounds.Width < 4 || _regions[SelectedIndex].Bounds.Height * Bounds.Height < 4))
        {
            _regions.Clear();
            _regions.AddRange(_beforeDrag ?? []);
            SelectedIndex = -1;
        }
        DrawingRole = null;
        InvalidateVisual();
        SelectionChanged?.Invoke();
        if (!(_beforeDrag ?? []).SequenceEqual(_regions)) Changed?.Invoke();
        _beforeDrag = null;
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (!_dragging) return;
        CancelDrag();
    }

    private void CancelDrag()
    {
        _dragging = false;
        var pointer = _capturedPointer;
        _capturedPointer = null;
        pointer?.Capture(null);
        _regions.Clear();
        _regions.AddRange(_beforeDrag ?? []);
        _beforeDrag = null;
        DrawingRole = null;
        Select(_beforeSelectedIndex);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Delete && !_dragging) { DeleteSelected(); e.Handled = true; }
        else if (e.Key == Key.Escape)
        {
            if (_dragging) CancelDrag();
            else { DrawingRole = null; Select(-1); }
            e.Handled = true;
        }
    }
}
