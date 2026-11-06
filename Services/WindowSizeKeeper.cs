using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AltDeck.Services;

/// <summary>
/// Roblox holds its window to at least 800×600 inside (816×638 with the frame): a small client jumps back to that size
/// as soon as it's dragged by its title bar.
///
/// AltDeck can't change that rule inside Roblox (that would mean injecting code into it). Instead it watches, from
/// outside, for the moment the user lets go of a Roblox window after moving or resizing it (a WinEvent hook, the same
/// kind screen readers use), and then sets the window to what the mouse asked for:
///   • moved: the size it had before the drag, at the place it was dropped;
///   • resized: the grabbed edges where the mouse was let go, even below 800×600 (but never under MinW × MinH).
///
/// Measured on Roblox 0.745:
///   • Roblox applies its own size up to ~1 s AFTER the drag ends, so the wanted size is held for two seconds.
///   • Windows drops these events when the listening thread is busy (WPF's UI thread often is), so the hook runs on
///     its own thread that does nothing else, and each window's last wanted size is remembered as a fallback in case
///     the "drag started" event is lost anyway.
/// </summary>
public sealed class WindowSizeKeeper : IDisposable
{
    const int MinW = 200, MinH = 150;    // smallest a window can be made (frame included)
    const int EdgeGrab = 10;             // how close to an edge (px) a press counts as grabbing that edge
    const int RobloxMinW = 816, RobloxMinH = 638;
    const uint EventMoveSizeStart = 0x000A, EventMoveSizeEnd = 0x000B;
    const uint OutOfContext = 0x0000, SkipOwnProcess = 0x0002;
    const uint WmQuit = 0x0012;

    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);

    readonly WinEventProc _proc;   // kept in a field so the garbage collector doesn't free it while Windows still calls it
    readonly Thread _thread;
    volatile uint _threadId;

    // drag in progress (only touched on the hook thread)
    IntPtr _dragWindow;
    RECT _startRect;
    POINT _startCursor;
    bool _left, _right, _top, _bottom;   // which edges were grabbed (none = a move)

    /// <summary>The size each window was last set to on purpose, used when the "drag started" event was lost. Locked.</summary>
    readonly Dictionary<IntPtr, (int W, int H)> _wanted = new();
    readonly Dictionary<uint, bool> _isRoblox = new();   // pid -> is a Roblox client (looked up once; hook thread only)
    int _generation;                                      // a new drag cancels the previous hold

    /// <summary>The running keeper (null when none), so every placement in Win32 can tell it the size it chose.</summary>
    public static WindowSizeKeeper? Current { get; private set; }

    public WindowSizeKeeper()
    {
        Current = this;
        _proc = OnEvent;
        _thread = new Thread(Run) { IsBackground = true, Name = "AltDeck window size keeper" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }


    void Run()
    {
        _threadId = GetCurrentThreadId();
        WarmUp();
        var hook = SetWinEventHook(EventMoveSizeStart, EventMoveSizeEnd, IntPtr.Zero, _proc, 0, 0, OutOfContext | SkipOwnProcess);
        if (hook == IntPtr.Zero) return;
        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        finally { UnhookWinEvent(hook); }
    }

    /// <summary>
    /// Compiles the event code before the hook is set. Windows makes the dragged window wait while the callback runs,
    /// and the first call (compiling it, loading the process lookup) was slow enough that a drag's mouse release got lost.
    /// </summary>
    void WarmUp()
    {
        try
        {
            foreach (var name in new[] { nameof(OnEvent), nameof(Start), nameof(End), nameof(IsRobloxWindow), nameof(HoldAsync) })
            {
                var m = typeof(WindowSizeKeeper).GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (m != null) System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(m.MethodHandle);
            }
            using (var self = Process.GetCurrentProcess()) _ = self.ProcessName;
            GetWindowRect(IntPtr.Zero, out _);
            GetCursorPos(out _);
            var cls = new StringBuilder(32);
            GetClassName(IntPtr.Zero, cls, cls.Capacity);
            GetWindowThreadProcessId(IntPtr.Zero, out _);
        }
        catch { }
    }

    /// <summary>Kept as short as possible: Windows drops events while this runs long.</summary>
    void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (idObject != 0 || !IsRobloxWindow(hwnd)) return;   // 0 = the window itself
            if (ev == EventMoveSizeStart) Start(hwnd);
            else if (ev == EventMoveSizeEnd) End(hwnd);
        }
        catch { }
    }

    void Start(IntPtr hwnd)
    {
        _dragWindow = IntPtr.Zero;
        Interlocked.Increment(ref _generation);   // stop holding the previous drag's size
        if (!GetWindowRect(hwnd, out var r) || !GetCursorPos(out var c)) return;

        // Roblox grows a small window to its minimum the moment the drag starts, before this event arrives.
        // If that just happened, the window's real starting size is the small one remembered for it.
        if (Math.Abs(r.Right - r.Left - RobloxMinW) <= 2 && Math.Abs(r.Bottom - r.Top - RobloxMinH) <= 2)
        {
            (int W, int H) want;
            bool known;
            lock (_wanted) known = _wanted.TryGetValue(hwnd, out want);
            if (known && (want.W < RobloxMinW - 2 || want.H < RobloxMinH - 2))
            {
                r.Right = r.Left + want.W;
                r.Bottom = r.Top + want.H;
            }
        }

        _dragWindow = hwnd;
        _startRect = r;
        _startCursor = c;
        // The resize zone sits at the window's edge (inside GetWindowRect's invisible border).
        _left = Math.Abs(c.X - r.Left) <= EdgeGrab;
        _right = Math.Abs(c.X - r.Right) <= EdgeGrab;
        _top = Math.Abs(c.Y - r.Top) <= EdgeGrab / 2;   // the top edge is thin: the title bar starts right under it
        _bottom = Math.Abs(c.Y - r.Bottom) <= EdgeGrab;
    }

    void End(IntPtr hwnd)
    {
        bool sawStart = hwnd == _dragWindow;
        _dragWindow = IntPtr.Zero;
        if (!GetWindowRect(hwnd, out var now) || !GetCursorPos(out var c)) return;

        int x, y, w, h;
        if (sawStart)
        {
            int dx = c.X - _startCursor.X, dy = c.Y - _startCursor.Y;
            var s = _startRect;
            x = s.Left; y = s.Top; w = s.Right - s.Left; h = s.Bottom - s.Top;
            if (!_left && !_right && !_top && !_bottom)
            {
                // Moved: same size, where it was dropped. The window's own top-left is exact (Roblox grows it from there);
                // adding up the mouse movement can be a step short when "drag started" arrived a moment late.
                x = now.Left; y = now.Top;
            }
            else
            {
                // resized: each grabbed edge goes where the mouse was let go
                if (_left) { int right = x + w; x = Math.Min(x + dx, right - MinW); w = right - x; }
                if (_right) w = Math.Max(MinW, w + dx);
                if (_top) { int bottom = y + h; y = Math.Min(y + dy, bottom - MinH); h = bottom - y; }
                if (_bottom) h = Math.Max(MinH, h + dy);
            }
        }
        else
        {
            // "Drag started" was lost. If this window was small and Roblox just grew it to its minimum, it was a move:
            // keep it where it is now, at the small size it had.
            (int W, int H) want;
            lock (_wanted) if (!_wanted.TryGetValue(hwnd, out want)) return;
            int nw = now.Right - now.Left, nh = now.Bottom - now.Top;
            if (want.W >= RobloxMinW && want.H >= RobloxMinH) return;
            if (nw < RobloxMinW - 2 && nh < RobloxMinH - 2) return;   // still small: nothing to fix
            x = now.Left; y = now.Top; w = want.W; h = want.H;
        }

        lock (_wanted) _wanted[hwnd] = (w, h);
        _ = HoldAsync(hwnd, x, y, w, h, Interlocked.Increment(ref _generation));
    }

    /// <summary>Roblox sets its own size up to ~1 s after the drag ends: put the wanted bounds back a few times over two seconds.</summary>
    async Task HoldAsync(IntPtr hwnd, int x, int y, int w, int h, int generation)
    {
        foreach (var wait in new[] { 0, 120, 250, 400, 600, 900 })
        {
            if (wait > 0) await Task.Delay(wait).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _generation)) return;
            if (!GetWindowRect(hwnd, out var r)) return;
            bool right = Math.Abs(r.Left - x) <= 2 && Math.Abs(r.Top - y) <= 2 &&
                         Math.Abs(r.Right - r.Left - w) <= 2 && Math.Abs(r.Bottom - r.Top - h) <= 2;
            if (!right) Win32.SetBounds(hwnd, x, y, w, h);
        }
    }

    /// <summary>Tells the keeper a window was given this size on purpose (Start tiny, tiling, layouts), for the lost-event fallback.</summary>
    /// <summary>If Roblox grew this window to its minimum (e.g. while restoring it), sets it back to the remembered small size, same top-left.</summary>
    public void KeepWantedSize(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return;
        (int W, int H) want;
        lock (_wanted) if (!_wanted.TryGetValue(hwnd, out want)) return;
        if (want.W >= RobloxMinW - 2 && want.H >= RobloxMinH - 2) return;
        if (Math.Abs(r.Right - r.Left - RobloxMinW) > 2 || Math.Abs(r.Bottom - r.Top - RobloxMinH) > 2) return;
        Win32.SetBounds(hwnd, r.Left, r.Top, want.W, want.H);
    }

    public void Remember(IntPtr hwnd, int w, int h)
    {
        if (hwnd == IntPtr.Zero) return;
        lock (_wanted) _wanted[hwnd] = (w, h);
    }

    bool IsRobloxWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (!_isRoblox.TryGetValue(pid, out var isRoblox))
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                isRoblox = p.ProcessName.Equals("RobloxPlayerBeta", StringComparison.OrdinalIgnoreCase);
            }
            catch { isRoblox = false; }
            if (_isRoblox.Count > 500) _isRoblox.Clear();
            _isRoblox[pid] = isRoblox;
        }
        if (!isRoblox) return false;
        var cls = new StringBuilder(32);
        GetClassName(hwnd, cls, cls.Capacity);
        return cls.ToString() == "WINDOWSCLIENT";
    }

    public void Dispose()
    {
        if (Current == this) Current = null;
        if (_threadId != 0) PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
    }
}
