using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AltDeck.Services;

/// <summary>
/// Reads the log each Roblox client writes to %LocalAppData%\Roblox\logs and reports when a client that is still
/// running has lost its game (the grey "Disconnected … Error Code: 277" box). The process stays open on that box,
/// so the process-exit check in ClientMonitor never sees it.
///
/// What the logs look like (Roblox 0.745):
///   ! Joining game '&lt;job id&gt;' place &lt;place id&gt; at &lt;ip&gt;           (every join, also after a teleport)
///   Report game_join_loadtime: placeid:…, userid:&lt;user id&gt;, …        (tells which account the log belongs to)
///   Sending disconnect with reason: 277                               (connection lost; 278 etc. are drops too)
///   Sending disconnect with reason: 285                               (the client left by itself: a teleport, or Leave)
///   [DFLog::NetworkClient] Connection accepted from &lt;ip&gt;|&lt;port&gt;      (the server let it in: really in the game)
///   [DFLog::NetworkClient] Failed to connect to server at …, no response. ("Connection Failed … Error Code: 279";
///                                                                     no disconnect line follows, the box just stays)
/// A disconnect is only reported when no new join follows it within a few seconds and the process is still alive.
/// </summary>
public sealed class RobloxLogWatcher
{
    /// <summary>A client is stuck on Roblox's "Disconnected" box. The int is Roblox's error code (0 = it never joined).</summary>
    public event Action<ClientInfo, int>? Disconnected;

    /// <summary>Reported for a client whose sign-in stopped working (Roblox shows "Teleport Failed … Error Code: 769").</summary>
    public const int SignedOutCode = 769;
    /// <summary>Roblox's "Connection Failed … No response from server" (the server it was sent to is gone or unreachable).</summary>
    public const int NoResponseCode = 279;

    /// <summary>The client left by itself or was moved: not a drop.</summary>
    static readonly HashSet<int> NotADrop = new() { 285 };

    static readonly Regex DisconnectRx = new(@"(?:Sending disconnect with reason|Disconnection Notification\. Reason):\s*(\d+)", RegexOptions.Compiled);
    static readonly Regex LostRx = new(@"Lost connection with reason", RegexOptions.Compiled);
    static readonly Regex JoinRx = new(@"! Joining game '([0-9a-fA-F-]{36})' place (\d+)", RegexOptions.Compiled);
    static readonly Regex ConnectedRx = new(@"\[DFLog::NetworkClient\] Connection accepted from", RegexOptions.Compiled);
    static readonly Regex ConnectFailRx = new(@"\[DFLog::NetworkClient\] Failed to connect to server", RegexOptions.Compiled);
    static readonly Regex UserRx = new(@"userid:(\d+)", RegexOptions.Compiled);
    static readonly Regex StampRx = new(@"_(\d{8}T\d{6})Z_Player_", RegexOptions.Compiled);

    /// <summary>How long a disconnect has to stand (no new join, process still open) before it counts.</summary>
    static readonly TimeSpan Confirm = TimeSpan.FromSeconds(5);
    /// <summary>A client AltDeck started that still hasn't joined a server after this long is treated as a failed join.</summary>
    static readonly TimeSpan NeverJoined = TimeSpan.FromMinutes(3);
    /// <summary>
    /// A join (or teleport) the server hasn't accepted after this long is stuck, whatever box Roblox shows.
    /// Roblox itself gives up after about 20 s, so this is only a safety net for anything not recognised above.
    /// </summary>
    static readonly TimeSpan StuckJoining = TimeSpan.FromSeconds(90);

