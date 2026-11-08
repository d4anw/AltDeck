using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RoDeck.Services;

internal static class Win32
{
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint param, ref RECT rect, uint winIni);

    /// <summary>The usable screen area (without the taskbar) in pixels: x, y, width, height.</summary>
    public static (int X, int Y, int W, int H) WorkArea()
    {
        var r = new RECT();
        if (SystemParametersInfo(0x0030, 0, ref r, 0) && r.Right > r.Left && r.Bottom > r.Top)
            return (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        return (0, 0, 1280, 720);
    }

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr hWnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int w, int h, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref CpuRateControl info, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    private const int JobObjectCpuRateControlInformation = 15;
    private const uint CpuRateEnable = 0x1;
    private const uint CpuRateHardCap = 0x4;
    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessTerminate = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct CpuRateControl
    {
        public uint ControlFlags;
        public uint CpuRate;   // percent of the whole CPU, times 100 (2500 = 25%)
    }

    public static IntPtr MainWindowOf(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Refresh();
            var h = p.MainWindowHandle;
            return h != IntPtr.Zero ? h : FindRobloxWindow(pid);   // Windows' guess can come back empty; then look it up
        }
        catch { return IntPtr.Zero; }
    }

    public static void Restore(IntPtr h) { if (h != IntPtr.Zero) { ShowWindow(h, 9); SetForegroundWindow(h); } }

    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    /// <summary>
    /// Brings a minimized window back and puts it on top WITHOUT making it the active window. Used for many windows at once:
    /// making each one active in turn fights Windows' focus rules, and a slow (CPU-limited) client could be skipped.
    /// Async, so a busy client can't hold RoDeck up.
    /// </summary>
    public static void ShowQuietly(IntPtr h)
    {
        if (h == IntPtr.Zero) return;
        if (IsIconic(h)) ShowWindowAsync(h, 4);   // SW_SHOWNOACTIVATE: back to its old size and place
        // Above every other app: "always on top" for a moment, then normal again. It stays in front, but isn't pinned
        // there and doesn't take the keyboard. NOMOVE | NOSIZE | NOACTIVATE | NOSENDCHANGING | ASYNCWINDOWPOS
        const uint flags = 0x0001 | 0x0002 | 0x0010 | 0x0400 | 0x4000;
        SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, flags);   // HWND_TOPMOST
        SetWindowPos(h, new IntPtr(-2), 0, 0, 0, 0, flags);   // HWND_NOTOPMOST
    }

    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);

    /// <summary>Roblox's game window (class WINDOWSCLIENT) of this process, found by listing every window.</summary>
    private static IntPtr FindRobloxWindow(int pid)
    {
        var found = IntPtr.Zero;
        var cls = new System.Text.StringBuilder(64);
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var p);
            if (p != pid || !IsWindowVisible(h)) return true;
            cls.Clear();
            GetClassName(h, cls, cls.Capacity);
            if (cls.ToString() != "WINDOWSCLIENT") return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }
    public static void Minimize(IntPtr h) { if (h != IntPtr.Zero) ShowWindow(h, 6); }
    public static bool IsMinimized(IntPtr h) => h != IntPtr.Zero && IsIconic(h);

    /// <summary>True when Windows says the window has stopped answering ("Not responding"). Returns at once, unlike Process.Responding.</summary>
    public static bool IsHung(IntPtr h) => h != IntPtr.Zero && IsHungAppWindow(h);

    /// <summary>
    /// Roblox runs borderless, so a shrunk window has no title bar: nothing to drag, minimize or close.
    /// This puts a normal title bar (move, minimize, maximize, close, resize edges) back on the window.
    /// </summary>
    public static void EnsureTitleBar(IntPtr h)
    {
        if (h == IntPtr.Zero) return;
        const int GwlStyle = -16;
        const int OverlappedWindow = 0x00CF0000;   // caption + system menu + resize frame + min/max buttons
        int style = GetWindowLong(h, GwlStyle);
        if ((style & OverlappedWindow) == OverlappedWindow) return;
        SetWindowLong(h, GwlStyle, style | OverlappedWindow);
        // NOMOVE | NOSIZE | NOZORDER | NOACTIVATE | FRAMECHANGED: just redraw the frame
        SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
    }

    /// <summary>The window's position and size in screen pixels. False for no window or a minimized one.</summary>
    public static bool TryGetRect(IntPtr h, out int x, out int y, out int w, out int hgt)
    {
        x = y = w = hgt = 0;
        if (h == IntPtr.Zero || IsIconic(h) || !GetWindowRect(h, out var r)) return false;
        x = r.Left; y = r.Top; w = r.Right - r.Left; hgt = r.Bottom - r.Top;
        return w > 0 && hgt > 0;
    }

    /// <summary>
    /// Puts the window exactly here at exactly this size. SWP_NOSENDCHANGING skips the "may I?" message, so Roblox
    /// can't hold the window to its 800×600 minimum: smaller sizes stick (tested: 420×280 stays 420×280).
    /// </summary>
    public static void Place(IntPtr h, int x, int y, int w, int hgt)
    {
        if (h == IntPtr.Zero) return;
        if (IsIconic(h) || IsZoomed(h)) ShowWindow(h, 9);   // minimized or maximized: back to a normal window first
        SetBounds(h, x, y, w, hgt);
    }

    /// <summary>Same as Place, but leaves a minimized/normal state alone and never activates the window.</summary>
    public static void SetBounds(IntPtr h, int x, int y, int w, int hgt)
    {
        if (h == IntPtr.Zero) return;
        WindowSizeKeeper.Current?.Remember(h, w, hgt);   // so dragging it later keeps this size
        // NOZORDER | NOACTIVATE | NOSENDCHANGING
        if (!SetWindowPos(h, IntPtr.Zero, x, y, Math.Max(1, w), Math.Max(1, hgt), 0x0004 | 0x0010 | 0x0400))
            MoveWindow(h, x, y, w, hgt, true);
    }

    /// <summary>Process ID that owns the window the user is working in right now (0 if none).</summary>
    public static int ForegroundPid()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(h, out var pid);
        return (int)pid;
    }

    /// <summary>Posts a key press to a window without bringing it to the front.</summary>
    public static void KeyTap(IntPtr h, int vk)
    {
        if (h == IntPtr.Zero) return;
        PostMessage(h, 0x0100, (IntPtr)vk, IntPtr.Zero);
        PostMessage(h, 0x0101, (IntPtr)vk, unchecked((IntPtr)(int)0xC0000001));
    }

    /// <summary>
    /// Puts a process in its own job object with a hard CPU cap. Returns the job handle (keep it to change the cap
    /// later), or IntPtr.Zero if Windows refused (for example the process is already in a job that forbids it).
    /// The job never kills the process when RoDeck closes.
    /// </summary>
    public static IntPtr CreateCpuCappedJob(int pid, int percent)
    {
        var proc = OpenProcess(ProcessSetQuota | ProcessTerminate, false, pid);
        if (proc == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            if (!AssignProcessToJobObject(job, proc) || !SetCpuCap(job, percent))
            {
                CloseHandle(job);
                return IntPtr.Zero;
            }
            return job;
        }
        finally { CloseHandle(proc); }
    }

    /// <summary>Changes the cap on a job from CreateCpuCappedJob. 100 or more means no limit.</summary>
    public static bool SetCpuCap(IntPtr job, int percent)
    {
        if (job == IntPtr.Zero) return false;
        var info = new CpuRateControl
        {
            ControlFlags = CpuRateEnable | CpuRateHardCap,
            CpuRate = (uint)Math.Clamp(percent, 1, 100) * 100
        };
        return SetInformationJobObject(job, JobObjectCpuRateControlInformation, ref info, Marshal.SizeOf<CpuRateControl>());
    }

    public static void CloseJob(IntPtr job) { if (job != IntPtr.Zero) CloseHandle(job); }
}
