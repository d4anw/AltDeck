using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace AltDeck;

/// <summary>
/// Smooth mouse-wheel scrolling for the whole window.
///  - The wheel scrolls the nearest list or page under the cursor that can still move in that direction, so a sideways
///    row (friends, games) or a full inner list never "eats" the wheel and stops the page.
///  - The movement glides to its target over a few frames instead of jumping 48 px at a time.
/// Shift + wheel is left to the sideways rows. Lists that scroll by item (CanContentScroll) scroll by item, without the glide.
/// </summary>
public static class SmoothScroll
{
    private const double PixelsPerNotch = 110;   // one wheel click
    private const double Ease = 0.22;            // share of the remaining distance covered each frame

    private static readonly Dictionary<ScrollViewer, double> Targets = new();
    private static bool _rendering;

    public static void Attach(Window w) => w.PreviewMouseWheel += OnWheel;

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || Keyboard.Modifiers == ModifierKeys.Shift) return;
        if (e.OriginalSource is not DependencyObject src) return;

        // a dropdown list that is open has its own scrolling; leave it alone
        if (FindParent<Popup>(src) != null) return;

        bool down = e.Delta < 0;
        for (var d = src; d != null; d = Parent(d))
        {
            if (d is not ScrollViewer sv) continue;
            if (sv.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled || sv.ScrollableHeight <= 0) continue;

            double current = Targets.TryGetValue(sv, out var t) ? t : sv.VerticalOffset;
            bool canMove = down ? current < sv.ScrollableHeight - 0.5 : current > 0.5;
            if (!canMove) continue;   // this one is at its end: let the page around it scroll

            if (sv.CanContentScroll)
            {
                // scrolls by item: keep Windows' own steps, just make sure the wheel lands here
                if (down) sv.LineDown(); else sv.LineUp();
                if (down) sv.LineDown(); else sv.LineUp();
                if (down) sv.LineDown(); else sv.LineUp();
            }
            else
            {
                double target = Math.Clamp(current - e.Delta / 120.0 * PixelsPerNotch, 0, sv.ScrollableHeight);
                Targets[sv] = target;
                if (!_rendering) { CompositionTarget.Rendering += OnFrame; _rendering = true; }
            }
            e.Handled = true;
            return;
        }
    }

    private static void OnFrame(object? sender, EventArgs e)
    {
        foreach (var (sv, target) in Targets.ToList())
        {
            double clamped = Math.Clamp(target, 0, sv.ScrollableHeight);
            double now = sv.VerticalOffset;
            double step = (clamped - now) * Ease;
            if (Math.Abs(clamped - now) < 0.6 || !sv.IsVisible)
            {
                sv.ScrollToVerticalOffset(clamped);
                Targets.Remove(sv);
                continue;
            }
            sv.ScrollToVerticalOffset(now + (Math.Abs(step) < 0.6 ? Math.Sign(step) * 0.6 : step));
        }
        if (Targets.Count == 0)
        {
            CompositionTarget.Rendering -= OnFrame;
            _rendering = false;
        }
    }

    private static DependencyObject? Parent(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (var p = d; p != null; p = Parent(p)) if (p is T t) return t;
        return null;
    }
}
