using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using RoDeck.Models;
using Microsoft.Win32;

namespace RoDeck.Services;

/// <summary>The user's own copy of one ClientAppSettings.json, so it can be put back.</summary>
public sealed class FlagBackup
{
    public bool Existed { get; set; }
    public string? Original { get; set; }
}

/// <summary>What RoDeck changed in the user's own Roblox files, so it can be put back exactly.</summary>
public sealed class PerfRestoreState
{
    public Dictionary<string, FlagBackup> Flags { get; set; } = new();          // version folder -> backup
    public Dictionary<string, string?> OwnOriginals { get; set; } = new();      // Roblox setting name -> value before (null = absent)
    public Dictionary<string, string> OwnSet { get; set; } = new();             // Roblox setting name -> value RoDeck set
}

/// <summary>
/// Makes the Performance page real:
///  - FPS cap and the graphics flags are written to Roblox as FastFlags (ClientAppSettings.json) just before each launch
///  - "Lowest graphics" sets Roblox's own quality level just before each launch
///  - both are put back a little after the last launch (and on exit / after a crash), so normal Roblox is untouched
///  - running clients: start tiny, minimize on launch, and a CPU limit for the ones you are not looking at
/// Nothing here may ever stop a launch: every failure is swallowed or turned into a short notice.
/// </summary>
public sealed class PerformanceService
{
    private const string LowestLevel = "1";
    private const int RestoreAfterSeconds = 90;
    private const int ProfileSwitchGapSeconds = 8;

