using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

using NeoPrint.Kiosk.ViewModels;
using NeoPrint.Layout;

namespace NeoPrint.Kiosk.Views;

/// <summary>
/// The paper of screen 4a (docs/DESIGN_SYSTEM.md, "Səhifədə yerləşdir"):
/// the margin, the pictures as they will print, the selection and the snap
/// lines. One finger selects and drags; two fingers make the selected picture
/// bigger or smaller. Every change goes through the view model, which applies
/// the shared editing rules (<see cref="LayoutEditing"/>).
/// </summary>
/// <remarks>Colours are the design tokens, looked up by key: nothing is hard-coded here.</remarks>
public sealed class LayoutCanvas : Control
{
    public static readonly StyledProperty<LayoutProject?> ProjectProperty =
        AvaloniaProperty.Register<LayoutCanvas, LayoutProject?>(nameof(Project));

    public static readonly StyledProperty<byte[]?> PictureProperty =
        AvaloniaProperty.Register<LayoutCanvas, byte[]?>(nameof(Picture));

    public static readonly StyledProperty<int> SelectedProperty =
        AvaloniaProperty.Register<LayoutCanvas, int>(nameof(Selected), -1);

    public static readonly StyledProperty<IReadOnlyList<SnapLine>?> LinesProperty =
        AvaloniaProperty.Register<LayoutCanvas, IReadOnlyList<SnapLine>?>(nameof(Lines));

    // A finger's travel under this, in screen pixels, is a tap, not a drag.
    private const double TapSlop = 8;

    private readonly Dictionary<long, Point> _pointers = [];
    private Bitmap? _bitmap;
    private Point _origin;
    private double _pixelsPerMm = 1;

    private LayoutElement? _gestureStart;
    private Point _dragFrom;
    private bool _dragging;
    private double _pinchFrom;

    static LayoutCanvas()
    {
        AffectsRender<LayoutCanvas>(ProjectProperty, PictureProperty, SelectedProperty, LinesProperty);
        ClipToBoundsProperty.OverrideDefaultValue<LayoutCanvas>(true);
    }

    public LayoutProject? Project
    {
        get => GetValue(ProjectProperty);
        set => SetValue(ProjectProperty, value);
    }

    public byte[]? Picture
    {
        get => GetValue(PictureProperty);
        set => SetValue(PictureProperty, value);
    }

    public int Selected
    {
        get => GetValue(SelectedProperty);
        set => SetValue(SelectedProperty, value);
    }

    public IReadOnlyList<SnapLine>? Lines
    {
        get => GetValue(LinesProperty);
        set => SetValue(LinesProperty, value);
    }

