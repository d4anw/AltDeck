using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace RoDeck;

public static class MenuHelper
{
    /// <summary>Opens a small dark popup menu under <paramref name="target"/>.</summary>
    public static void Show(FrameworkElement target, params (string Text, Action Do)[] items)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
        foreach (var (text, action) in items)
        {
            var mi = new MenuItem { Header = text };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }
}
