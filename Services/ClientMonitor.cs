using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media;
using AltDeck.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AltDeck.Services;

public partial class ClientInfo : ObservableObject
{
    public int Pid { get; init; }
    public string Key => Pid.ToString();
    public Account? Owner { get; init; }
    public LaunchTarget? Target { get; init; }
    public bool StoppedByUser { get; set; }
    /// <summary>Started from Multi-launch (farming): auto-rejoin / auto-restart may bring it back. Clients started from
    /// Home or Games (AltDeck used as a normal launcher) are left alone when they close or freeze.</summary>
    public bool Farm { get; init; }
    /// <summary>Ended by auto-restart because it was "Not responding" (so it isn't counted as a drop as well).</summary>
    public bool EndedAsFrozen { get; set; }
    /// <summary>Ended because Roblox showed its "Disconnected" box (already logged with the error code, so not a second drop).</summary>
    public bool EndedAsDisconnected { get; set; }
    /// <summary>When the process started (UTC), used to find its Roblox log.</summary>
    public DateTime? StartedAtUtc { get; set; }
    /// <summary>The server (job ID) it last joined, read from its Roblox log. Used to rejoin the same server first.</summary>
    public string? JoinedJobId { get; set; }
    /// <summary>Roblox's error code when AltDeck closed it for a disconnect (0 = none / never joined).</summary>
    public int DisconnectCode { get; set; }
    /// <summary>Where its window last was while not minimized, so a relaunched client can go back to the same spot.</summary>
    public (int X, int Y, int W, int H)? LastRect { get; set; }

    public string Name => Owner?.Label ?? "Unknown account";
    public string Initials => Owner?.Initials ?? "?";
    public string AvatarUrl => Owner?.AvatarUrl ?? "";
    public string StatusText => IsClosed ? "Disconnected" : "In game";
    public string StatusKind => IsClosed ? "signedout" : "ingame";
    public bool IsAlive => !IsClosed;

    [ObservableProperty] private double cpu;
    [ObservableProperty] private double memoryMb;
    [ObservableProperty] private double gpu;            // % of the busiest GPU engine this client uses
    [ObservableProperty] private double gpuMemoryMb;    // dedicated video memory this client holds

    /// <summary>Last picture of the client's window (null until the first one was taken).</summary>
    [ObservableProperty] private ImageSource? preview;
    /// <summary>Short line over the picture: why it is not live right now. Empty when it is live.</summary>
    [ObservableProperty] private string previewNote = "Waiting for the window…";
    /// <summary>When the window was first seen, used to give it a moment to draw before it is minimized.</summary>
    public DateTime? WindowSeenAt { get; set; }
    /// <summary>Since when the window has been "Not responding" (null while it answers).</summary>
    public DateTime? HungSince { get; set; }
    [ObservableProperty] private string uptime = "0m";
    [ObservableProperty] private bool isTabSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAlive))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusKind))]
    [NotifyPropertyChangedFor(nameof(PlaceText))]
    private bool isClosed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaceText))]
    private string placeName = "";

    public string PlaceText
    {
        get
        {
            var place = !string.IsNullOrEmpty(PlaceName) ? PlaceName
                      : Target != null ? "Place " + Target.PlaceId : "Roblox";
            return IsClosed ? place : place + " · " + Uptime;
        }
    }

    partial void OnUptimeChanged(string value) => OnPropertyChanged(nameof(PlaceText));
}

public sealed class ClientMonitor
{
    public ObservableCollection<ClientInfo> Clients { get; } = new();
    public event Action<ClientInfo>? ClientDropped;

    private readonly Dictionary<int, (TimeSpan Cpu, DateTime At)> _prev = new();
    private readonly Queue<(Account Acc, LaunchTarget Target, DateTime At, bool Farm)> _pending = new();

    public double TotalCpu => Clients.Where(c => c.IsAlive).Sum(c => c.Cpu);
    public double TotalMemoryMb => Clients.Where(c => c.IsAlive).Sum(c => c.MemoryMb);
    public double TotalGpu => Clients.Where(c => c.IsAlive).Sum(c => c.Gpu);
    public double TotalGpuMemoryMb => Clients.Where(c => c.IsAlive).Sum(c => c.GpuMemoryMb);

