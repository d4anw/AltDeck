using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AltDeck.Services;
using AltDeck.ViewModels;

namespace AltDeck;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new MainViewModel();
    private readonly TrayService _tray = new();
    private readonly HotkeyService _hotkey = new();
    private bool _reallyExit;       // true when the person chose Quit (or Windows is signing out): Close must not hide to the tray
    private bool _trayTipShown;
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
            else if (e.PropertyName == "CloseToTray") _tray.Visible = _vm.Settings.CloseToTray;
            else if (e.PropertyName == "PanicHotkey") ApplyHotkey();
        };
        ApplyBackground();

        // ---- tray icon (only while "Close to tray" is on)
        _tray.Visible = _vm.Settings.CloseToTray;
        _tray.OpenRequested += ShowFromTray;
        _tray.StopAllRequested += () =>
        {
            _vm.StopAllCommand.Execute(null);
            _tray.Balloon("AltDeck", _vm.StatusMessage);
        };
        _tray.QuitRequested += () => { _reallyExit = true; Close(); };
        _vm.UpdateAvailable += (u, notes, manual) => Dispatcher.InvokeAsync(() => AskToUpdate(u, notes, manual));
        _vm.WhatsNew += (v, notes) => Dispatcher.InvokeAsync(() =>
        {
            var w = new WhatsNewWindow(v, notes);
            if (IsVisible) w.Owner = this; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            w.Show();
        });
        _vm.Notify += msg => { if (!IsVisible) _tray.Balloon("AltDeck", msg); };
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.RunningCount))
                _tray.SetTooltip(_vm.RunningCount == 0 ? "AltDeck" : $"AltDeck · {_vm.RunningCount} running");
        };
        Application.Current.SessionEnding += (_, _) => _reallyExit = true;   // never block a Windows shutdown or sign-out

        // ---- panic hotkey (needs the window handle, so it waits for SourceInitialized)
        SourceInitialized += (_, _) =>
        {
            // A borderless window overflows the screen when maximized (the bottom ends up under the taskbar),
            // so ask for exactly the work area of the monitor it is on.
            ((System.Windows.Interop.HwndSource)PresentationSource.FromVisual(this)).AddHook(MaximizeHook);
            _hotkey.Attach(this);
            _hotkey.Pressed += OnPanicHotkey;
            _hotkey.PressedId += OnPresetHotkey;
            ApplyHotkey();
            ApplyPresetHotkeys();
        };
        _vm.PresetHotkeysChanged += ApplyPresetHotkeys;

        Closing += (_, e) =>
        {
            if (_reallyExit || !_vm.Settings.CloseToTray) return;
            e.Cancel = true;
            Hide();
            if (!_trayTipShown)
            {
                _trayTipShown = true;
                _tray.Balloon("AltDeck is still running", "Clients and auto-rejoin keep working. Click the tray icon to open it, right-click to quit.");
            }
        };


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

    /// <summary>Shows the window (from the tray, minimized or behind other apps). Used when AltDeck is started a second time.</summary>
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
        if (!IsVisible) _tray.Balloon("AltDeck", _vm.StatusMessage);
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
            var header = a.Label + (string.IsNullOrEmpty(a.Username) || a.Username == a.Label ? "" : $"  (@{a.Username})")
                       + (a.StatusKind == "signedout" ? "  · signed out" : "");
            var item = new System.Windows.Controls.MenuItem { Header = header, IsCheckable = true, IsChecked = a == _vm.CurrentAccount };
            var account = a;
            item.Click += (_, _) => _vm.SwitchAccountCommand.Execute(account);
            menu.Items.Add(item);
        }
        if (_vm.Accounts.Count > 0) menu.Items.Add(new System.Windows.Controls.Separator());
        var manage = new System.Windows.Controls.MenuItem { Header = _vm.Accounts.Count > 0 ? "Manage accounts…" : "Add an account…" };
        manage.Click += (_, _) => _vm.GoMultiTabCommand.Execute("Accounts");
        menu.Items.Add(manage);
        menu.IsOpen = true;
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