    private static readonly string StatePath = Path.Combine(SettingsStore.DataDir, "perf-restore.json");
    private static string GraphicsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "GlobalBasicSettings_13.xml");

    private readonly object _gate = new();
    private readonly System.Threading.Timer _restoreTimer;
    private PerfRestoreState? _state;
    private DateTime _lastLaunch = DateTime.MinValue;
    private bool? _lastWasFull;

    private readonly Dictionary<int, int> _stage = new();          // pid -> 0 new, 1 resized, 2 done
    private readonly Dictionary<int, IntPtr> _jobs = new();         // pid -> job object holding the CPU cap
    private readonly Dictionary<int, int> _capNow = new();          // pid -> cap currently set (100 = none)
    private readonly HashSet<int> _capFailed = new();
    private readonly Dictionary<int, int> _slot = new();           // pid -> position in the side-by-side grid

    public PerformanceService()
    {
        _restoreTimer = new System.Threading.Timer(_ =>
        {
            try { RestoreNow(); } catch { }
        }, null, Timeout.Infinite, Timeout.Infinite);
    }

    // ------------------------------------------------------------------ what applies

    /// <summary>What "Unlimited FPS" writes as the frame-rate target: far above any screen.</summary>
    public const int UnlimitedFpsValue = 9999;

    /// <summary>The flags the current settings ask for. Empty means "leave Roblox alone".</summary>
    public static Dictionary<string, string> BuildFlags(AppSettings s)
    {
        var f = BuildBuiltInFlags(s);
        // the user's own flags last, so they win over RoDeck's
        foreach (var flag in s.CustomFlags)
            if (flag.Enabled && flag.IsValid) f[flag.Name.Trim()] = flag.WrittenValue;
        return f;
    }

    private static Dictionary<string, string> BuildBuiltInFlags(AppSettings s)
    {
        var f = new Dictionary<string, string>();
        if (s.UnlimitedFps)
        {
            f["DFIntTaskSchedulerTargetFps"] = UnlimitedFpsValue.ToString(CultureInfo.InvariantCulture);
            f["FFlagTaskSchedulerLimitTargetFpsTo2402"] = "false";
        }
        else if (s.UsesFpsCap)
            f["DFIntTaskSchedulerTargetFps"] = s.FpsCap.ToString(CultureInfo.InvariantCulture);

        if (!s.LighterRendering) return f;

        if (int.TryParse((s.Msaa ?? "").TrimStart('x', 'X'), out var msaa) && (msaa == 1 || msaa == 2 || msaa == 4 || msaa == 8))
            f["FIntDebugForceMSAASamples"] = msaa.ToString(CultureInfo.InvariantCulture);

        int? tex = s.TextureQuality switch { "Lowest" => 0, "Low" => 1, "Medium" => 2, "High" => 3, _ => null };
        if (tex != null)
        {
            f["DFFlagTextureQualityOverride"] = "true";
            f["DFIntTextureQualityOverride"] = tex.Value.ToString(CultureInfo.InvariantCulture);
        }

        switch (s.RenderingMode)
        {
            case "Direct3D 11": f["FFlagDebugGraphicsPreferD3D11"] = "true"; break;
            case "Vulkan": f["FFlagDebugGraphicsPreferVulkan"] = "true"; break;
            case "OpenGL": f["FFlagDebugGraphicsPreferOpenGL"] = "true"; break;
        }

        if (s.GraySky) f["FFlagDebugSkyGray"] = "true";
        if (s.NoGrass)
        {
            f["FIntFRMMinGrassDistance"] = "0";
            f["FIntFRMMaxGrassDistance"] = "0";
            f["FIntRenderGrassDetailStrands"] = "0";
        }
        return f;
    }

    /// <summary>Name of a custom Roblox launcher (Bloxstrap and the like) if one has taken over roblox-player: links, else null.</summary>
    public static string? CustomLauncher()
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(@"roblox-player\shell\open\command");
            var cmd = key?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(cmd)) return null;
            var lower = cmd.ToLowerInvariant();
            if (lower.Contains("robloxplayerlauncher.exe") || lower.Contains("robloxplayerbeta.exe")) return null;
            foreach (var name in new[] { "Bloxstrap", "Fishstrap", "Voidstrap", "Froststrap", "Lunastrap" })
                if (lower.Contains(name.ToLowerInvariant())) return name;
            return "A custom Roblox launcher";
        }
        catch { return null; }
    }

    /// <summary>Every installed Roblox version folder, so the flags reach whichever one actually starts.</summary>
    public static List<string> FindVersionDirs()
    {
        var result = new List<string>();
        try
        {
            var roots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "Versions"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Roblox", "Versions"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Roblox", "Versions")
            };
            foreach (var root in roots.Where(Directory.Exists))
                result.AddRange(Directory.GetDirectories(root).Where(d => File.Exists(Path.Combine(d, "RobloxPlayerBeta.exe"))));
        }
        catch { }
        return result;
    }

    public static string? FindVersionDir()
    {
        try
        {
            var roots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "Versions"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Roblox", "Versions"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Roblox", "Versions")
            };
            foreach (var root in roots.Where(Directory.Exists))
            {
                var dir = Directory.GetDirectories(root)
                    .Where(d => File.Exists(Path.Combine(d, "RobloxPlayerBeta.exe")))
                    .OrderByDescending(Directory.GetLastWriteTime).FirstOrDefault();
                if (dir != null) return dir;
            }
        }
        catch { }
        return null;
    }

    // ------------------------------------------------------------------ before / after a launch

    /// <summary>
    /// Call right before starting a client for this account. Returns a short notice for the user, or null.
    /// A "Full quality" account gets the user's own settings back instead of the light profile.
    /// </summary>
    public async Task<string?> PrepareLaunchAsync(AppSettings s, Account account)
    {
        try
        {
            _restoreTimer.Change(Timeout.Infinite, Timeout.Infinite);

            bool full = account.FullQuality;

            // Roblox reads its settings while it starts, so give the previous client time to finish
            // before the files change under it.
            if (_lastWasFull != null && _lastWasFull != full)
            {
                var wait = TimeSpan.FromSeconds(ProfileSwitchGapSeconds) - (DateTime.UtcNow - _lastLaunch);
                if (wait > TimeSpan.Zero) await Task.Delay(wait);
            }
            _lastWasFull = full;

            string? notice = null;
            await Task.Run(() =>
            {
                lock (_gate)
                {
                    if (full) { RestoreLocked(); HealOwnSettingsLocked(s); }
                    else notice = ApplyLocked(s);
                }
            });
            return notice;
        }
        catch { return null; }
        finally
        {
            // Failsafe: even if the launch never happens, the user's own files come back.
            ScheduleRestore(RestoreAfterSeconds + 15);
        }
    }

    /// <summary>Call right after a client was started: the files are put back a while later.</summary>
    public void LaunchStarted()
    {
        _lastLaunch = DateTime.UtcNow;
        ScheduleRestore(RestoreAfterSeconds);
    }

    private void ScheduleRestore(int seconds)
    {
        try { _restoreTimer.Change(TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan); } catch { }
    }

    /// <summary>Puts the user's own Roblox settings back (safe to call any time).</summary>
    public void RestoreNow()
    {
        lock (_gate) RestoreLocked();
    }

    /// <summary>If RoDeck was closed or crashed while it had changed Roblox's files, put them back.</summary>
    public void RecoverFromCrash()
    {
        try
        {
            lock (_gate)
            {
                if (!File.Exists(StatePath)) return;
                _state = JsonSerializer.Deserialize<PerfRestoreState>(File.ReadAllText(StatePath));
                RestoreLocked();
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------ the files (call with _gate held)

    private string? ApplyLocked(AppSettings s)
    {
        string? notice = null;

        // Start from a clean slate: put back whatever the previous launch changed, then apply what is wanted now.
        // (The originals are read fresh from disk, so they are always the user's own values.)
        if (_state != null) RestoreLocked();
        var st = new PerfRestoreState();

        var flags = BuildFlags(s);
        var custom = CustomLauncher();
        var dirs = FindVersionDirs();

        // ---- FastFlags (ClientAppSettings.json)
        if (flags.Count > 0 && custom != null)
        {
            notice = $"{custom} keeps its own flags, so RoDeck skipped the graphics flags.";
            flags.Clear();
        }
        if (flags.Count > 0 && dirs.Count == 0)
        {
            notice = "Couldn't find your Roblox install, so the FPS cap and graphics flags were skipped.";
            flags.Clear();
        }
        if (flags.Count > 0)
        {
            foreach (var dir in dirs)
            {
                try { WriteFlags(st, dir, flags); }
                catch (Exception ex) { notice ??= "Couldn't write the Roblox flags: " + ex.Message; }
            }
        }

        // ---- Roblox's own settings (GlobalBasicSettings_13.xml)
        var own = BuildOwnSettings(s);
        if (own.Count > 0)
        {
            try { SetOwnSettings(st, own); }
            catch (Exception ex) { notice ??= "Couldn't change Roblox's own settings: " + ex.Message; }
        }

        _state = st;
        SaveStateLocked();
        return notice;
    }

    /// <summary>Roblox's own settings this launch needs: lowest graphics, the FPS cap, and muted sound.</summary>
    private static List<(string Name, string Type, string Value)> BuildOwnSettings(AppSettings s)
    {
        var l = new List<(string, string, string)>();
        if (s.LowestGraphics)
        {
            l.Add(("GraphicsQualityLevel", "int", LowestLevel));
            l.Add(("SavedQualityLevel", "token", LowestLevel));
        }
        if (s.UsesFpsCap)
            l.Add(("FramerateCap", "int", s.FpsCap.ToString(CultureInfo.InvariantCulture)));
        if (s.MuteOnStart)
            l.Add(("MasterVolume", "float", "0"));
        return l;
    }

    private void RestoreLocked()
    {
        if (_state == null) { DeleteStateFile(); return; }
        try { RestoreFlags(_state); } catch { }
        try { RestoreOwnSettings(_state); } catch { }
        _state = null;
        DeleteStateFile();
    }

    private void SaveStateLocked()
    {
        try
        {
            if (_state == null || (_state.Flags.Count == 0 && _state.OwnSet.Count == 0))
            {
                _state = null;
                DeleteStateFile();
                return;
            }
            Directory.CreateDirectory(SettingsStore.DataDir);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(_state));
        }
        catch { }
    }

    private static void DeleteStateFile()
    {
        try { if (File.Exists(StatePath)) File.Delete(StatePath); } catch { }
    }

    // ---- ClientSettings\ClientAppSettings.json

    private static string FlagsFile(string versionDir) => Path.Combine(versionDir, "ClientSettings", "ClientAppSettings.json");

    private static void WriteFlags(PerfRestoreState st, string versionDir, Dictionary<string, string> flags)
    {
        var path = FlagsFile(versionDir);
        var backup = new FlagBackup { Existed = File.Exists(path) };
        if (backup.Existed) backup.Original = File.ReadAllText(path);

        JsonObject root;
        if (string.IsNullOrWhiteSpace(backup.Original)) root = new JsonObject();
        else root = JsonNode.Parse(backup.Original!) as JsonObject
                    ?? throw new InvalidOperationException("your ClientAppSettings.json isn't a flag list");

        foreach (var kv in flags) root[kv.Key] = kv.Value;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        st.Flags[versionDir] = backup;
    }

    private static void RestoreFlags(PerfRestoreState st)
    {
        foreach (var kv in st.Flags)
        {
            try
            {
                var path = FlagsFile(kv.Key);
                if (kv.Value.Existed && kv.Value.Original != null) File.WriteAllText(path, kv.Value.Original);
                else if (File.Exists(path)) File.Delete(path);

                var folder = Path.GetDirectoryName(path)!;
                if (!kv.Value.Existed && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                    Directory.Delete(folder);
            }
            catch { }
        }
        st.Flags.Clear();
    }

    // ---- GlobalBasicSettings_13.xml (Roblox's own settings)

    private const string EmptySettings =
        "<roblox xmlns:xmime=\"http://www.w3.org/2005/05/xmlmime\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" " +
        "xsi:noNamespaceSchemaLocation=\"http://www.roblox.com/roblox.xsd\" version=\"4\">" +
        "<External>null</External><External>nil</External>" +
        "<Item class=\"UserGameSettings\" referent=\"RBX0\"><Properties></Properties></Item></roblox>";

    private static XElement? SettingsProps(XDocument doc) =>
        doc.Descendants("Item").FirstOrDefault(i => (string?)i.Attribute("class") == "UserGameSettings")?.Element("Properties");

    private static XElement? Entry(XElement props, string name) =>
        props.Elements().FirstOrDefault(e => (string?)e.Attribute("name") == name);

    private static void SaveSettingsXml(XDocument doc)
    {
        var path = GraphicsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var ws = new XmlWriterSettings { OmitXmlDeclaration = true, Encoding = new UTF8Encoding(false) };
        using var w = XmlWriter.Create(path, ws);
        doc.Save(w);
    }

    // Roblox saves the settings it is running with when it exits, so a low-profile client can write RoDeck's values into
    // the user's own file after they were put back. To tell those apart from the user's real values, RoDeck remembers
    // which values it has written, and the real value it found the first time (null = the user had none).
    private sealed class OwnMemory
    {
        public Dictionary<string, string?> Originals { get; set; } = new();
        public Dictionary<string, List<string>> Written { get; set; } = new();
    }

    private static readonly string MemoryPath = Path.Combine(SettingsStore.DataDir, "perf-originals.json");

    private static OwnMemory LoadMemory()
    {
        try { if (File.Exists(MemoryPath)) return JsonSerializer.Deserialize<OwnMemory>(File.ReadAllText(MemoryPath)) ?? new(); }
        catch { }
        return new();
    }

    private static void SaveMemory(OwnMemory m)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DataDir);
            File.WriteAllText(MemoryPath, JsonSerializer.Serialize(m));
        }
        catch { }
    }

    private static bool LooksLikeOurs(OwnMemory m, string name, string? value) =>
        value != null && (m.Written.TryGetValue(name, out var w) && w.Contains(value)
            || (name is "GraphicsQualityLevel" or "SavedQualityLevel" && value == LowestLevel)
            || (name == "FramerateCap" && value is "10" or "30"));

    /// <summary>
    /// For "Full quality" launches: if Roblox's own settings still hold values RoDeck wrote for a light profile
    /// (saved back by an earlier client), put the user's real value back, or remove the entry so Roblox uses its default.
    /// </summary>
    private void HealOwnSettingsLocked(AppSettings s)
    {
        try
        {
            var path = GraphicsFile;
            if (!File.Exists(path)) return;
            var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            var props = SettingsProps(doc);
            if (props == null) return;

            var mem = LoadMemory();
            var current = BuildOwnSettings(s).ToDictionary(x => x.Name, x => x.Value);
            bool changed = false;
            foreach (var name in new[] { "GraphicsQualityLevel", "SavedQualityLevel", "FramerateCap", "MasterVolume" })
            {
                var e = Entry(props, name);
                if (e == null) continue;
                bool ours = LooksLikeOurs(mem, name, e.Value) || (current.TryGetValue(name, out var cv) && cv == e.Value);
                if (!ours) continue;
                if (mem.Originals.TryGetValue(name, out var original) && original != null) e.Value = original;
                else e.Remove();
                changed = true;
            }
            if (changed) SaveSettingsXml(doc);
        }
        catch { }
    }

    private static void SetOwnSettings(PerfRestoreState st, List<(string Name, string Type, string Value)> wanted)
    {
        var path = GraphicsFile;
        XDocument doc = File.Exists(path)
            ? XDocument.Load(path, LoadOptions.PreserveWhitespace)
            : XDocument.Parse(EmptySettings, LoadOptions.PreserveWhitespace);

        var props = SettingsProps(doc) ?? throw new InvalidOperationException("unknown settings file layout");

        var mem = LoadMemory();
        foreach (var (name, type, value) in wanted)
        {
            var e = Entry(props, name);
            var found = e?.Value;
            string? original;
            if (found == value || LooksLikeOurs(mem, name, found))
                original = mem.Originals.TryGetValue(name, out var known) ? known : null;   // not the user's: use what they really had
            else
                mem.Originals[name] = original = found;                                    // the user's value (null when it wasn't there)
            st.OwnOriginals[name] = original;
            if (!mem.Written.TryGetValue(name, out var w)) mem.Written[name] = w = new List<string>();
            if (!w.Contains(value)) w.Add(value);
            if (e == null)
            {
                e = new XElement(type, new XAttribute("name", name));
                props.Add(e);
            }
            e.Value = value;
            st.OwnSet[name] = value;
        }
        SaveSettingsXml(doc);
        SaveMemory(mem);
    }

    private static void RestoreOwnSettings(PerfRestoreState st)
    {
        if (st.OwnSet.Count == 0) return;
        try
        {
            var path = GraphicsFile;
            if (!File.Exists(path)) return;
            var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            var props = SettingsProps(doc);
            if (props == null) return;

            bool changed = false;
            foreach (var kv in st.OwnSet)
            {
                var e = Entry(props, kv.Key);
                // Only put it back if it still holds what RoDeck set (the user may have changed it since).
                if (e == null || e.Value != kv.Value) continue;
                var original = st.OwnOriginals.TryGetValue(kv.Key, out var o) ? o : null;
                if (original == null) e.Remove(); else e.Value = original;
                changed = true;
            }
            if (changed) SaveSettingsXml(doc);
        }
        finally { st.OwnSet.Clear(); st.OwnOriginals.Clear(); }
    }

    // ------------------------------------------------------------------ running clients

    /// <summary>Called every couple of seconds with the clients that are running.</summary>
    public void TuneClients(IEnumerable<ClientInfo> clients, AppSettings s)
    {
        var live = clients.Where(c => c.IsAlive).ToList();
        var pids = live.Select(c => c.Pid).ToHashSet();

        foreach (var pid in _jobs.Keys.Where(p => !pids.Contains(p)).ToList())
        {
            Win32.CloseJob(_jobs[pid]);
            _jobs.Remove(pid);
        }
        foreach (var pid in _capNow.Keys.Where(p => !pids.Contains(p)).ToList()) _capNow.Remove(pid);
        foreach (var pid in _stage.Keys.Where(p => !pids.Contains(p)).ToList()) _stage.Remove(pid);
        _capFailed.RemoveWhere(p => !pids.Contains(p));
        foreach (var pid in _slot.Keys.Where(p => !pids.Contains(p)).ToList()) _slot.Remove(pid);

        int foreground = Win32.ForegroundPid();

        foreach (var c in live)
        {
            // Clients RoDeck didn't start, and "Full quality" accounts, are left exactly as they are.
            bool exempt = c.Owner == null || c.Owner.FullQuality;

            if (!exempt) TuneWindow(c, s, live.Count(x => x.Owner != null && !x.Owner.FullQuality));

            int limit = !exempt && s.BackgroundCpuLimit > 0 && c.Pid != foreground ? s.BackgroundCpuLimit : 0;
            SetCpuLimit(c.Pid, limit);
        }
    }

    // With live previews on, a window gets this long to draw something before it is minimized
    // (a minimized window can't be photographed, so the card would stay empty).
    private const int PreviewGraceSeconds = 12;

    /// <summary>
    /// The window was already put somewhere on purpose (a rejoined client going back to its old spot):
    /// skip the grid placement, but still minimize it later if that is on.
    /// </summary>
    public void MarkPlaced(int pid)
    {
        if (!_stage.TryGetValue(pid, out var stage) || stage < 1) _stage[pid] = 1;
    }

    /// <summary>Until when new windows are put side by side: set when several accounts launch at once.</summary>
    public DateTime ArrangeUntil { get; set; }

    private void TuneWindow(ClientInfo c, AppSettings s, int running)
    {
        int pid = c.Pid;
        _stage.TryGetValue(pid, out var stage);
        if (stage >= 2) return;

        // Side by side only matters with more than one window: a single game keeps Roblox's own window size
        bool sideBySide = s.WindowLayout == "Side by side" && (running > 1 || DateTime.Now < ArrangeUntil);
        bool place = s.StartTiny || sideBySide;
        if (!place && !s.MinimizeOnLaunch) { _stage[pid] = 2; return; }

        var h = Win32.MainWindowOf(pid);
        if (h == IntPtr.Zero) return;   // the window isn't up yet, try again next time
        c.WindowSeenAt ??= DateTime.Now;

        if (stage == 0 && place)
        {
            Win32.EnsureTitleBar(h);        // so the small window can still be moved, minimized and closed
            if (sideBySide)
            {
                var (x, y, w, hgt) = SlotRect(pid, s.StartTiny);
                Win32.Place(h, x, y, w, hgt);
            }
            else Win32.Place(h, 40, 40, 420, 280);   // everything in the same spot
            _stage[pid] = 1;            // minimize (if asked) on the next pass, once it has settled
            return;
        }

        if (s.StartTiny) Win32.EnsureTitleBar(h);   // Roblox may have gone borderless again while loading

        if (s.MinimizeOnLaunch)
        {
            // keep it up until the card has a picture of the game (or the grace time ran out)
            if (s.DetailedView && c.IsAlive)
            {
                var seen = (DateTime.Now - c.WindowSeenAt.Value).TotalSeconds;
                bool hasGamePicture = c.Preview != null && seen >= PreviewGraceSeconds;
                if (!hasGamePicture && seen < PreviewGraceSeconds * 2) return;
            }
            Win32.Minimize(h);
        }
        _stage[pid] = 2;
    }

    /// <summary>The next free spot in a left-to-right, top-to-bottom grid on the screen, so windows don't cover each other.</summary>
    private (int X, int Y, int W, int H) SlotRect(int pid, bool tiny)
    {
        if (!_slot.TryGetValue(pid, out var slot))
        {
            var used = _slot.Values.ToHashSet();
            slot = 0;
            while (used.Contains(slot)) slot++;
            _slot[pid] = slot;
        }
        var wa = Win32.WorkArea();
        int cw = tiny ? 420 : 640, ch = tiny ? 280 : 400;
        cw = Math.Min(cw, wa.W); ch = Math.Min(ch, wa.H);
        int cols = Math.Max(1, wa.W / cw), rows = Math.Max(1, wa.H / ch);
        int perScreen = cols * rows;
        int i = slot % perScreen;            // past a full screen, the grid starts over (slightly shifted)
        int shift = (slot / perScreen) * 30;
        int x = wa.X + (i % cols) * cw + shift, y = wa.Y + (i / cols) * ch + shift;
        return (x, y, cw, ch);
    }

    private void SetCpuLimit(int pid, int percent)
    {
        if (_capFailed.Contains(pid)) return;
        _capNow.TryGetValue(pid, out var current);
        if (current == 0) current = 100;
        int wanted = percent <= 0 ? 100 : Math.Min(100, percent);
        if (current == wanted) return;

        if (_jobs.TryGetValue(pid, out var existing))
        {
            if (Win32.SetCpuCap(existing, wanted)) _capNow[pid] = wanted;
            return;
        }

        if (wanted >= 100) return;   // nothing to lift
        var job = Win32.CreateCpuCappedJob(pid, wanted);
        if (job == IntPtr.Zero) { _capFailed.Add(pid); return; }
        _jobs[pid] = job;
        _capNow[pid] = wanted;
    }

    /// <summary>Lifts every CPU limit (used when RoDeck closes, because the limit would otherwise stay on the client).</summary>
    public void ReleaseAll()
    {
        foreach (var job in _jobs.Values)
        {
            Win32.SetCpuCap(job, 100);
            Win32.CloseJob(job);
        }
        _jobs.Clear();
        _capNow.Clear();
    }
}
