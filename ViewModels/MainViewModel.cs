using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using AltDeck.Models;
using AltDeck.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AltDeck.ViewModels;

public partial class MainViewModel : ObservableObject
{
    /// <summary>From &lt;Version&gt; in AltDeck.csproj, so the app, the exe and backups always agree.</summary>
    public static readonly string AppVersion =
        (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
            typeof(MainViewModel).Assembly)?.InformationalVersion ?? "0.0.0").Split('+')[0];

    private readonly Vault _vault = Vault.Shared;
    private readonly Launcher _launcher = new();
    private readonly PerformanceService _perf = new();
    private readonly GpuMonitor _gpu = new();
    private readonly DiscordPresence _discord = new();
    private readonly PreviewService _previews = new();
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly SettingsStore _store = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _vaultTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly Dictionary<long, string> _placeNames = new();
    private readonly Dictionary<string, List<DateTime>> _rejoin = new();
    private readonly RobloxLogWatcher _logWatcher = new();
    private WindowSizeKeeper? _sizeKeeper;                             // lets client windows be smaller than Roblox's 800×600
    private readonly SemaphoreSlim _launchGate = new(1, 1);          // one launch at a time, so each new process is matched to the right account
    private DateTime _lastLaunchAt = DateTime.MinValue;
    private int _rejoinGeneration;                                    // bumped by Stop all: rejoins that were waiting give up
    private Task? _robloxWait;                                        // shared "wait until Roblox can be reached again" while offline
    private readonly Dictionary<string, ((int X, int Y, int W, int H) Rect, DateTime At)> _snapBack = new();   // account id -> where its window goes
    private DateTime _lastAfk = DateTime.Now;
    private bool _applyingPreset;
    private DateTime? _stopAt;                                        // when the stop timer closes every client (null = no timer)
    private static readonly TimeSpan HungLimit = TimeSpan.FromSeconds(30);   // "Not responding" this long = frozen client

    public AppSettings Settings { get; }
    public ObservableCollection<Account> Accounts { get; } = new();
    public ObservableCollection<GroupChip> GroupChips { get; } = new();
    public ClientMonitor ClientMon { get; } = new();
    public ICollectionView AccountsView { get; }
    public ICollectionView CardsView { get; }
    public string[] StatusFilters { get; } = { "All", "In game", "Online", "Offline", "Needs sign-in" };
    public string RobloxVersion { get; } = FindRobloxVersion();
    public string VersionText => AppVersion;

    public event Action<string>? ScrollToSection;

    /// <summary>Something happened that deserves a tray balloon when the window is hidden (stop timer, panic hotkey).</summary>
    public event Action<string>? Notify;

    [ObservableProperty] private string page = "Home";
    [ObservableProperty] private Account? selectedAccount;
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string groupFilter = "";
    [ObservableProperty] private string statusFilter = "All";
    [ObservableProperty] private string selectedTab = "Overview";
    [ObservableProperty] private bool isOverviewSelected = true;
    [ObservableProperty] private bool isAllActive = true;
    [ObservableProperty] private string targetText = "";
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private string lockStatus = "";
    [ObservableProperty] private string lockBadge = "";
    [ObservableProperty] private string homeTitle = "Nothing playing yet";
    [ObservableProperty] private string homeSubtitle = "Launch some accounts to see them here.";
    [ObservableProperty] private string autoRejoinLinkText = "Turn on";
    [ObservableProperty] private string homePlayText = "Play with accounts";
    [ObservableProperty] private string roomText = "Plenty of room";
    [ObservableProperty] private string cpuText = "0%";
    [ObservableProperty] private string memText = "0.0 GB of 0.0 GB";
    [ObservableProperty] private string gpuText = "…";
    [ObservableProperty] private string gpuSubText = "Measuring…";
    [ObservableProperty] private bool hasMasterPassword;
    [ObservableProperty] private bool stopTimerActive;
    [ObservableProperty] private string stopTimerText = "";

    private const string StopTimerBaseText =
        "Closes every Roblox client after the time you pick. AltDeck and your PC stay on. The timer is forgotten when AltDeck closes.";
    public string StopTimerDescription => string.IsNullOrEmpty(StopTimerText) ? StopTimerBaseText : StopTimerBaseText + "\n" + StopTimerText;
    partial void OnStopTimerTextChanged(string value) => OnPropertyChanged(nameof(StopTimerDescription));

    private const string PanicBaseText =
        "Click the box and press the keys (for example Ctrl+Shift+F12). Works from any app: stops every Roblox client at once, like Hold to stop all. Backspace turns it off.";
    [ObservableProperty] private string panicHotkeyStatus = "";
    public string PanicHotkeyDescription => string.IsNullOrEmpty(PanicHotkeyStatus) ? PanicBaseText : PanicBaseText + "\n" + PanicHotkeyStatus;
    partial void OnPanicHotkeyStatusChanged(string value) => OnPropertyChanged(nameof(PanicHotkeyDescription));

    private const string DiscordBaseText =
        "Your Discord profile shows “Playing AltDeck” and how many alts run. Needs the Discord desktop app, and an Application ID named AltDeck from discord.com/developers/applications.";
    [ObservableProperty] private string discordStatus = "";
    public string DiscordDescription => string.IsNullOrEmpty(DiscordStatus) ? DiscordBaseText : DiscordBaseText + "\n" + DiscordStatus;
    partial void OnDiscordStatusChanged(string value) => OnPropertyChanged(nameof(DiscordDescription));
    [ObservableProperty] private string pickedText = "0 of 0 picked";
    [ObservableProperty] private string launchButtonText = "Pick an account first";
    [ObservableProperty] private string accountsSubtitle = "0 total";
    [ObservableProperty] private string needsSignInText = "";
    [ObservableProperty] private int needsSignInCount;
    [ObservableProperty] private bool hasExpiredSignIns;
    [ObservableProperty] private string expiredSignInText = "";
    [ObservableProperty] private double cpuPercent;
    [ObservableProperty] private double memPercent;
    [ObservableProperty] private double gpuPercent;
    [ObservableProperty] private int runningCount;
    [ObservableProperty] private int accountCount;
    [ObservableProperty] private double rowHeight = 60;
    [ObservableProperty] private bool canLaunch;

    public string BringBackNote => Settings.AutoRejoin || Settings.AutoRestart
        ? "If this client drops, AltDeck relaunches it into the same game."
        : "Auto-rejoin and auto-restart are off in Settings, so nothing brings it back yet.";

    public bool AllVisibleSelected
    {
        get
        {
            var v = AccountsView.Cast<Account>().ToList();
            return v.Count > 0 && v.All(a => a.IsSelected);
        }
        set
        {
            foreach (var a in AccountsView.Cast<Account>().ToList()) a.IsSelected = value;
            OnPropertyChanged();
        }
    }

    public MainViewModel()
    {
        Settings = _store.Load();
        _perf.RecoverFromCrash();   // put Roblox's own settings back if AltDeck was closed while they were changed
        SignInIsolation.Restore();  // same for the user's own Roblox sign-in
        ThemeManager.Apply(Settings.Theme, Settings.Accent);

        HasMasterPassword = _vault.IsPasswordProtected;   // App.OnStartup already unlocked it if it is
        foreach (var a in _vault.Load()) { Accounts.Add(a); HookAccount(a); }

        AccountsView = new CollectionViewSource { Source = Accounts }.View;
        AccountsView.Filter = FilterAccount;
        CardsView = new CollectionViewSource { Source = ClientMon.Clients }.View;
        CardsView.Filter = o => o is ClientInfo c && (SelectedTab == "Overview" || c.Pid.ToString() == SelectedTab);

        Settings.PropertyChanged += OnSettingsChanged;
        ClientMon.ClientDropped += OnClientDropped;
        _logWatcher.Disconnected += OnClientDisconnected;
        try { _sizeKeeper = new WindowSizeKeeper(); } catch { }
        SetupFeatures();

        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _store.Save(Settings); };
        _vaultTimer.Tick += (_, _) =>
        {
            _vaultTimer.Stop();
            try { _vault.Save(Accounts); }
            catch (Exception ex)
            {
                StatusMessage = "Couldn't save vault: " + ex.Message;
                Log(ActivityLog.Error, "Couldn't save the vault: " + ex.Message);
            }
        };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); StatusMessage = ""; };
        _timer.Tick += (_, _) => Tick();
        _previewTimer.Tick += (_, _) => RefreshPreviews();

        ApplyDensity();
        RebuildGroups();
        UpdatePicked();
        RefreshLock();
        UpdateHomeTexts();
        _timer.Start();
        _previewTimer.Start();
    }

    public async Task InitializeAsync()
    {
        if (Accounts.Count > 0) await CheckAllAsync();
    }

    /// <summary>Called when the window closes: Roblox's own settings and any CPU limits go back to normal.</summary>
    public void Shutdown()
    {
        try { _store.Save(Settings); } catch { }
        try { _perf.RestoreNow(); } catch { }
        try { SignInIsolation.Restore(); } catch { }
        try { _sizeKeeper?.Dispose(); } catch { }
        try { _perf.ReleaseAll(); } catch { }
        try { _discord.Close(); } catch { }
        Log(ActivityLog.Info, "AltDeck closed.");
    }

    // ------------------------------------------------------------------ plumbing

    partial void OnStatusMessageChanged(string value)
    {
        _toastTimer.Stop();
        if (!string.IsNullOrEmpty(value)) _toastTimer.Start();
    }

    partial void OnSearchTextChanged(string value) => AccountsView?.Refresh();
    partial void OnStatusFilterChanged(string value) => AccountsView?.Refresh();

    partial void OnGroupFilterChanged(string value)
    {
        IsAllActive = string.IsNullOrEmpty(value);
        foreach (var g in GroupChips) g.IsActive = string.Equals(g.Name, value, StringComparison.OrdinalIgnoreCase);
        AccountsView?.Refresh();
    }

    partial void OnSelectedTabChanged(string value)
    {
        IsOverviewSelected = value == "Overview";
        foreach (var c in ClientMon.Clients) c.IsTabSelected = c.Pid.ToString() == value;
        CardsView?.Refresh();
    }

    private bool FilterAccount(object o)
    {
        if (o is not Account a) return false;
        var q = (SearchText ?? "").Trim().TrimStart('@');
        if (q.Length > 0 &&
            !(a.Label.Contains(q, StringComparison.OrdinalIgnoreCase) ||
              a.Username.Contains(q, StringComparison.OrdinalIgnoreCase) ||
              a.UserId.ToString().Contains(q)))
            return false;
        if (!string.IsNullOrEmpty(GroupFilter) && !a.GroupList.Contains(GroupFilter, StringComparer.OrdinalIgnoreCase))
            return false;
        return StatusFilter switch
        {
            "In game" => a.StatusKind == "ingame",
            "Online" => a.StatusKind == "online",
            "Offline" => a.StatusKind == "offline",
            "Needs sign-in" => a.StatusKind == "signedout" || a.StatusKind == "check",
            _ => true
        };
    }

    private void HookAccount(Account a)
    {
        a.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(Account.IsSelected):
                case nameof(Account.IsPlaying):
                    UpdatePicked();
                    break;
                case nameof(Account.Groups):
                    RebuildGroups();
                    AccountsView.Refresh();
                    QueueVaultSave();
                    break;
                case nameof(Account.IsFavourite):
                case nameof(Account.FullQuality):
                case nameof(Account.BringBack):
                case nameof(Account.Notes):
                case nameof(Account.Alias):
                case nameof(Account.LaunchArgs):
                    QueueVaultSave();
                    break;
            }
        };
    }

    private void QueueVaultSave() { _vaultTimer.Stop(); _vaultTimer.Start(); }

    private void RebuildGroups()
    {
        var names = Accounts.SelectMany(a => a.GroupList).Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        GroupChips.Clear();
        foreach (var n in names)
            GroupChips.Add(new GroupChip
            {
                Name = n,
                Count = Accounts.Count(a => a.GroupList.Contains(n, StringComparer.OrdinalIgnoreCase)),
                IsActive = string.Equals(n, GroupFilter, StringComparison.OrdinalIgnoreCase)
            });
    }

    private void UpdatePicked()
    {
        var selectable = Accounts.Count(a => !a.IsPlaying);
        var picked = Accounts.Count(a => a.IsSelected && !a.IsPlaying);
        PickedText = $"{picked} of {selectable} picked";
        LaunchButtonText = picked == 0 ? "Pick an account first" : $"Launch {picked} account{(picked == 1 ? "" : "s")}";
        HomePlayText = picked == 0 ? "Play with accounts" : $"Play with {picked} account{(picked == 1 ? "" : "s")}";
        CanLaunch = picked > 0;
        OnPropertyChanged(nameof(AllVisibleSelected));
    }

    private void ApplyDensity() => RowHeight = Settings.Density == "Compact" ? 46 : 62;

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.Theme):
            case nameof(AppSettings.Accent):
                ThemeManager.Apply(Settings.Theme, Settings.Accent);
                break;
            case nameof(AppSettings.Density):
                ApplyDensity();
                break;
            case nameof(AppSettings.MultiInstance):
                RefreshLock();
                break;
            case nameof(AppSettings.PerfPreset):
                if (!_applyingPreset) ApplyPreset(Settings.PerfPreset);
                break;
            case nameof(AppSettings.ShowOnDiscord):
            case nameof(AppSettings.DiscordAppId):
                _discord.Reset();
                break;
            case nameof(AppSettings.AutoRejoin):
                OnPropertyChanged(nameof(BringBackNote));
                UpdateHomeTexts();
                break;
            case nameof(AppSettings.AutoRestart):
                OnPropertyChanged(nameof(BringBackNote));
                break;
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Saves the current fine-tune values as the user's own preset and selects it.</summary>
    [RelayCommand]
    private void SaveCustomPerf()
    {
        var s = Settings;
        s.CustomPerf = new PerfSnapshot
        {
            FpsCap = s.FpsCap, LowestGraphics = s.LowestGraphics, LighterRendering = s.LighterRendering,
            Msaa = s.Msaa, TextureQuality = s.TextureQuality, RenderingMode = s.RenderingMode,
            GraySky = s.GraySky, NoGrass = s.NoGrass, StartTiny = s.StartTiny, MinimizeOnLaunch = s.MinimizeOnLaunch,
            MuteOnStart = s.MuteOnStart, BackgroundCpuLimit = s.BackgroundCpuLimit, AntiAfk = s.AntiAfk,
        };
        _applyingPreset = true;
        s.PerfPreset = "Custom";
        _applyingPreset = false;
    }

    private void ApplyPreset(string preset)
    {
        _applyingPreset = true;
        switch (preset)
        {
            case "Max alts":
                Settings.FpsCap = 10; Settings.LowestGraphics = true; Settings.LighterRendering = true;
                Settings.Msaa = "x1"; Settings.TextureQuality = "Lowest"; Settings.RenderingMode = "Roblox default";
                Settings.GraySky = true; Settings.NoGrass = true;
                Settings.StartTiny = true; Settings.MinimizeOnLaunch = true; Settings.BackgroundCpuLimit = 25;
                Settings.AntiAfk = true;
                break;
            case "Balanced":
                Settings.FpsCap = 30; Settings.LowestGraphics = true; Settings.LighterRendering = false;
                Settings.Msaa = "x1"; Settings.TextureQuality = "Low"; Settings.RenderingMode = "Roblox default";
                Settings.GraySky = false; Settings.NoGrass = false;
                Settings.StartTiny = false; Settings.MinimizeOnLaunch = false; Settings.BackgroundCpuLimit = 50;
                break;
            case "Custom":
                if (Settings.CustomPerf is { } c)
                {
                    Settings.FpsCap = c.FpsCap; Settings.LowestGraphics = c.LowestGraphics; Settings.LighterRendering = c.LighterRendering;
                    Settings.Msaa = c.Msaa; Settings.TextureQuality = c.TextureQuality; Settings.RenderingMode = c.RenderingMode;
                    Settings.GraySky = c.GraySky; Settings.NoGrass = c.NoGrass;
                    Settings.StartTiny = c.StartTiny; Settings.MinimizeOnLaunch = c.MinimizeOnLaunch;
                    Settings.MuteOnStart = c.MuteOnStart; Settings.BackgroundCpuLimit = c.BackgroundCpuLimit; Settings.AntiAfk = c.AntiAfk;
                }
                break;
            default:
                Settings.FpsCap = AppSettings.FpsMax; Settings.LowestGraphics = false; Settings.LighterRendering = false;
                Settings.Msaa = "x4"; Settings.TextureQuality = "High"; Settings.RenderingMode = "Roblox default";
                Settings.GraySky = false; Settings.NoGrass = false;
                Settings.StartTiny = false; Settings.MinimizeOnLaunch = false; Settings.BackgroundCpuLimit = 0;
                break;
        }
        _applyingPreset = false;
    }

    private static string FindRobloxVersion()
    {
        try
        {
            var roots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "Versions"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Roblox", "Versions")
            };
            foreach (var root in roots.Where(Directory.Exists))
            {
                var dir = Directory.GetDirectories(root)
                    .Where(d => File.Exists(Path.Combine(d, "RobloxPlayerBeta.exe")))
                    .OrderByDescending(Directory.GetLastWriteTime).FirstOrDefault();
                if (dir != null) return Path.GetFileName(dir);
            }
        }
        catch { }
        return "not found";
    }

    private void RefreshLock()
    {
        if (!Settings.MultiInstance)
        {
            _launcher.Disable();
            LockBadge = "Multi-instance: OFF";
            LockStatus = "Multi-instance is turned off in Settings.";
            return;
        }
        bool ok = _launcher.EnableMultiInstance();
        LockBadge = ok ? "Multi-instance: ON" : "Multi-instance: OFF";
        LockStatus = ok
            ? "Multi-instance is ACTIVE. AltDeck is holding Roblox's lock, so each account can run its own client."
            : "Multi-instance is OFF: a Roblox process (possibly a hidden background one that starts with Windows) already holds the lock, so launching another account would close the first. Use the button below to fix it.";
    }

    // ------------------------------------------------------------------ periodic tick

    /// <summary>Takes a fresh picture of the client windows whose cards are showing (only on Home, only in Detailed view).</summary>
    private void RefreshPreviews()
    {
        try
        {
            if (Page != "Home" || !Settings.DetailedView) return;
            if (Application.Current?.MainWindow is { IsVisible: false }) return;   // hidden in the tray: nobody is looking
            if (Application.Current?.MainWindow?.WindowState == WindowState.Minimized) return;   // nobody is looking
            var shown = ClientMon.Clients
                .Where(c => c.IsAlive && (SelectedTab == "Overview" || c.Pid.ToString() == SelectedTab))
                .ToList();
            _previews.Refresh(shown);
        }
        catch { }
    }

    private void Tick()
    {
        ClientMon.Tick();
        try { _logWatcher.Poll(ClientMon.Clients); } catch { }
        try { SnapBackWindows(); } catch { }
        try { _perf.TuneClients(ClientMon.Clients, Settings); } catch { }
        if (Settings.AutoRestart) RestartFrozenClients();
        CheckStopTimer();
        var live = ClientMon.Clients.Where(c => c.IsAlive).ToList();
        MaybeSweep();

        // GPU: hand out what the last background read found, then start the next read
        if (live.Count > 0)
        {
            foreach (var c in live)
            {
                if (_gpu.TryGet(c.Pid, out var g)) { c.Gpu = g.Percent; c.GpuMemoryMb = g.MemoryMb; }
            }
            _gpu.Poll();
        }

        foreach (var c in ClientMon.Clients)
            if (c.Target != null && _placeNames.TryGetValue(c.Target.PlaceId, out var n)) c.PlaceName = n;

        if (SelectedTab != "Overview" && !ClientMon.Clients.Any(c => c.Pid.ToString() == SelectedTab))
            SelectedTab = "Overview";

        foreach (var a in Accounts)
        {
            bool playing = live.Any(c => c.Owner == a);
            if (a.IsPlaying != playing)
            {
                a.IsPlaying = playing;
                if (playing) { a.StatusKind = "ingame"; a.StatusText = "In game"; }
            }
            if (a.LastChecked is DateTime t)
            {
                var m = (int)(DateTime.Now - t).TotalMinutes;
                a.CheckedText = m < 1 ? "checked just now" : $"checked {m}m ago";
            }
        }

        RunningCount = live.Count;
        _discord.Update(Settings.ShowOnDiscord, Settings.DiscordAppId, live.Count);
        DiscordStatus = _discord.Status;
        AccountCount = Accounts.Count;
        CpuPercent = Math.Min(100, ClientMon.TotalCpu);
        CpuText = $"{CpuPercent:0}%";
        double totalMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1048576.0;
        if (totalMb <= 0) totalMb = 16384;
        double usedMb = ClientMon.TotalMemoryMb;
        MemPercent = Math.Min(100, usedMb / totalMb * 100);
        MemText = $"{usedMb / 1024:0.0} GB of {totalMb / 1024:0.0} GB";

        if (!_gpu.Available)
        {
            GpuPercent = 0;
            GpuText = "N/A";
            GpuSubText = "Not supported on this PC";
        }
        else if (live.Count == 0)
        {
            GpuPercent = 0;
            GpuText = "0%";
            GpuSubText = "All clients";
        }
        else
        {
            GpuPercent = Math.Min(100, ClientMon.TotalGpu);
            GpuText = $"{GpuPercent:0}%";
            var vram = ClientMon.TotalGpuMemoryMb;
            GpuSubText = vram > 0 ? $"{vram / 1024:0.0} GB video memory" : "All clients";
        }
        RoomText = MemPercent < 50 && CpuPercent < 80 ? "Plenty of room" : MemPercent < 75 ? "Getting busy" : "Low on memory";

        if (Settings.AntiAfk && live.Count > 0 && DateTime.Now - _lastAfk > TimeSpan.FromMinutes(5))
        {
            _lastAfk = DateTime.Now;
            ClientMon.AntiAfkPulse();
        }

        UpdateHomeTexts();
    }

    private void UpdateHomeTexts()
    {
        var live = ClientMon.Clients.Where(c => c.IsAlive).ToList();
        HomeTitle = live.Count == 0 ? "Nothing playing yet"
                  : $"{live.Count} account{(live.Count == 1 ? "" : "s")} playing";
        var first = live.FirstOrDefault(c => c.Target != null);
        HomeSubtitle = first == null
            ? "Launch some accounts to see them here."
            : $"In {(string.IsNullOrEmpty(first.PlaceName) ? "place " + first.Target!.PlaceId : first.PlaceName)}. Auto-rejoin is {(Settings.AutoRejoin ? "on" : "off")}.";
        AutoRejoinLinkText = Settings.AutoRejoin ? "Turn off" : "Turn on";

        AccountsSubtitle = $"{Accounts.Count} total";
        var k = Accounts.Count(a => a.StatusKind == "signedout" || a.StatusKind == "check");
        NeedsSignInText = k > 0 ? $"{k} need{(k == 1 ? "s" : "")} sign-in" : "";
        var expired = Accounts.Count(a => a.StatusKind == "signedout");   // only real expiries, not "couldn't reach Roblox"
        NeedsSignInCount = expired;
        HasExpiredSignIns = expired > 0;
        ExpiredSignInText = expired == 0 ? "" : $"{expired} sign-in{(expired == 1 ? " has" : "s have")} expired. Click to see.";
    }

    // ------------------------------------------------------------------ accounts

    private static void SetStatus(Account a, string kind, string text)
    {
        a.StatusKind = kind;
        a.StatusText = text;
        a.LastChecked = DateTime.Now;
        a.CheckedText = "checked just now";
    }

    private async Task CheckAccountAsync(Account a)
    {
        try
        {
            var u = await RobloxApi.GetAuthenticatedUserAsync(a.Cookie);
            if (u == null) { SetStatus(a, "signedout", "Signed out"); return; }
            a.Username = u.Name;
            a.DisplayName = u.DisplayName;
            if (string.IsNullOrEmpty(a.AvatarUrl)) a.AvatarUrl = await RobloxApi.GetAvatarUrlAsync(u.Id);

            var p = await RobloxApi.GetPresenceAsync(a.Cookie, u.Id);
            if (a.IsPlaying) SetStatus(a, "ingame", "In game");
            else if (p == null) SetStatus(a, "online", "Ready");
            else switch (p.Type)
            {
                case 2: SetStatus(a, "ingame", string.IsNullOrEmpty(p.Location) ? "In game" : "In game · " + p.Location); break;
                case 3: SetStatus(a, "studio", "In Studio"); break;
                case 1: SetStatus(a, "online", "Online"); break;
                default: SetStatus(a, "offline", "Offline"); break;
            }
        }
        catch { SetStatus(a, "check", "Needs a check"); }
    }

    /// <summary>Returns an error message, or null on success.</summary>
    public async Task<string?> AddAccountFromCookieAsync(string rawCookie)
    {
        try
        {
            var cookie = RobloxApi.CleanCookie(rawCookie);
            var user = await RobloxApi.GetAuthenticatedUserAsync(cookie);
            if (user == null) return "That cookie isn't valid, or it has expired.";

            var acc = Accounts.FirstOrDefault(a => a.UserId == user.Id);
            if (acc == null)
            {
                acc = new Account { UserId = user.Id };
                Accounts.Add(acc);
                HookAccount(acc);
            }
            acc.Cookie = cookie;
            acc.Username = user.Name;
            acc.DisplayName = user.DisplayName;
            acc.AvatarUrl = await RobloxApi.GetAvatarUrlAsync(user.Id);
            await CheckAccountAsync(acc);
            _vault.Save(Accounts);
            RebuildGroups();
            UpdatePicked();
            SelectedAccount = acc;
            StatusMessage = $"Added {acc.Label}.";
            Log(ActivityLog.Info, $"Added or signed in {acc.Label} (@{acc.Username}).");
            return null;
        }
        catch (Exception ex)
        {
            return "Couldn't add the account: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task CheckAllAsync()
    {
        var before = SignedOutIds();
        foreach (var a in Accounts.ToList()) await CheckAccountAsync(a);
        _lastSweep = DateTime.Now;
        try { if (Accounts.Count > 0) _vault.Save(Accounts); } catch { }
        AccountsView.Refresh();
        UpdateHomeTexts();
        if (Accounts.Count > 0) StatusMessage = "Checked all sign-ins.";
        ReportNewSignOuts(before);   // after the toast above, so a problem isn't hidden by it
    }

    [RelayCommand]
    private async Task CheckOneAsync(Account? a)
    {
        if (a == null) return;
        await CheckAccountAsync(a);
        AccountsView.Refresh();
    }

    [RelayCommand]
    private void RemoveAccount(Account? a)
    {
        if (a == null) return;
        var ok = MessageBox.Show($"Remove {a.Label} and delete its saved sign-in from this PC?", "AltDeck",
                                 MessageBoxButton.YesNo) == MessageBoxResult.Yes;
        if (!ok) return;
        Accounts.Remove(a);
        if (SelectedAccount == a) SelectedAccount = null;
        _vault.Save(Accounts);
        RebuildGroups();
        UpdatePicked();
    }

    [RelayCommand]
    private void ToggleFavourite(Account? a)
    {
        if (a != null) a.IsFavourite = !a.IsFavourite;
    }

    [RelayCommand]
    private void SetGroup(string? name)
    {
        name ??= "";
        GroupFilter = string.Equals(name, GroupFilter, StringComparison.OrdinalIgnoreCase) ? "" : name;
    }

    [RelayCommand]
    private void Copy(object? value)
    {
        // the ID button passes a number, the name button a string
        var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); StatusMessage = "Copied."; } catch { }
    }

    [RelayCommand]
    private async Task PlayOneAsync(Account? a)
    {
        if (a == null) return;
        if (TargetParser.Parse(TargetText) == null)
        {
            foreach (var x in Accounts) x.IsSelected = x == a;
            Page = "Play";
            StatusMessage = "Paste where to play, then launch.";
            return;
        }
        await LaunchAccountsAsync(new List<Account> { a }, TargetText);
    }

    // ------------------------------------------------------------------ navigation / small commands

    [RelayCommand] private void GoPlay() => Page = "Play";
    [RelayCommand] private void GoPerformance() => Page = "Performance";
    [RelayCommand] private void Soon(string? what) => StatusMessage = (what ?? "That") + " is coming in a later version.";

    [RelayCommand]
    private void GoSection(string? section)
    {
        Page = "Settings";
        ScrollToSection?.Invoke(section ?? "General");
    }

    [RelayCommand]
    private async Task HomePlayAsync()
    {
        if (CanLaunch) await LaunchAsync();
        else Page = "Play";
    }

    [RelayCommand]
    private void SelectTab(string? key) => SelectedTab = string.IsNullOrEmpty(key) ? "Overview" : key;

    [RelayCommand]
    private void ToggleAutoRejoin() => Settings.AutoRejoin = !Settings.AutoRejoin;

    [RelayCommand]
    private void ToggleTheme() => Settings.Theme = Settings.Theme == "Light" ? "Dark" : "Light";

    [RelayCommand]
    private void SetAccent(string? hex)
    {
        if (!string.IsNullOrEmpty(hex)) Settings.Accent = hex;
    }

    [RelayCommand]
    private void PromptAccent()
    {
        var hex = PromptDialog.Ask(Application.Current.MainWindow, "Accent colour", "Enter a colour like #FF8A1F:", Settings.Accent);
        if (hex == null) return;
        try { ColorConverter.ConvertFromString(hex); Settings.Accent = hex; }
        catch { StatusMessage = "That isn't a valid colour."; }
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DataDir);
            Process.Start(new ProcessStartInfo("explorer.exe", SettingsStore.DataDir) { UseShellExecute = true });
        }
        catch { }
    }

    [RelayCommand]
    private void UseFavourite(Favourite? f)
    {
        if (f != null) TargetText = f.Target;
    }

    [RelayCommand]
    private void SaveFavourite()
    {
        if (TargetParser.Parse(TargetText) == null) { StatusMessage = "Paste a game link or place ID first."; return; }
        var name = PromptDialog.Ask(Application.Current.MainWindow, "Save favourite", "Name for this game:");
        if (name == null) return;
        Settings.Favourites.Add(new Favourite { Name = name, Target = TargetText.Trim() });
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void RemoveFavourite(Favourite f)
    {
        Settings.Favourites.Remove(f);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    // ------------------------------------------------------------------ clients on Home

    [RelayCommand] private void ShowClient(ClientInfo? c) { if (c != null) ClientMon.Show(c); }
    [RelayCommand] private void MinimizeClient(ClientInfo? c) { if (c != null) ClientMon.Minimize(c); }
    [RelayCommand] private void MinimizeAll() => ClientMon.MinimizeAll();
    /// <summary>Concurrent on purpose: the button is free again at once; the retry checks run in the background.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ShowAllAsync()
    {
        try
        {
            int left = await ClientMon.ShowAllAsync();
            if (left > 0) StatusMessage = $"{left} window{(left == 1 ? "" : "s")} didn't come back. Try Show all again.";
        }
        catch { }
    }
    [RelayCommand] private void SaveLayout() => SaveCurrentLayout();

    /// <summary>One monitor, as offered in the Tile menu. Area is the part without the taskbar, in screen pixels.</summary>
    public record ScreenChoice(string Label, System.Drawing.Rectangle Area);

    /// <summary>Every monitor: the main one first, then the others from left to right.</summary>
    public List<ScreenChoice> GetScreens()
    {
        var all = System.Windows.Forms.Screen.AllScreens;
        var main = all.FirstOrDefault(s => s.Primary) ?? all[0];
        var list = new List<ScreenChoice>();
        int n = 2;
        foreach (var s in all.OrderByDescending(s => s.Primary).ThenBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y))
        {
            string where = s.Primary ? "" :
                s.Bounds.Right <= main.Bounds.Left ? ", left" :
                s.Bounds.Left >= main.Bounds.Right ? ", right" :
                s.Bounds.Bottom <= main.Bounds.Top ? ", above" :
                s.Bounds.Top >= main.Bounds.Bottom ? ", below" : "";
            var name = s.Primary ? "main screen" : $"screen {n++}";
            list.Add(new ScreenChoice($"Tile on {name} ({s.Bounds.Width}×{s.Bounds.Height}{where})", s.WorkingArea));
        }
        return list;
    }

    /// <summary>Moves every client window onto this screen and arranges them in a grid there.</summary>
    public void TileOn(ScreenChoice screen)
    {
        var a = screen.Area;
        ClientMon.TileWindows(a.X, a.Y, a.Width, a.Height);
        int n = ClientMon.Clients.Count(c => c.IsAlive);
        StatusMessage = n == 0 ? "Nothing is running." : $"Tiled {n} window{(n == 1 ? "" : "s")} on the {screen.Label.Substring("Tile on ".Length)}.";
    }

    [RelayCommand]
    private void TileWindows() => TileOn(GetScreens()[0]);

    [RelayCommand]
    private void CloseClient(ClientInfo? c)
    {
        if (c == null) return;
        if (c.IsClosed) { ClientMon.Clients.Remove(c); return; }
        // Ended directly: asking the window to close makes Roblox show "Are you sure?" and needs a second click.
        ClientMon.KillNow(c);
        Log(ActivityLog.Stop, $"Closed {c.Name}'s client.");
    }

    [RelayCommand]
    private void DismissClient(ClientInfo? c)
    {
        if (c != null && c.IsClosed) ClientMon.Clients.Remove(c);
    }

    [RelayCommand]
    private void DismissDisconnected()
    {
        foreach (var c in ClientMon.Clients.Where(c => c.IsClosed).ToList()) ClientMon.Clients.Remove(c);
    }

    [RelayCommand]
    private void StopAll()
    {
        var live = ClientMon.Clients.Where(c => c.IsAlive).ToList();
        foreach (var c in live) ClientMon.KillNow(c);
        StatusMessage = live.Count == 0 ? "Nothing is running." : $"Stopped {live.Count} client{(live.Count == 1 ? "" : "s")}.";
        if (live.Count > 0) Log(ActivityLog.Stop, $"Stopped all {live.Count} client{(live.Count == 1 ? "" : "s")}.");
        if (QueueRunning) CancelQueue();   // "stop all" also means "don't start more"
        _rejoinGeneration++;               // ...and no rejoins either, including ones waiting for the internet
        _snapBack.Clear();
    }

    [RelayCommand]
    private async Task PlayAgainAsync(ClientInfo? c)
    {
        if (c == null) return;
        if (await RelaunchAsync(c)) Log(ActivityLog.Launch, $"Played {c.Name} again in {(c.Target != null ? DescribeTarget(c.Target) : "the same game")}.");
    }

    [RelayCommand]
    private async Task FreeLockAsync()
    {
        var answer = MessageBox.Show(
            "This closes every running Roblox client so AltDeck can take the lock. Continue?",
            "AltDeck", MessageBoxButton.YesNo);
        if (answer != MessageBoxResult.Yes) return;

        var n = await Task.Run(() => Launcher.CloseAllRobloxClients());
        RefreshLock();
        StatusMessage = $"Closed {n} Roblox process(es). " + (_launcher.IsActive
            ? "Multi-instance is active."
            : "The lock is still held. In Task Manager, end any RobloxPlayerBeta.exe, then press the button again.");
    }

    // ------------------------------------------------------------------ launching

    [RelayCommand]
    private async Task LaunchAsync()
    {
        var picked = Accounts.Where(a => a.IsSelected && !a.IsPlaying).ToList();
        if (picked.Count == 0) { StatusMessage = "Tick at least one account."; return; }
        await LaunchAccountsAsync(picked, TargetText);   // the launch queue itself is in MainViewModel.Features.cs
    }

    private async Task LoadPlaceNameAsync(Account a, long placeId)
    {
        if (_placeNames.ContainsKey(placeId)) return;
        var name = await RobloxApi.GetPlaceNameAsync(a.Cookie, placeId);
        if (!string.IsNullOrEmpty(name)) _placeNames[placeId] = name;
    }

    private enum RelaunchResult { Ok, Failed, Offline }

    private async Task<bool> RelaunchAsync(ClientInfo c) => await RelaunchAsync(c, c.Target, -1) == RelaunchResult.Ok;

    /// <summary>
    /// Starts the account again. <paramref name="joinTarget"/> is where to go this time (e.g. the same server);
    /// the new client keeps <c>c.Target</c> as its target, so later rejoins don't stick to one server.
    /// Launches wait for each other and are spaced at least a few seconds apart, so each new Roblox process
    /// is matched to the right account. Returns Offline when Roblox couldn't be reached (that try doesn't count).
    /// </summary>
    private async Task<RelaunchResult> RelaunchAsync(ClientInfo c, LaunchTarget? joinTarget, int generation)
    {
        if (c.Owner == null || c.Target == null || joinTarget == null) return RelaunchResult.Failed;

        await _launchGate.WaitAsync();
        try
        {
            var gap = TimeSpan.FromSeconds(Math.Max(5, Settings.LaunchDelay));
            var since = DateTime.UtcNow - _lastLaunchAt;
            if (since < gap)
            {
                StatusMessage = $"Rejoining {c.Name} in {Math.Ceiling((gap - since).TotalSeconds)}s (spacing launches)…";
                await Task.Delay(gap - since);
            }
            if (!ClientMon.Clients.Contains(c) || !c.IsClosed) return RelaunchResult.Failed;
            if (generation >= 0 && generation != _rejoinGeneration) return RelaunchResult.Failed;   // Stop all was pressed meanwhile

            RefreshLock();
            if (Settings.MultiInstance && !_launcher.IsActive)
            {
                StatusMessage = "Multi-instance is off. Fix it on the Play page first.";
                return RelaunchResult.Failed;
            }
            try
            {
                await _perf.PrepareLaunchAsync(Settings, c.Owner);
                if (Settings.IsolateSignIns) SignInIsolation.PrepareLaunch();
                await _launcher.LaunchAsync(c.Owner, joinTarget, ArgsFor(c.Owner));
                if (Settings.IsolateSignIns) SignInIsolation.LaunchStarted();
                _lastLaunchAt = DateTime.UtcNow;
                _perf.LaunchStarted();
                ClientMon.ExpectLaunch(c.Owner, c.Target);
                ClientMon.Clients.Remove(c);
                if (c.LastRect is { } r) _snapBack[c.Owner.Id] = (r, DateTime.UtcNow);
                return RelaunchResult.Ok;
            }
            catch (Exception ex)
            {
                if (!await RobloxApi.IsReachableAsync())
                {
                    StatusMessage = $"{c.Name}: can't reach Roblox right now.";
                    return RelaunchResult.Offline;
                }
                StatusMessage = $"{c.Name}: {ex.Message}";
                Log(ActivityLog.Error, $"Relaunch of {c.Name} failed: {ex.Message}");
                return RelaunchResult.Failed;
            }
        }
        finally { _launchGate.Release(); }
    }

    /// <summary>
    /// Roblox's log says this client lost its game ("Disconnected … Error Code: 277") while its window is still open.
    /// With auto-rejoin on, the client is closed; the next tick reports it as dropped and OnClientDropped brings it back.
    /// </summary>
    private void OnClientDisconnected(ClientInfo c, int code)
    {
        if (c.Owner == null || c.StoppedByUser || c.IsClosed) return;

        var what = code switch
        {
            0 => "never got into a server (3 minutes after starting)",
            RobloxLogWatcher.NoResponseCode => "couldn't connect: the server didn't answer (Error Code: 279)",
            275 => "was disconnected because Roblox shut the server down (Error Code: 275)",
            RobloxLogWatcher.SignedOutCode => "lost its sign-in: Roblox answers \"Unauthorized\", so the game's teleports fail (Error Code: 769) and its anti-AFK rejoin can't work",
            273 => "was disconnected because the account joined from somewhere else (Error Code: 273)",
            _ => $"was disconnected by Roblox (Error Code: {code})"
        };

        // 273 means the account is being played elsewhere: rejoining would kick that session, so leave it.
        bool bringBack = Settings.AutoRejoin && c.Owner.BringBack && c.Target != null && code != 273;
        if (!bringBack)
        {
            var why = code == 273 ? "Not rejoining so the other session isn't kicked."
                    : !Settings.AutoRejoin ? "Auto-rejoin is off, so it was left as it is."
                    : !c.Owner.BringBack ? "\"Bring it back if it drops\" is off for this account."
                    : "There's no game to rejoin.";
            Log(ActivityLog.Drop, $"{c.Name} {what}. {why}");
            StatusMessage = $"{c.Name} disconnected (Error Code: {code}).";
            Notify?.Invoke(StatusMessage);
            return;
        }

        Log(ActivityLog.Drop, $"{c.Name} {what}. Closing it to rejoin.");
        c.DisconnectCode = code;
        ClientMon.KillDisconnected(c);
    }

    /// <summary>Whether a dropped client should still be brought back (nothing was turned off or pressed meanwhile).</summary>
    private bool StillWanted(ClientInfo c, int generation) =>
        generation == _rejoinGeneration && ClientMon.Clients.Contains(c) && c.IsClosed
        && (Settings.AutoRejoin || Settings.AutoRestart) && c.Owner != null && c.Owner.BringBack;

    /// <summary>
    /// Waits until Roblox can be reached again (internet back, Roblox up). One shared wait for every client,
    /// so five clients dropping at once check once, not five times. Waiting never uses up a try.
    /// </summary>
    private async Task WaitForRobloxAsync()
    {
        if (_robloxWait == null)
        {
            if (await RobloxApi.IsReachableAsync()) return;
            _robloxWait ??= WaitUntilReachableAsync();
        }
        await _robloxWait;
    }

    private async Task WaitUntilReachableAsync()
    {
        var since = DateTime.Now;
        Log(ActivityLog.Info, "Can't reach Roblox (your internet or Roblox is down). Rejoins wait until it's back; waiting doesn't count as a try.");
        try
        {
            while (!await RobloxApi.IsReachableAsync())
            {
                StatusMessage = $"No connection to Roblox for {FormatSpan(DateTime.Now - since)}. Rejoining when it's back…";
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
            Log(ActivityLog.Info, $"Roblox can be reached again (was gone for {FormatSpan(DateTime.Now - since)}). Rejoining.");
        }
        finally { _robloxWait = null; }
    }

    private async void OnClientDropped(ClientInfo c)
    {
        try
        {
            if (c.Owner != null && !c.EndedAsFrozen && !c.EndedAsDisconnected)   // those were already logged, with the reason
                Log(ActivityLog.Drop, $"{c.Name}'s client closed without AltDeck closing it (after {c.Uptime}).");

            // Auto-rejoin and auto-restart both bring back a client whose process ended without AltDeck closing it.
            if (!(Settings.AutoRejoin || Settings.AutoRestart) || c.Owner == null || !c.Owner.BringBack || c.Target == null) return;

            int generation = _rejoinGeneration;
            if (!_rejoin.TryGetValue(c.Owner.Id, out var hist)) _rejoin[c.Owner.Id] = hist = new List<DateTime>();
            int attempt = 0;
            bool slowLogged = false;

            while (true)
            {
                var cutoff = DateTime.Now - TimeSpan.FromMinutes(Math.Max(1, Settings.GiveUpMinutes));
                hist.RemoveAll(t => t < cutoff);
                bool slow = false;
                if (hist.Count >= Math.Max(1, Settings.GiveUpTries))
                {
                    if (!Settings.KeepTrying)
                    {
                        StatusMessage = $"Gave up on {c.Name} after {hist.Count} tries.";
                        Log(ActivityLog.GiveUp, $"Gave up on {c.Name}: {hist.Count} rejoins within {Math.Max(1, Settings.GiveUpMinutes)} min.");
                        Notify?.Invoke(StatusMessage);
                        return;
                    }
                    // e.g. Roblox keeps sending it to a server that was shut down: slow down, but don't stop
                    slow = true;
                    if (!slowLogged)
                    {
                        slowLogged = true;
                        Log(ActivityLog.GiveUp, $"{c.Name} failed {hist.Count} rejoins within {Math.Max(1, Settings.GiveUpMinutes)} min. Trying again every 5 min until it works.");
                    }
                }
                else hist.Add(DateTime.Now);
                attempt++;

                // 5 s, 10 s, 20 s, 40 s, then a minute: a client that keeps failing waits longer each time
                var wait = slow ? 300 : Math.Min(60, 5 * (1 << Math.Min(4, hist.Count - 1)));
                StatusMessage = $"{c.Name} disconnected. Rejoining in {FormatSpan(TimeSpan.FromSeconds(wait))}…";
                await Task.Delay(TimeSpan.FromSeconds(wait));
                if (!StillWanted(c, generation)) return;

                // Internet (or Roblox) down? Wait for it first. This doesn't count as a try.
                await WaitForRobloxAsync();
                if (!StillWanted(c, generation)) return;

                // First try goes back to the same server; after that any server, in case that one is full or gone.
                // A private server or a server the user picked keeps its own target.
                var target = c.Target;
                // Not when that server is the problem: shut down (275), not answering (279) or never reached (0).
                bool serverGone = c.EndedAsDisconnected && c.DisconnectCode is 0 or 275 or RobloxLogWatcher.NoResponseCode;
                bool sameServer = attempt == 1 && c.JoinedJobId != null && !serverGone
                                  && target.PrivateCode == null && target.ShareCode == null && target.JobId == null;
                if (sameServer) target = target with { JobId = c.JoinedJobId };

                switch (await RelaunchAsync(c, target, generation))
                {
                    case RelaunchResult.Ok:
                        Log(ActivityLog.Rejoin, slow
                            ? $"Rejoined {c.Name} (slow retry, every 5 min)."
                            : $"Rejoined {c.Name}{(sameServer ? " into the same server" : "")} (try {hist.Count} of {Math.Max(1, Settings.GiveUpTries)}).");
                        return;
                    case RelaunchResult.Offline:
                        if (!slow) hist.RemoveAt(hist.Count - 1);   // the connection dropped again before it could launch: not this account's fault
                        attempt--;
                        continue;
                    default:
                        return;   // already logged (or nothing to do any more)
                }
            }
        }
        catch { }
    }

    /// <summary>A rejoined client's window goes back where the old one was (once, as soon as the window shows up).</summary>
    private void SnapBackWindows()
    {
        if (_snapBack.Count == 0) return;
        foreach (var id in _snapBack.Keys.ToList())
        {
            var (rect, at) = _snapBack[id];
            if (DateTime.UtcNow - at > TimeSpan.FromMinutes(3)) { _snapBack.Remove(id); continue; }
            var c = ClientMon.Clients.FirstOrDefault(x => x.IsAlive && x.Owner?.Id == id);
            if (c == null) continue;
            var h = Win32.MainWindowOf(c.Pid);
            if (h == IntPtr.Zero) continue;   // not up yet
            if (Settings.StartTiny) Win32.EnsureTitleBar(h);
            Win32.Place(h, rect.X, rect.Y, rect.W, rect.H);
            _perf.MarkPlaced(c.Pid);
            _snapBack.Remove(id);
        }
    }

    // ------------------------------------------------------------------ launch arguments

    /// <summary>Global arguments first, then the ones set on this account.</summary>
    private string ArgsFor(Account a) =>
        string.Join(" ", new[] { Settings.LaunchArgs, a.LaunchArgs }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));

    // ------------------------------------------------------------------ auto-restart (frozen windows)

    /// <summary>
    /// A client whose window has been "Not responding" for 30 s is ended without marking it as stopped by the user.
    /// The next tick sees it gone, reports it as dropped, and OnClientDropped relaunches it (same give-up limit as auto-rejoin).
    /// </summary>
    private void RestartFrozenClients()
    {
        var now = DateTime.UtcNow;
        var frozen = ClientMon.Clients
            .Where(c => c.IsAlive && c.HungSince is DateTime t && now - t >= HungLimit
                        && c.Owner != null && c.Owner.BringBack && c.Target != null)
            .ToList();
        foreach (var c in frozen)
        {
            StatusMessage = $"{c.Name} stopped responding. Restarting it…";
            Log(ActivityLog.Frozen, $"{c.Name} was \"Not responding\" for {HungLimit.TotalSeconds:0} s, so it was ended to restart it.");
            ClientMon.KillHung(c);
        }
    }

    // ------------------------------------------------------------------ stop timer

    [RelayCommand]
    private void StartStopTimer(string? minutes)
    {
        if (!int.TryParse(minutes, out var m) || m <= 0) return;
        _stopAt = DateTime.Now.AddMinutes(m);
        StopTimerActive = true;
        CheckStopTimer();
        StatusMessage = $"Every Roblox client will be closed in {FormatSpan(TimeSpan.FromMinutes(m))}.";
    }

    [RelayCommand]
    private void CancelStopTimer()
    {
        _stopAt = null;
        StopTimerActive = false;
        StopTimerText = "";
        StatusMessage = "Stop timer cancelled.";
    }

    private void CheckStopTimer()
    {
        if (_stopAt is not DateTime at) return;
        var left = at - DateTime.Now;
        if (left > TimeSpan.Zero)
        {
            StopTimerText = $"Closing all clients in {FormatSpan(left)}.";
            return;
        }
        _stopAt = null;
        StopTimerActive = false;
        StopTimerText = "";
        _ = RunStopTimerAsync();
    }

    private async Task RunStopTimerAsync()
    {
        var live = ClientMon.Clients.Where(c => c.IsAlive).ToList();
        try { await Task.WhenAll(live.Select(c => ClientMon.StopAsync(c, Settings.CloseGrace))); } catch { }
        var msg = live.Count == 0
            ? "Stop timer finished. Nothing was running."
            : $"Stop timer finished: closed {live.Count} client{(live.Count == 1 ? "" : "s")}.";
        StatusMessage = msg;
        Log(ActivityLog.Stop, msg);
        Notify?.Invoke(msg);
    }

    private static string FormatSpan(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{(int)Math.Ceiling(t.TotalMinutes)} min";
        return $"{Math.Max(1, (int)t.TotalSeconds)} s";
    }

    // ------------------------------------------------------------------ master password

    private static string? ValidateNewPassword(string newPassword, string repeat) =>
        newPassword.Length < 8 ? "Use at least 8 characters."
        : newPassword != repeat ? "The two passwords don't match."
        : null;

    [RelayCommand]
    private void SetMasterPassword()
    {
        var typed = PasswordDialog.Ask(Application.Current.MainWindow, "Set master password",
            "AltDeck will ask for this password each time it starts.\n\nThere is no way to recover it: if you forget it, the saved sign-ins are lost and you have to add your accounts again.",
            new[] { "New password", "Repeat password" },
            v => ValidateNewPassword(v[0], v[1]));
        if (typed == null) return;
        try
        {
            _vault.EnablePassword(typed[0], Accounts);
            HasMasterPassword = true;
            StatusMessage = "Master password set. The vault is locked with it.";
        }
        catch (Exception ex) { StatusMessage = "Couldn't set the password: " + ex.Message; }
    }

    [RelayCommand]
    private void ChangeMasterPassword()
    {
        var typed = PasswordDialog.Ask(Application.Current.MainWindow, "Change master password",
            "Enter the current password, then choose a new one.",
            new[] { "Current password", "New password", "Repeat new password" },
            v => !_vault.VerifyPassword(v[0]) ? "The current password isn't right." : ValidateNewPassword(v[1], v[2]));
        if (typed == null) return;
        try
        {
            _vault.ChangePassword(typed[1], Accounts);
            StatusMessage = "Master password changed.";
        }
        catch (Exception ex) { StatusMessage = "Couldn't change the password: " + ex.Message; }
    }

    [RelayCommand]
    private void RemoveMasterPassword()
    {
        var typed = PasswordDialog.Ask(Application.Current.MainWindow, "Turn off master password",
            "Enter the password to go back to Windows-only protection (the vault is then encrypted for your Windows user, with no password).",
            new[] { "Current password" },
            v => _vault.VerifyPassword(v[0]) ? null : "That password isn't right.");
        if (typed == null) return;
        try
        {
            _vault.DisablePassword(Accounts);
            HasMasterPassword = false;
            StatusMessage = "Master password turned off.";
        }
        catch (Exception ex) { StatusMessage = "Couldn't turn it off: " + ex.Message; }
    }

    // ------------------------------------------------------------------ server browser + join someone

    [RelayCommand]
    private void BrowseServers()
    {
        var owner = Application.Current.MainWindow;
        var target = TargetParser.Parse(TargetText);
        if (target?.ShareCode != null || target?.PrivateCode != null)
        {
            StatusMessage = "Private servers have no server list. Paste the game's normal link to browse its public servers.";
            return;
        }

        long placeId = target?.PlaceId ?? 0;
        if (placeId == 0)
        {
            var input = PromptDialog.Ask(owner, "Browse servers", "Paste the game link or place ID:");
            if (input == null) return;
            var t = TargetParser.Parse(input);
            if (t == null || t.ShareCode != null || t.PrivateCode != null || t.PlaceId == 0)
            {
                StatusMessage = "That isn't a game link or place ID.";
                return;
            }
            placeId = t.PlaceId;
        }

        var win = new ServerBrowserWindow(placeId, _placeNames.TryGetValue(placeId, out var name) ? name : "") { Owner = owner };
        if (win.ShowDialog() == true && win.SelectedJobId != null)
        {
            TargetText = $"https://www.roblox.com/games/{placeId}?gameInstanceId={win.SelectedJobId}";
            StatusMessage = "Server picked. Choose who's playing, then launch.";
        }
    }

    /// <summary>
    /// Looks up where another player is right now (using one of your signed-in accounts) and puts that server into the
    /// target box. Roblox only shows this to accounts the player allows (friends, or everyone if they let anyone join).
    /// </summary>
    [RelayCommand]
    private async Task JoinSomeoneAsync()
    {
        var viewer = Accounts.FirstOrDefault(a => a.IsSelected && a.StatusKind != "signedout")
                     ?? Accounts.FirstOrDefault(a => a.StatusKind != "signedout");
        if (viewer == null)
        {
            StatusMessage = "Add or sign in an account first. AltDeck uses it to see where they are.";
            return;
        }

        var input = PromptDialog.Ask(Application.Current.MainWindow, "Join someone",
            $"Roblox username (or profile link) of the player to join. AltDeck looks where they are using {viewer.Label}, " +
            "so that account must be allowed to see it: a friend, or the player lets anyone join.");
        if (input == null) return;

        try
        {
            StatusMessage = "Looking them up…";
            var (id, error) = await RobloxApi.ResolveUserAsync(viewer.Cookie, input);
            if (id == 0) { StatusMessage = error ?? "Couldn't find that player."; return; }

            var p = await RobloxApi.GetPresenceAsync(viewer.Cookie, id);
            if (p == null) { StatusMessage = "Roblox didn't say where they are."; return; }
            if (p.Type != 2)
            {
                StatusMessage = p.Type == 1 ? "They're online but not in a game right now."
                              : p.Type == 3 ? "They're in Roblox Studio, not a game."
                              : "They're offline right now.";
                return;
            }
            if (p.PlaceId == 0)
            {
                StatusMessage = $"They're in a game, but {viewer.Label} isn't allowed to see which one (privacy settings).";
                return;
            }

            if (!string.IsNullOrEmpty(p.Location)) _placeNames[p.PlaceId] = p.Location;
            if (string.IsNullOrEmpty(p.GameId))
            {
                TargetText = $"https://www.roblox.com/games/{p.PlaceId}";
                StatusMessage = "Found the game, but not their exact server, so launching would pick any server. Try again in a moment.";
                return;
            }
            TargetText = $"https://www.roblox.com/games/{p.PlaceId}?gameInstanceId={p.GameId}";
            StatusMessage = string.IsNullOrEmpty(p.Location)
                ? "Found them. Choose who's playing, then launch."
                : $"Found them in {p.Location}. Choose who's playing, then launch.";
            Page = "Play";
        }
        catch (Exception ex) { StatusMessage = "Couldn't check where they are: " + ex.Message; }
    }
}