    public static string LogFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "logs");

    sealed class Tracked
    {
        public required string Path;
        public long Pos;
        public string Rest = "";
        public long UserId;
        public bool Joined;
        public DateTime? JoiningSince;   // a join line with no "Connection accepted" after it yet
        public DateTime? DisconnectAt;
        public int DisconnectCode;
        public bool Reported;
        public int Unauthorized;     // "401 Unauthorized" answers seen
        public bool SignedOut;       // this client's session no longer works (teleports fail with Error 769)
    }

    /// <summary>A working client gets no 401 at all; this many means its session was signed out.</summary>
    const int SignedOutAfter401s = 5;

    readonly Dictionary<int, Tracked> _byPid = new();
    readonly HashSet<string> _claimed = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _rejected = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Set once any log showed a join line, so the "never joined" rule can't fire if Roblox ever changes its log format.</summary>
    bool _formatSeen;
    /// <summary>Set once any log showed "Connection accepted", so the stuck-joining rule only runs when Roblox still logs it.</summary>
    bool _connectSeen;

    /// <summary>Call every tick with the current clients (UI thread).</summary>
    public void Poll(IEnumerable<ClientInfo> clients)
    {
        var live = clients.Where(c => c.IsAlive && c.Owner != null && !c.StoppedByUser).ToList();

        foreach (var pid in _byPid.Keys.Where(p => !live.Any(c => c.Pid == p)).ToList())
        {
            _claimed.Remove(_byPid[pid].Path);
            _byPid.Remove(pid);
        }

        if (live.Any(c => !_byPid.ContainsKey(c.Pid))) Associate(live.Where(c => !_byPid.ContainsKey(c.Pid)).ToList());

        var now = DateTime.UtcNow;
        foreach (var c in live)
        {
            if (!_byPid.TryGetValue(c.Pid, out var t)) continue;
            try { Read(c, t); } catch { }
            if (t.Reported) continue;

            // a log that turns out to belong to another account is let go, and the client gets matched again
            if (t.UserId != 0 && c.Owner!.UserId != 0 && t.UserId != c.Owner.UserId)
            {
                _rejected.Add(t.Path);
                _claimed.Remove(t.Path);
                _byPid.Remove(c.Pid);
                continue;
            }

            if (t.SignedOut)
            {
                t.Reported = true;
                Disconnected?.Invoke(c, SignedOutCode);
            }
            else if (t.DisconnectAt is DateTime at && now - at >= Confirm)
            {
                t.Reported = true;
                Disconnected?.Invoke(c, t.DisconnectCode);
            }
            else if (_connectSeen && t.JoiningSince is DateTime js && now - js >= StuckJoining)
            {
                t.Reported = true;
                Disconnected?.Invoke(c, NoResponseCode);
            }
            else if (!t.Joined && _formatSeen && c.StartedAtUtc is DateTime started && now - started >= NeverJoined)
            {
                t.Reported = true;
                Disconnected?.Invoke(c, 0);
            }
        }
    }

    /// <summary>Lets a client be reported again (it rejoined by itself, e.g. the user clicked Reconnect).</summary>
    void Rearm(Tracked t)
    {
        t.DisconnectAt = null;
        t.DisconnectCode = 0;
        t.Reported = false;
    }

    /// <summary>Matches each new client to the log Roblox created when its process started (same few seconds).</summary>
    void Associate(List<ClientInfo> clients)
    {
        string[] files;
        try { files = Directory.GetFiles(LogFolder, "*_Player_*.log"); }
        catch { return; }

        var candidates = new List<(string Path, DateTime Stamp)>();
        foreach (var f in files)
        {
            var name = Path.GetFileName(f);
            if (name.Contains("CrashHandler", StringComparison.OrdinalIgnoreCase)) continue;
            if (_claimed.Contains(f) || _rejected.Contains(f)) continue;
            var m = StampRx.Match(name);
            if (!m.Success) continue;
            if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var stamp)) continue;
            candidates.Add((f, stamp));
        }

        foreach (var c in clients.Where(c => c.StartedAtUtc != null).OrderBy(c => c.StartedAtUtc))
        {
            var start = c.StartedAtUtc!.Value;
            var best = candidates
                .Where(x => !_claimed.Contains(x.Path) && x.Stamp >= start.AddSeconds(-5) && x.Stamp <= start.AddSeconds(60))
                .OrderBy(x => Math.Abs((x.Stamp - start).TotalSeconds))
                .Select(x => x.Path)
                .FirstOrDefault();
            if (best == null) continue;

            long len = 0;
            try { len = new FileInfo(best).Length; } catch { }
            _claimed.Add(best);
            // a fresh client's log is small; only skip ahead in a big one (e.g. AltDeck matched it late)
            _byPid[c.Pid] = new Tracked { Path = best, Pos = len > 4_000_000 ? len - 256_000 : 0 };
        }
    }

    void Read(ClientInfo c, Tracked t)
    {
        using var fs = new FileStream(t.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < t.Pos) { t.Pos = 0; t.Rest = ""; }          // rewritten: start over
        if (fs.Length - t.Pos > 8_000_000) { t.Pos = fs.Length - 1_000_000; t.Rest = ""; }   // far behind: only the recent part matters
        if (fs.Length == t.Pos) return;

        fs.Seek(t.Pos, SeekOrigin.Begin);
        var buf = new byte[fs.Length - t.Pos];
        int read = 0;
        while (read < buf.Length)
        {
            int n = fs.Read(buf, read, buf.Length - read);
            if (n <= 0) break;
            read += n;
        }
        t.Pos += read;

        var text = t.Rest + Encoding.UTF8.GetString(buf, 0, read);
        var lastNl = text.LastIndexOf('\n');
        if (lastNl < 0) { t.Rest = text; return; }
        t.Rest = text.Substring(lastNl + 1);

        foreach (var raw in text.AsSpan(0, lastNl).ToString().Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Contains("WndProcessCheck", StringComparison.Ordinal)) continue;   // the bulk of the file, never useful here

            var j = JoinRx.Match(line);
            if (j.Success)
            {
                t.Joined = true;
                _formatSeen = true;
                c.JoinedJobId = j.Groups[1].Value;
                t.JoiningSince = DateTime.UtcNow;
                Rearm(t);   // joined again (teleport, or the user pressed Reconnect): any earlier disconnect is over
                continue;
            }

            if (ConnectedRx.IsMatch(line))
            {
                t.JoiningSince = null;
                _connectSeen = true;
                continue;
            }
            if (ConnectFailRx.IsMatch(line))
            {
                // the server never answered: Roblox shows "Connection Failed (Error Code: 279)" and waits for a click
                t.JoiningSince = null;
                t.DisconnectAt ??= DateTime.UtcNow;
                if (t.DisconnectCode == 0) t.DisconnectCode = NoResponseCode;
                continue;
            }

            // Signed out: Roblox answers 401 to this client's requests, and joining/teleporting fails.
            if (line.Contains("status:401", StringComparison.Ordinal))
            {
                t.Unauthorized++;
                if (t.Unauthorized >= SignedOutAfter401s || line.Contains("gamejoin.roblox.com", StringComparison.Ordinal))
                    t.SignedOut = true;
                continue;
            }
            if (line.Contains("raiseTeleportInitFailedEvent", StringComparison.Ordinal) && line.Contains("Http error", StringComparison.Ordinal))
            {
                t.SignedOut = true;
                continue;
            }

            if (t.UserId == 0)
            {
                var u = UserRx.Match(line);
                if (u.Success && line.Contains("game_join_loadtime", StringComparison.Ordinal)) long.TryParse(u.Groups[1].Value, out t.UserId);
            }

            var d = DisconnectRx.Match(line);
            if (d.Success && int.TryParse(d.Groups[1].Value, out var code))
            {
                if (NotADrop.Contains(code)) continue;
                t.DisconnectAt ??= DateTime.UtcNow;
                if (t.DisconnectCode == 0) t.DisconnectCode = code;
                continue;
            }

            if (LostRx.IsMatch(line))
            {
                t.DisconnectAt ??= DateTime.UtcNow;
                if (t.DisconnectCode == 0) t.DisconnectCode = 277;
            }
        }
    }
}
