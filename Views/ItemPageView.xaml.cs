using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using RoDeck.ViewModels;

namespace RoDeck.Views;

/// <summary>A limited's page: picture, numbers, the RAP graph (hover for a day) and its sellers. Used by Trades and the Marketplace.</summary>
public partial class ItemPageView : UserControl
{
    public ItemPageView() { InitializeComponent(); }

    // ---- the item page's graph: a line, a dot and a card for the day under the mouse
    private void Chart_MouseMove(object sender, MouseEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.RapChartData.Count < 2 || vm.RapChart.Count < 2) { Chart_MouseLeave(sender, e); return; }
        double w = ChartArea.ActualWidth, h = ChartArea.ActualHeight;
        if (w <= 0) return;
        var pos = e.GetPosition(ChartArea);
        int i = (int)Math.Round(Math.Clamp(pos.X / w, 0, 1) * (vm.RapChartData.Count - 1));
        var (day, price) = vm.RapChartData[i];
        var pt = vm.RapChart[Math.Min(i, vm.RapChart.Count - 1)];
        double x = pt.X / 600 * w, y = pt.Y / 140 * h;

        HoverLine.X1 = HoverLine.X2 = x;
        HoverLine.Y2 = h;
        Canvas.SetLeft(HoverDot, x - 5);
        Canvas.SetTop(HoverDot, y - 5);
        HoverDate.Text = day.ToString("d MMM yyyy");
        HoverRap.Text = $"R$ {price:N0}";
        long value = vm.OpenStats?.Value ?? -1;
        HoverValueLabel.Text = "Value now ";
        HoverValue.Text = value > 0 ? $"R$ {value:N0}" : "not assigned";
        HoverLine.Visibility = HoverDot.Visibility = HoverTip.Visibility = Visibility.Visible;

        HoverTip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var tip = HoverTip.DesiredSize;
        double left = x + 14 + tip.Width > w ? x - 14 - tip.Width : x + 14;
        Canvas.SetLeft(HoverTip, Math.Max(0, left));
        Canvas.SetTop(HoverTip, Math.Clamp(y - tip.Height / 2, 0, Math.Max(0, h - tip.Height)));
    }

    private void Chart_MouseLeave(object sender, MouseEventArgs e)
    {
        HoverLine.Visibility = HoverDot.Visibility = HoverTip.Visibility = Visibility.Collapsed;
    }

    // ---- sellers: two columns, one on a narrow window
    private UniformGrid? _sellersGrid;
    private double _sellersWidth;
    private void SellersGrid_Loaded(object sender, RoutedEventArgs e) { _sellersGrid = sender as UniformGrid; FitSellers(); }
    private void Sellers_SizeChanged(object sender, SizeChangedEventArgs e) { _sellersWidth = e.NewSize.Width; FitSellers(); }
    private void FitSellers()
    {
        if (_sellersGrid != null && _sellersWidth > 0) _sellersGrid.Columns = Math.Clamp((int)(_sellersWidth / 420), 1, 3);
    }
}
