using System.Windows;

namespace RoDeck;

/// <summary>
/// The small panel that opens from RoDeck's tray icon, above the taskbar near the clock: the trade ad (with Start /
/// Stop), clients running (with Stop all), notifications, and Open / Quit. Closes when you click anywhere else.
/// </summary>
public partial class TrayFlyout : Window
{
    public event Action? OpenRequested;
    public event Action? QuitRequested;
    public event Action? StopAllRequested;

    private bool _closing;

    public TrayFlyout()
    {
        InitializeComponent();
        Deactivated += (_, _) => SafeClose();
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) SafeClose(); };
        Closing += (_, _) => _closing = true;
    }

    /// <summary>Closes once. Opening RoDeck from here also takes the focus away, which would ask it to close a second time.</summary>
    public void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    /// <summary>Opens next to the tray: above the taskbar, right-aligned with where the icon was clicked.</summary>
    public void ShowNearTray()
    {
        var area = SystemParameters.WorkArea;
        Left = -10000; Top = -10000;
        Show();
        UpdateLayout();
        var cursor = System.Windows.Forms.Cursor.Position;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        double x = cursor.X / dpi.DpiScaleX, y = cursor.Y / dpi.DpiScaleY;
        Left = Math.Clamp(x - ActualWidth + 40, area.Left, area.Right - ActualWidth);
        Top = y > area.Bottom - 10 || y < area.Top + 10
            ? (y > area.Height / 2 ? area.Bottom - ActualHeight : area.Top)      // taskbar at the bottom (or top)
            : Math.Clamp(y - ActualHeight, area.Top, area.Bottom - ActualHeight);
        Activate();
    }

    // close first, then act once the panel is gone (so the main window coming up can't touch a closing panel)
    private void Open_Click(object sender, RoutedEventArgs e) { SafeClose(); Dispatcher.BeginInvoke(new Action(() => OpenRequested?.Invoke())); }
    private void Quit_Click(object sender, RoutedEventArgs e) { SafeClose(); Dispatcher.BeginInvoke(new Action(() => QuitRequested?.Invoke())); }
    private void StopAll_Click(object sender, RoutedEventArgs e) => StopAllRequested?.Invoke();
}
