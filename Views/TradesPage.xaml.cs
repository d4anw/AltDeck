using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RoDeck.ViewModels;

namespace RoDeck.Views;

/// <summary>Trades: live deals, every limited with its value, what each account's limiteds are worth, and your trades.</summary>
public partial class TradesPage : UserControl
{
    public TradesPage()
    {
        InitializeComponent();
        // a picker (calculator or trade ad) closes when you click anywhere outside it, or press Esc
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (DataContext is not MainViewModel vm || e.OriginalSource is not DependencyObject src) return;
            if (PresentationSource.FromDependencyObject(src) != PresentationSource.FromVisual(this)) return;   // a dropdown's list: not outside
            if (vm.IsCalcPickerOpen && !IsInside(src, CalcPickerCard)) vm.IsCalcPickerOpen = false;
            if (vm.IsAdPickerOpen && !IsInside(src, AdPickerCard)) vm.IsAdPickerOpen = false;
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || DataContext is not MainViewModel vm) return;
            if (vm.IsCalcPickerOpen) { vm.IsCalcPickerOpen = false; e.Handled = true; }
            else if (vm.IsAdPickerOpen) { vm.IsAdPickerOpen = false; e.Handled = true; }
        };
    }

    // ---- the calculator's picker opens right under the slot you clicked, not at the top of the tab
    private void CalcSlot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement slot) return;
        // after the command ran (it opens the picker for an empty slot)
        Dispatcher.BeginInvoke(() =>
        {
            if (DataContext is not MainViewModel { IsCalcPickerOpen: true }) return;
            var below = slot.TranslatePoint(new Point(0, slot.ActualHeight), CalcTab);
            CalcPickerCard.HorizontalAlignment = HorizontalAlignment.Left;
            CalcPickerCard.Width = Math.Min(900, CalcTab.ActualWidth);
            CalcPickerCard.Margin = new Thickness(0, Math.Max(0, below.Y + 2), 0, 0);
            CalcPickerCard.UpdateLayout();
            CalcPickerCard.BringIntoView();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // the watchlist's price and % boxes: Enter applies what's typed
    private void WatchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }

    private static bool IsInside(DependencyObject? d, DependencyObject card)
    {
        for (; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d == card) return true;
        return false;
    }
}
