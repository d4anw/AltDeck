using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RoDeck.Services;
using RoDeck.ViewModels;

namespace RoDeck;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new MainViewModel();
    private readonly TrayService _tray = new();
    private readonly HotkeyService _hotkey = new();
    private bool _reallyExit;       // true when the person chose Quit (or Windows is signing out): Close must not hide to the tray
    private bool _updateOpen;          // the update window is showing
    private bool _restartAfterUpdate;  // the person chose "Update": restart into the new version on close

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        StateChanged += (_, _) =>
        {
            MaxBtn.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        };

        _vm.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "BackgroundImage") ApplyBackground();
            else if (e.PropertyName is "CloseToTray" or "TradeAdOn" || e.PropertyName?.StartsWith("Notify") == true) _tray.Visible = TrayWanted;
            else if (e.PropertyName == "PanicHotkey") ApplyHotkey();
        };
        ApplyBackground();

        // ---- tray icon (while "Close to tray" or a notification is on: Windows shows notifications through it)
        _tray.Visible = TrayWanted;
        _vm.Notification += (title, text, where) => Dispatcher.InvokeAsync(() =>
        {
            _lastNotificationPage = where;
            _tray.Balloon(title, text);
        });
        _tray.BalloonClicked += () => Dispatcher.InvokeAsync(() =>
        {
            ShowFromTray();
            if (_lastNotificationPage.Length > 0) _vm.OpenNotification(_lastNotificationPage);
        });
        _tray.OpenRequested += ShowFromTray;
        _tray.PanelRequested += () => Dispatcher.InvokeAsync(ShowTrayPanel);
        _tray.StopAllRequested += () =>
        {
            _vm.StopAllCommand.Execute(null);
            _tray.Balloon("RoDeck", _vm.StatusMessage);
        };
        _tray.QuitRequested += () => { _reallyExit = true; Close(); };
        _vm.UpdateAvailable += (u, notes, manual) => Dispatcher.InvokeAsync(() => AskToUpdate(u, notes, manual));
        _vm.WhatsNew += (v, notes) => Dispatcher.InvokeAsync(() =>
        {
            var w = new WhatsNewWindow(v, notes);
            if (IsVisible) w.Owner = this; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            w.Show();
        });
        _vm.Notify += msg => { if (!IsVisible) _tray.Balloon("RoDeck", msg); };
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Page)) AnimatePageIn();
            if (e.PropertyName == nameof(MainViewModel.RunningCount))
                _tray.SetTooltip(_vm.RunningCount == 0 ? "RoDeck" : $"RoDeck · {_vm.RunningCount} running");
        };
        Application.Current.SessionEnding += (_, _) => _reallyExit = true;   // never block a Windows shutdown or sign-out

        // ---- panic hotkey (needs the window handle, so it waits for SourceInitialized)
        SourceInitialized += (_, _) =>
        {
            // A borderless window overflows the screen when maximized (the bottom ends up under the taskbar),
            // so ask for exactly the work area of the monitor it is on.
            ((System.Windows.Interop.HwndSource)PresentationSource.FromVisual(this)).AddHook(MaximizeHook);
            RoundCorners();
            _hotkey.Attach(this);
            _hotkey.Pressed += OnPanicHotkey;
            _hotkey.PressedId += OnPresetHotkey;
            ApplyHotkey();
            ApplyPresetHotkeys();
        };
        _vm.PresetHotkeysChanged += ApplyPresetHotkeys;

        Closing += (_, e) =>
        {
            if (_reallyExit || !KeepRunningOnClose) return;
            e.Cancel = true;
            _tray.Visible = true;
            Hide();
            if (!_vm.Settings.TrayTipShown)   // once ever, not every time
            {
                _vm.Settings.TrayTipShown = true;
                _vm.SaveSettingsNow();
                _tray.Balloon("RoDeck is still running",
                    _vm.AdRunning ? "Your trade ad keeps posting. Click the RoDeck icon by the clock to see it, stop it or quit."
                                  : "Clients and auto-rejoin keep working. Click the RoDeck icon by the clock to open it or quit.");
            }
        };


        SmoothScroll.Attach(this);   // gliding mouse-wheel scrolling everywhere

        // back / forward: the mouse's side buttons and Alt+← / Alt+→
        PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.XButton1) { _vm.GoBack(); e.Handled = true; }
            else if (e.ChangedButton == System.Windows.Input.MouseButton.XButton2) { _vm.GoForward(); e.Handled = true; }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Alt) return;
            var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
            if (key == System.Windows.Input.Key.Left) { _vm.GoBack(); e.Handled = true; }
            else if (key == System.Windows.Input.Key.Right) { _vm.GoForward(); e.Handled = true; }
        };

        Loaded += async (_, _) =>
        {
            _ = _vm.CheckForUpdatesAtStartAsync();   // asks right away if a new version is out
            await _vm.InitializeAsync();
        };
        Closed += (_, _) =>
        {
            try { _hotkey.Dispose(); } catch { }
            try { _tray.Dispose(); } catch { }
            _vm.Shutdown();   // also installs a downloaded update
            if (_restartAfterUpdate) try { Updater.Restart(); } catch { }
        };
    }

    private void AskToUpdate(UpdateInfo u, string? notes, bool manual)
    {
        if (_updateOpen) return;
        _updateOpen = true;
        try
        {
            var w = new UpdateWindow(u, notes, askedByUser: manual);
            if (IsVisible) w.Owner = this; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            bool update = w.ShowDialog() == true;
            if (w.DontAskAgain) _vm.StopUpdatePrompts();
            if (!update) return;

            _vm.UpdateAccepted(u);
            _restartAfterUpdate = true;
            _reallyExit = true;
            Close();   // Closed installs the download and starts the new version
        }
        finally { _updateOpen = false; }
    }

    /// <summary>A new page fades in and slides up a few pixels, so switching feels smooth instead of a hard cut.</summary>
    private void AnimatePageIn()
    {
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
        var slide = new System.Windows.Media.Animation.DoubleAnimation(10.0, 0.0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
        PageHost.BeginAnimation(OpacityProperty, fade);
        PageSlide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slide);
    }

    private string _lastNotificationPage = "";
    private bool TrayWanted => _vm.Settings.CloseToTray || _vm.AnyNotifications || _vm.AdRunning;
    /// <summary>Closing the window hides it instead: "Close to tray" is on, or a trade ad is posting (it needs RoDeck running).</summary>
    private bool KeepRunningOnClose => _vm.Settings.CloseToTray || _vm.AdRunning;
    private TrayFlyout? _flyout;

    private void ShowTrayPanel()
    {
        if (_flyout != null) { _flyout.SafeClose(); _flyout = null; return; }   // a second click closes it
        _flyout = new TrayFlyout { DataContext = _vm };
        _flyout.Closed += (_, _) => _flyout = null;
        _flyout.OpenRequested += ShowFromTray;
        _flyout.QuitRequested += () => { _reallyExit = true; Close(); };
        _flyout.StopAllRequested += () => _vm.StopAllCommand.Execute(null);
        _flyout.ShowNearTray();
    }

    /// <summary>Shows the window (from the tray, minimized or behind other apps). Used when RoDeck is started a second time.</summary>
    public void BringToFront()
    {
        ShowFromTray();
        Topmost = true;    // Windows only lets a background app take focus like this
        Topmost = false;
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ApplyHotkey()
    {
        var (ok, message) = _hotkey.Register(_vm.Settings.PanicHotkey);
        _vm.PanicHotkeyStatus = ok ? (message == "Off." ? "" : message) : "Not active: " + message;
    }

    private void OnPanicHotkey()
    {
        _vm.StopAllCommand.Execute(null);
        if (!IsVisible) _tray.Balloon("RoDeck", _vm.StatusMessage);
    }

    /// <summary>Registers every preset's hotkey again (ids follow the presets' order) and shows each one's status.</summary>
    private void ApplyPresetHotkeys()
    {
        _hotkey.UnregisterPresets();
        var presets = _vm.Settings.Presets;
        for (int i = 0; i < presets.Count; i++)
        {
            var p = presets[i];
            if (!p.HasHotkey) { p.HotkeyStatus = ""; continue; }
            var (ok, message) = _hotkey.Register(HotkeyService.PresetIdBase + i, p.Hotkey);
            p.HotkeyStatus = ok ? message : "Hotkey not active: " + message;
        }
    }

    private async void OnPresetHotkey(int id)
    {
        var i = id - HotkeyService.PresetIdBase;
        var presets = _vm.Settings.Presets;
        if (i < 0 || i >= presets.Count) return;
        try { await _vm.RunPresetAsync(presets[i], "hotkey"); }
        catch (Exception ex) { _vm.StatusMessage = ex.Message; }
    }

    private void ApplyBackground()
    {
        try
        {
            var path = _vm.Settings.BackgroundImage;
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) { BgImage.Source = null; return; }
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            BgImage.Source = bmp;
        }
        catch { BgImage.Source = null; }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PointI { public int X, Y; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RectI { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MinMaxInfo { public PointI Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public RectI Monitor, Work; public int Flags; }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Rounded corners like other Windows 11 apps (Windows squares them itself when maximized). Older Windows: no change.</summary>
    private void RoundCorners()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int round = 2;                                   // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref round, 4);   // DWMWA_WINDOW_CORNER_PREFERENCE
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private static IntPtr MaximizeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != 0x0024) return IntPtr.Zero;   // WM_GETMINMAXINFO
        var mon = MonitorFromWindow(hwnd, 2);    // nearest monitor
        var mi = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
        if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi)) return IntPtr.Zero;
        var info = System.Runtime.InteropServices.Marshal.PtrToStructure<MinMaxInfo>(lParam);
        info.MaxPosition = new PointI { X = mi.Work.Left - mi.Monitor.Left, Y = mi.Work.Top - mi.Monitor.Top };
        info.MaxSize = new PointI { X = mi.Work.Right - mi.Work.Left, Y = mi.Work.Bottom - mi.Work.Top };
        System.Runtime.InteropServices.Marshal.StructureToPtr(info, lParam, true);
        return IntPtr.Zero;   // not handled, so WPF still applies the minimum size
    }

    /// <summary>Top right: pick which account the pages show, or go manage accounts.</summary>
    private void AccountBtn_Click(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu
        {
            PlacementTarget = AccountBtn,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };
        foreach (var a in _vm.Accounts.OrderByDescending(x => x.IsFavourite).ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase))
        {
            // the avatar, the name (and @username), and a tick on the account in use
            bool current = a == _vm.CurrentAccount;
            var row = new System.Windows.Controls.DockPanel { LastChildFill = true, MinWidth = 190 };
            var tick = new System.Windows.Controls.TextBlock
            {
                Text = "", FontSize = 12, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("Icons"),
                Visibility = current ? Visibility.Visible : Visibility.Hidden
            };
            tick.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Accent");
            System.Windows.Controls.DockPanel.SetDock(tick, System.Windows.Controls.Dock.Right);
            row.Children.Add(tick);
            var names = new System.Windows.Controls.TextBlock { VerticalAlignment = VerticalAlignment.Center };
            names.Inlines.Add(new System.Windows.Documents.Run(a.Label) { FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal });
            if (!string.IsNullOrEmpty(a.Username) && a.Username != a.Label)
            {
                var at = new System.Windows.Documents.Run("  @" + a.Username) { FontSize = 11.5 };
                at.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Muted");
                names.Inlines.Add(at);
            }
            if (a.StatusKind == "signedout")
            {
                var so = new System.Windows.Documents.Run("  · signed out") { FontSize = 11.5 };
                so.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Red");
                names.Inlines.Add(so);
            }
            row.Children.Add(names);
            var item = new System.Windows.Controls.MenuItem
            {
                Header = row,
                Padding = new Thickness(14, 6, 14, 6),
                Icon = new Controls.AvatarView { Width = 26, Height = 26, Url = a.AvatarUrl, Initials = a.Initials }
            };
            var account = a;
            item.Click += (_, _) => _vm.SwitchAccountCommand.Execute(account);
            menu.Items.Add(item);
        }
        if (_vm.Accounts.Count > 0) menu.Items.Add(new System.Windows.Controls.Separator());
        Add(menu, _vm.Accounts.Count > 0 ? "Manage accounts…" : "Add an account…", "\uE716", () => _vm.GoMultiTabCommand.Execute("Accounts"));
        Add(menu, "Check all sign-ins", "\uE72C", () => _vm.CheckAllCommand.Execute(null));
        Add(menu, "Accounts & security", "\uE72E", () => _vm.GoSectionCommand.Execute("Accounts"));
        Add(menu, _vm.Settings.Theme == "Light" ? "Dark mode" : "Light mode", "\uE706", () => _vm.ToggleThemeCommand.Execute(null));
        menu.IsOpen = true;
    }

    /// <summary>The bell top right: what is waiting, and the notification settings.</summary>
    private void BellBtn_Click(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu
        {
            PlacementTarget = BellBtn,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };
        Add(menu, _vm.RequestCount switch { 0 => "No friend requests", 1 => "1 friend request", var n => $"{n} friend requests" },
            "\uE8FA", () => _vm.OpenNotification("Requests"));
        Add(menu, _vm.InboundCount switch { 0 => "No trades waiting", 1 => "1 trade waiting", var n => $"{n} trades waiting" },
            "\uE8AB", () => _vm.OpenNotification("Trades"));
        menu.Items.Add(new System.Windows.Controls.Separator());
        Add(menu, "Notification settings…", "\uE713", () => _vm.GoSectionCommand.Execute("Notifications"));
        menu.IsOpen = true;
    }

    /// <summary>The Robux pill: the Marketplace.</summary>
    private void Robux_Click(object sender, RoutedEventArgs e) => _vm.Page = "Marketplace";

    private static void Add(System.Windows.Controls.ContextMenu menu, string text, string glyph, Action click)
    {
        var item = new System.Windows.Controls.MenuItem
        {
            Header = text,
            Icon = new System.Windows.Controls.TextBlock { Text = glyph, FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("Icons"), FontSize = 13 }
        };
        item.Click += (_, _) => click();
        menu.Items.Add(item);
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
