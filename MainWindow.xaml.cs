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
    private readonly DispatcherTimer _hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
    private DateTime _holdStart;
    private readonly TrayService _tray = new();
    private readonly HotkeyService _hotkey = new();
    private bool _reallyExit;       // true when the person chose Quit (or Windows is signing out): Close must not hide to the tray
    private bool _trayTipShown;
    private bool _updatePrompted;      // ask about an update once per run
    private bool _restartAfterUpdate;  // the person chose "Restart now"

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
        _vm.UpdateReady += u => Dispatcher.InvokeAsync(() => AskToRestartForUpdate(u));
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

        _hold.Tick += (_, _) =>
        {
            var p = (DateTime.Now - _holdStart).TotalSeconds;
            HoldBar.Value = Math.Min(100, p * 100);
            if (p >= 1)
            {
                StopHold();
                _vm.StopAllCommand.Execute(null);
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

        Loaded += async (_, _) => await _vm.InitializeAsync();
        Closed += (_, _) =>
        {
            try { _hotkey.Dispose(); } catch { }
            try { _tray.Dispose(); } catch { }
            _vm.Shutdown();   // also installs a downloaded update
            if (_restartAfterUpdate) try { Updater.Restart(); } catch { }
        };
    }

    private void AskToRestartForUpdate(UpdateInfo u)
    {
        if (_updatePrompted) return;
        _updatePrompted = true;
        if (!IsVisible)
        {
            _tray.Balloon("AltDeck update ready", $"AltDeck {u.Version} installs the next time AltDeck starts.");
            return;
        }
        var answer = MessageBox.Show(this,
            $"AltDeck {u.Version} is downloaded and ready.\n\nRestart AltDeck now to install it? Your Roblox clients keep running.\n\n" +
            "Choose No to install it the next time you close AltDeck.",
            "Update ready", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes) return;
        _restartAfterUpdate = true;
        _reallyExit = true;
        Close();
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

    private void StopHold()
    {
        _hold.Stop();
        HoldBar.Visibility = Visibility.Collapsed;
        HoldBar.Value = 0;
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

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
