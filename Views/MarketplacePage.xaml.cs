using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RoDeck.Views;

/// <summary>Marketplace: browse avatar items, an item page with Try on, and getting / buying behind a confirm card.</summary>
public partial class MarketplacePage : UserControl
{
    public MarketplacePage()
    {
        InitializeComponent();
        // another item opened (from the rows under one): start at its top
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is System.ComponentModel.INotifyPropertyChanged vm)
                vm.PropertyChanged += (_, p) =>
                {
                    if (p.PropertyName == "OpenMarketItem") { ItemScroll.ScrollToTop(); SimilarRow.ScrollToLeftEnd(); CreatorRow.ScrollToLeftEnd(); }
                };
        };
    }

    // the rows under an item: the ‹ › buttons and Shift + wheel scroll sideways
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