    /// <summary>Call right after launching: the next new Roblox process is assigned to this account.</summary>
    public void ExpectLaunch(Account a, LaunchTarget t, bool farm = true) => _pending.Enqueue((a, t, DateTime.UtcNow, farm));

    public void Tick()
    {
        var procs = Process.GetProcessesByName("RobloxPlayerBeta");
        try
        {
            var alive = procs.Select(p => p.Id).ToHashSet();

            foreach (var c in Clients.ToList())
            {
                if (c.IsClosed || alive.Contains(c.Pid)) continue;
                _prev.Remove(c.Pid);
                if (c.Owner != null && !c.StoppedByUser)
                {
                    c.IsClosed = true;      // keep the card so it can be played again
                    c.Cpu = 0;
                    c.MemoryMb = 0;
                    c.Gpu = 0;
                    c.GpuMemoryMb = 0;
                    c.PreviewNote = "Disconnected · last picture";
                    ClientDropped?.Invoke(c);
                }
                else Clients.Remove(c);
            }

            while (_pending.Count > 0 && DateTime.UtcNow - _pending.Peek().At > TimeSpan.FromSeconds(90))
                _pending.Dequeue();   // that launch never produced a client

            foreach (var p in procs.OrderBy(StartOf))
            {
                if (Clients.Any(c => c.Pid == p.Id)) continue;
                DateTime? started = null;
                try { started = p.StartTime.ToUniversalTime(); } catch { }
                if (_pending.Count > 0)
                {
                    var (acc, target, _, farm) = _pending.Dequeue();
                    Clients.Add(new ClientInfo { Pid = p.Id, Owner = acc, Target = target, StartedAtUtc = started, Farm = farm });
                }
                else Clients.Add(new ClientInfo { Pid = p.Id, StartedAtUtc = started });
            }

            var now = DateTime.UtcNow;
            foreach (var p in procs)
            {
                var c = Clients.FirstOrDefault(x => x.Pid == p.Id);
                if (c == null) continue;
                try { p.Refresh(); } catch { continue; }

                try
                {
                    var cpuTime = p.TotalProcessorTime;
                    if (_prev.TryGetValue(p.Id, out var prev))
                    {
                        var secs = (now - prev.At).TotalSeconds;
                        if (secs > 0)
                            c.Cpu = Math.Max(0, (cpuTime - prev.Cpu).TotalSeconds / secs / Environment.ProcessorCount * 100);
                    }
                    _prev[p.Id] = (cpuTime, now);
                }
                catch { }

                try { c.MemoryMb = p.WorkingSet64 / 1048576.0; } catch { }

                // "Not responding" window: remember since when (a minimized or still-loading window has no handle and counts as fine)
                try
                {
                    var hwnd = p.MainWindowHandle;
                    if (Win32.IsHung(hwnd)) c.HungSince ??= DateTime.UtcNow;
                    else c.HungSince = null;
                    if (c.Owner != null && Win32.TryGetRect(hwnd, out var rx, out var ry, out var rw, out var rh) && rw > 100 && rh > 80)
                        c.LastRect = (rx, ry, rw, rh);
                }
                catch { }

                try
                {
                    var up = DateTime.Now - p.StartTime;
                    c.Uptime = up.TotalHours >= 1 ? $"{(int)up.TotalHours}h {up.Minutes}m" : $"{up.Minutes}m";
                }
                catch { }
            }
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    private static DateTime StartOf(Process p)
    {
        try { return p.StartTime; } catch { return DateTime.MaxValue; }
    }

    /// <summary>Asks the window to close, then ends the process after the grace period.</summary>
    public async Task StopAsync(ClientInfo c, int graceSeconds)
    {
        c.StoppedByUser = true;
        try
        {
            using var p = Process.GetProcessById(c.Pid);
            if (graceSeconds > 0 && p.MainWindowHandle != IntPtr.Zero)
            {
                p.CloseMainWindow();
                var exited = await Task.Run(() => p.WaitForExit(graceSeconds * 1000));
                if (exited) return;
            }
            p.Kill();
        }
        catch { }
    }

    /// <summary>
    /// Ends a frozen client WITHOUT marking it as stopped by the user, so the next Tick reports it as dropped
    /// and auto-restart can bring it back.
    /// </summary>
    public void KillHung(ClientInfo c)
    {
        c.HungSince = null;
        c.EndedAsFrozen = true;
        try { using var p = Process.GetProcessById(c.Pid); p.Kill(); } catch { }
    }

    /// <summary>
    /// Ends a client stuck on Roblox's "Disconnected" box WITHOUT marking it as stopped by the user,
    /// so the next Tick reports it as dropped and auto-rejoin brings it back.
    /// </summary>
    public void KillDisconnected(ClientInfo c)
    {
        c.EndedAsDisconnected = true;
        try { using var p = Process.GetProcessById(c.Pid); p.Kill(); } catch { }
    }

    public void KillNow(ClientInfo c)
    {
        c.StoppedByUser = true;
        try { using var p = Process.GetProcessById(c.Pid); p.Kill(); } catch { }
    }

    public void TileWindows(double areaX, double areaY, double areaW, double areaH)
    {
        var live = Clients.Where(c => c.IsAlive).ToList();
        if (live.Count == 0) return;
        int cols = (int)Math.Ceiling(Math.Sqrt(live.Count));
        int rows = (int)Math.Ceiling(live.Count / (double)cols);
        int w = (int)(areaW / cols), h = (int)(areaH / rows);
        int ax = (int)areaX, ay = (int)areaY, aw = (int)areaW, ah = (int)areaH;
        for (int i = 0; i < live.Count; i++)
        {
            var hwnd = Win32.MainWindowOf(live[i].Pid);
            int col = i % cols, row = i / cols;
            Win32.Place(hwnd, ax + col * w, ay + row * h, w, h);

            // Roblox won't go smaller than 800×600 inside (816×638 with the frame). When the window came out bigger
            // than its cell, spread the windows so they overlap a little instead of running off this screen.
            if (!Win32.TryGetRect(hwnd, out _, out _, out var realW, out var realH)) continue;
            if (realW <= w && realH <= h) continue;
            int stepX = cols > 1 ? Math.Min(w, Math.Max(0, (aw - realW) / (cols - 1))) : 0;
            int stepY = rows > 1 ? Math.Min(h, Math.Max(0, (ah - realH) / (rows - 1))) : 0;
            Win32.Place(hwnd, ax + col * stepX, ay + row * stepY, Math.Min(realW, aw), Math.Min(realH, ah));
        }
    }

    public void MinimizeAll()
    {
        foreach (var c in Clients.Where(c => c.IsAlive)) Win32.Minimize(Win32.MainWindowOf(c.Pid));
    }

    /// <summary>
    /// Brings every client window back. Windows are shown without fighting over focus, then checked again:
    /// any that is still minimized (a slow, CPU-limited client) is retried, up to 5 rounds.
    /// Returns how many are still minimized at the end (normally 0).
    /// </summary>
    public async Task<int> ShowAllAsync()
    {
        var all = Clients.Where(c => c.IsAlive).Select(c => Win32.MainWindowOf(c.Pid)).Where(h => h != IntPtr.Zero).ToList();
        foreach (var h in all) Win32.ShowQuietly(h);   // everything at once, right away

        // Then, in the background: retry any slow (CPU-limited) client that is still minimized, and put back the
        // small size of a window Roblox grew to its 800×600 minimum while restoring it.
        var left = all;
        for (int round = 0; round < 5; round++)
        {
            await Task.Delay(round == 0 ? 250 : 400);
            foreach (var h in all) WindowSizeKeeper.Current?.KeepWantedSize(h);
            left = left.Where(Win32.IsMinimized).ToList();
            if (left.Count == 0) break;
            foreach (var h in left) Win32.ShowQuietly(h);
        }
        return left.Count;
    }

    public void Show(ClientInfo c) => Win32.Restore(Win32.MainWindowOf(c.Pid));
    public void Minimize(ClientInfo c) => Win32.Minimize(Win32.MainWindowOf(c.Pid));
    public void AntiAfkPulse()
    {
        foreach (var c in Clients.Where(c => c.IsAlive)) Win32.KeyTap(Win32.MainWindowOf(c.Pid), 0x7E);
    }
}
