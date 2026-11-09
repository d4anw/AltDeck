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

    private static bool IsInside(DependencyObject? d, DependencyObject card)
    {
        for (; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d == card) return true;
        return false;
    }
}
