using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AltDeck.ViewModels;

namespace AltDeck.Views;

/// <summary>Multi-launch: Running, Launch, Accounts, Farming and Activity tabs, and "Hold to stop all".</summary>
public partial class MultiLaunchPage : UserControl
{
    private readonly DispatcherTimer _hold = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private DateTime _holdStart;

    public MultiLaunchPage()
    {
        InitializeComponent();

        _hold.Tick += (_, _) =>
        {
            var p = (DateTime.Now - _holdStart).TotalSeconds;
            HoldBar.Value = Math.Min(100, p * 100);
            if (p >= 1)
            {
                StopHold();
                ((MainViewModel)DataContext).StopAllCommand.Execute(null);
            }
        };
        HoldBtn.PreviewMouseLeftButtonDown += (_, _) =>
        {
            _holdStart = DateTime.Now;
            HoldBar.Value = 0;
            HoldBar.Visibility = Visibility.Visible;
            _hold.Start();
        };
        HoldBtn.PreviewMouseLeftButtonUp += (_, _) => StopHold();
        HoldBtn.MouseLeave += (_, _) => StopHold();
    }

    private void StopHold()
    {
        _hold.Stop();
        HoldBar.Visibility = Visibility.Collapsed;
        HoldBar.Value = 0;
    }
}
