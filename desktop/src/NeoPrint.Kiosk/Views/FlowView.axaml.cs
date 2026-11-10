using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

using NeoPrint.Kiosk.ViewModels;

namespace NeoPrint.Kiosk.Views;

public sealed partial class FlowView : UserControl
{
    /// <summary>How far, in pixels, a finger goes across the page to turn it (T-419).</summary>
    public const double SwipeDistance = 80;

    private Point? _swipeFrom;

    public FlowView()
    {
        InitializeComponent();

        // The editor's page is turned by a swipe as well as by its buttons (T-419).
        EditPageCard.PointerPressed += (_, e) => _swipeFrom = e.GetPosition(this);
        EditPageCard.PointerReleased += (_, e) => EndSwipe(e.GetPosition(this));
        EditPageCard.PointerCaptureLost += (_, _) => _swipeFrom = null;
    }

    private void EndSwipe(Point to)
    {
        if (_swipeFrom is not { } from)
        {
            return;
        }

        _swipeFrom = null;
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;

        // Mostly sideways and far enough: right to left is the next page.
        if (Math.Abs(dx) >= SwipeDistance && Math.Abs(dx) > Math.Abs(dy) * 2 && DataContext is PrintFlowViewModel flow)
        {
            flow.SwipeEditPage(forward: dx < 0);
        }
    }
}
