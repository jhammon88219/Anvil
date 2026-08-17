using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives; // ButtonBase
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;               // VisualTreeHelper, TranslateTransform
using Windows.Foundation;

namespace Anvil.Controls;

/// <summary>
/// DragMove — makes a floating card draggable by a grab-handle strip (its header).
///
/// Attach <c>DragMove.MoveTarget</c> to the handle element (e.g. a card's header Grid), pointing it
/// at the element to move (the card's visual-root Border). A pointer drag on the handle offsets the
/// target via a <see cref="TranslateTransform"/> — a PURE VISUAL offset, so the card's anchoring
/// (HorizontalAlignment + FloatingWindowMargin in MainWindow) still decides where it OPENS; the drag
/// just layers a session-lived offset on top and nothing else reflows. The offset persists across
/// hide/show (the card stays where you parked it) and resets to the anchor on app restart (it's not
/// persisted). The offset is clamped so a sliver of the card always stays on-screen and grabbable.
///
/// Removable as a unit: delete this file plus the <c>DragMove.MoveTarget</c> usages
/// (DevToolsCard.xaml header, RadarSiteExplorer.xaml header).
/// </summary>
public static class DragMove
{
    private const double KeepVisibleMargin = 8; // px of the card that must remain within the root visual

    // The element a drag on THIS handle should move. Set on the handle; value = the card root to offset.
    public static readonly DependencyProperty MoveTargetProperty =
        DependencyProperty.RegisterAttached(
            "MoveTarget", typeof(FrameworkElement), typeof(DragMove),
            new PropertyMetadata(null, OnMoveTargetChanged));

    public static FrameworkElement GetMoveTarget(DependencyObject o) =>
        (FrameworkElement)o.GetValue(MoveTargetProperty);
    public static void SetMoveTarget(DependencyObject o, FrameworkElement value) =>
        o.SetValue(MoveTargetProperty, value);

    private sealed class DragState
    {
        public bool Dragging;
        public Point StartPointer;   // window-relative (GetCurrentPoint(null)), so moving the target can't feed back
        public double StartX, StartY;
        public Pointer? Captured;
    }

    // Per-handle drag state, GC-friendly (no strong refs keeping controls alive).
    private static readonly ConditionalWeakTable<UIElement, DragState> States = new();

    private static void OnMoveTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement handle) return;
        // Idempotent (re)wire.
        handle.PointerPressed -= OnPressed;
        handle.PointerMoved -= OnMoved;
        handle.PointerReleased -= OnReleased;
        handle.PointerCaptureLost -= OnCaptureLost;
        if (e.NewValue is FrameworkElement)
        {
            handle.PointerPressed += OnPressed;
            handle.PointerMoved += OnMoved;
            handle.PointerReleased += OnReleased;
            handle.PointerCaptureLost += OnCaptureLost;
        }
    }

    private static TranslateTransform EnsureTransform(FrameworkElement target)
    {
        if (target.RenderTransform is TranslateTransform t) return t;
        var nt = new TranslateTransform();
        target.RenderTransform = nt;
        return nt;
    }

    private static void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        var handle = (UIElement)sender;
        var target = GetMoveTarget(handle);
        if (target is null) return;
        // Don't hijack a press that landed on an interactive control in the header (the close button).
        if (IsInteractive(e.OriginalSource as DependencyObject, handle)) return;

        var t = EnsureTransform(target);
        var st = States.GetOrCreateValue(handle);
        st.StartPointer = e.GetCurrentPoint(null).Position;
        st.StartX = t.X;
        st.StartY = t.Y;
        if (handle.CapturePointer(e.Pointer))
        {
            st.Dragging = true;
            st.Captured = e.Pointer;
            e.Handled = true;
        }
    }

    private static void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        var handle = (UIElement)sender;
        if (!States.TryGetValue(handle, out var st) || !st.Dragging) return;
        var target = GetMoveTarget(handle);
        if (target is null) return;

        var p = e.GetCurrentPoint(null).Position;
        var t = EnsureTransform(target);
        double ox = st.StartX + (p.X - st.StartPointer.X);
        double oy = st.StartY + (p.Y - st.StartPointer.Y);
        (ox, oy) = Clamp(target, t, ox, oy);
        t.X = ox;
        t.Y = oy;
        e.Handled = true;
    }

    private static void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        var handle = (UIElement)sender;
        if (States.TryGetValue(handle, out var st) && st.Dragging)
        {
            st.Dragging = false;
            if (st.Captured is not null) handle.ReleasePointerCapture(st.Captured);
            st.Captured = null;
            e.Handled = true;
        }
    }

    private static void OnCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        var handle = (UIElement)sender;
        if (States.TryGetValue(handle, out var st))
        {
            st.Dragging = false;
            st.Captured = null;
        }
    }

    // Keep at least KeepVisibleMargin px of the card within the root visual on every side.
    private static (double, double) Clamp(FrameworkElement target, TranslateTransform t, double ox, double oy)
    {
        if (target.XamlRoot?.Content is not FrameworkElement root) return (ox, oy);
        double rootW = root.ActualWidth, rootH = root.ActualHeight;
        double w = target.ActualWidth, h = target.ActualHeight;
        if (rootW <= 0 || rootH <= 0 || w <= 0 || h <= 0) return (ox, oy);

        // Current top-left in root space (includes the live transform); back out the offset to get the
        // ANCHORED base position, so the clamp is expressed against a fixed origin.
        Point cur;
        try { cur = target.TransformToVisual(root).TransformPoint(new Point(0, 0)); }
        catch { return (ox, oy); }
        double baseX = cur.X - t.X;
        double baseY = cur.Y - t.Y;

        double minOx = KeepVisibleMargin - w - baseX;      // card may hang off the left, leaving a sliver at x=0
        double maxOx = rootW - KeepVisibleMargin - baseX;  // …and off the right
        double minOy = KeepVisibleMargin - h - baseY;
        double maxOy = rootH - KeepVisibleMargin - baseY;

        if (minOx <= maxOx) ox = Clamp(ox, minOx, maxOx);
        if (minOy <= maxOy) oy = Clamp(oy, minOy, maxOy);
        return (ox, oy);
    }

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

    // True if the pressed element is (or sits under) an interactive control between it and the handle.
    private static bool IsInteractive(DependencyObject? src, UIElement handle)
    {
        while (src is not null && !ReferenceEquals(src, handle))
        {
            if (src is ButtonBase) return true;
            src = VisualTreeHelper.GetParent(src);
        }
        return false;
    }
}