    private PrintFlowViewModel? Flow => DataContext as PrintFlowViewModel;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == PictureProperty)
        {
            _bitmap?.Dispose();
            _bitmap = Picture is { } bytes ? new Bitmap(new MemoryStream(bytes)) : null;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _pointers.Clear();
    }

    public override void Render(DrawingContext context)
    {
        if (Project is not { } project)
        {
            return;
        }

        var (widthMm, heightMm) = project.PageSizeMm();
        _pixelsPerMm = Math.Min(Bounds.Width / widthMm, Bounds.Height / heightMm);
        _origin = new Point((Bounds.Width - (widthMm * _pixelsPerMm)) / 2, (Bounds.Height - (heightMm * _pixelsPerMm)) / 2);

        var paper = new Rect(_origin, new Size(widthMm * _pixelsPerMm, heightMm * _pixelsPerMm));
        context.DrawRectangle(Brush("Neutral0"), new Pen(Brush("Neutral200"), 1), paper);

        var margin = project.MarginMm * _pixelsPerMm;
        context.DrawRectangle(
            new Pen(Brush("Neutral400"), 1, new DashStyle([4, 4], 0)),
            paper.Deflate(margin));

        var elements = project.Pages[0].Elements;

        for (var index = 0; index < elements.Count; index++)
        {
            DrawPicture(context, elements[index]);
        }

        if (Selected >= 0 && Selected < elements.Count)
        {
            var element = elements[Selected];
            var warn = LayoutGeometry.LeavesPrintableArea(project, element);
            DrawOutline(context, element, new Pen(Brush(warn ? "Warning500" : "Brand500"), 3));
        }

        var snap = new Pen(Brush("Brand500"), 1);

        foreach (var line in Lines ?? [])
        {
            var at = line.Mm * _pixelsPerMm;
            context.DrawLine(
                snap,
                line.Vertical ? new Point(paper.X + at, paper.Top) : new Point(paper.Left, paper.Y + at),
                line.Vertical ? new Point(paper.X + at, paper.Bottom) : new Point(paper.Right, paper.Y + at));
        }
    }

    private void DrawPicture(DrawingContext context, LayoutElement element)
    {
        using (context.PushTransform(Placement(element)))
        {
            var box = Box(element);

            if (_bitmap is { } bitmap)
            {
                context.DrawImage(bitmap, new Rect(bitmap.Size), box);
            }
            else
            {
                context.DrawRectangle(Brush("Neutral100"), null, box);
            }
        }
    }

    private void DrawOutline(DrawingContext context, LayoutElement element, Pen pen)
    {
        using (context.PushTransform(Placement(element)))
        {
            context.DrawRectangle(pen, Box(element));
        }
    }

    // The picture's own box, centred on the origin; Placement turns and moves it.
    private Rect Box(LayoutElement element)
    {
        var width = element.WidthMm * _pixelsPerMm;
        var height = element.HeightMm * _pixelsPerMm;
        return new Rect(-width / 2, -height / 2, width, height);
    }

    private Matrix Placement(LayoutElement element)
    {
        var centre = ToScreen(element.XMm + (element.WidthMm / 2), element.YMm + (element.HeightMm / 2));
        return Matrix.CreateRotation(element.RotationDeg * Math.PI / 180) * Matrix.CreateTranslation(centre.X, centre.Y);
    }

    private Point ToScreen(double xMm, double yMm) => new(_origin.X + (xMm * _pixelsPerMm), _origin.Y + (yMm * _pixelsPerMm));

    private (double X, double Y) ToMm(Point point) => ((point.X - _origin.X) / _pixelsPerMm, (point.Y - _origin.Y) / _pixelsPerMm);

    private IBrush Brush(string key)
        => this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Transparent;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var point = e.GetPosition(this);
        _pointers[e.Pointer.Id] = point;
        e.Pointer.Capture(this);
        e.Handled = true;

        if (_pointers.Count == 1)
        {
            // The picture under the finger is selected straight away, so a
            // drag can start on any picture, not just the selected one.
            var (x, y) = ToMm(point);
            Flow?.LayoutSelectAt(x, y);
            _gestureStart = Flow?.LayoutSelectedElement;
            _dragFrom = point;
            _dragging = false;
        }
        else if (_pointers.Count == 2)
        {
            _gestureStart = Flow?.LayoutSelectedElement;
            _pinchFrom = Spread();
            _dragging = false;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!_pointers.ContainsKey(e.Pointer.Id) || Flow is not { } flow || _gestureStart is not { } start)
        {
            return;
        }

        var point = e.GetPosition(this);
        _pointers[e.Pointer.Id] = point;

        if (_pointers.Count == 1)
        {
            var delta = point - _dragFrom;

            if (!_dragging && Math.Abs(delta.X) < TapSlop && Math.Abs(delta.Y) < TapSlop)
            {
                return;
            }

            _dragging = true;
            flow.LayoutDrag(start, delta.X / _pixelsPerMm, delta.Y / _pixelsPerMm);
        }
        else if (_pointers.Count == 2 && _pinchFrom > 0)
        {
            flow.LayoutPinch(start, Spread() / _pinchFrom);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        EndPointer(e.Pointer);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndPointer(e.Pointer);
    }

    private void EndPointer(IPointer pointer)
    {
        if (!_pointers.Remove(pointer.Id))
        {
            return;
        }

        // Lifting one of two fingers carries on as a drag from where the other is.
        if (_pointers.Count == 1)
        {
            _gestureStart = Flow?.LayoutSelectedElement;
            _dragFrom = _pointers.Values.Single();
            _dragging = false;
        }
        else if (_pointers.Count == 0)
        {
            _gestureStart = null;
            Flow?.LayoutGestureEnded();
        }
    }

    private double Spread()
    {
        var points = _pointers.Values.Take(2).ToList();
        return points.Count < 2 ? 0 : Point.Distance(points[0], points[1]);
    }
}
