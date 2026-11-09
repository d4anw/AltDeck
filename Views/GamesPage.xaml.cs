using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RoDeck.Views;

/// <summary>Games: charts as rows (each opens the whole chart), search, favourites, recently played, and a page per game.</summary>
public partial class GamesPage : UserControl
{
    public GamesPage() { InitializeComponent(); }

    // the ‹ › buttons and Shift + wheel scroll a row sideways; the plain wheel keeps scrolling the page
    private void ScrollLeft_Click(object sender, RoutedEventArgs e) => Step((sender as FrameworkElement)?.Tag as ScrollViewer, -1);
    private void ScrollRight_Click(object sender, RoutedEventArgs e) => Step((sender as FrameworkElement)?.Tag as ScrollViewer, +1);

    private static void Step(ScrollViewer? row, int dir)
    {
        if (row == null) return;
        row.ScrollToHorizontalOffset(row.HorizontalOffset + dir * Math.Max(200, row.ViewportWidth * 0.8));
    }

    private void Row_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Shift || sender is not ScrollViewer row) return;
        row.ScrollToHorizontalOffset(row.HorizontalOffset - e.Delta);
        e.Handled = true;
    }
}
