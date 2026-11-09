using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Data;
using RoDeck.Models;
using RoDeck.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RoDeck.ViewModels;

/// <summary>One account waiting in (or done with) the launch queue.</summary>
public partial class QueueItem : ObservableObject
{
    public Account Account { get; init; } = null!;
    public LaunchTarget Target { get; init; } = null!;
    /// <summary>From Multi-launch: auto-rejoin / auto-restart may bring it back (see ClientInfo.Farm).</summary>
    public bool Farm { get; init; } = true;
    public string Name => Account.Label;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    [NotifyPropertyChangedFor(nameof(IsWaiting))]
    private string state = "Waiting";   // Waiting, Launching, Launched, Skipped, Failed, Signed out, Already playing, Cancelled

    public bool IsDone => State is not ("Waiting" or "Launching");
    public bool IsWaiting => State == "Waiting";
}

/// <summary>
/// 0.4.0 features: activity log, encrypted backup, launch queue, launch presets, sign-in checks
/// and window layout profiles.
/// </summary>
public partial class MainViewModel
{
    private readonly ActivityLog _log = ActivityLog.Shared;

    private void Log(string kind, string message) => _log.Add(kind, message);
    private void QueueSettingsSave() { _saveTimer.Stop(); _saveTimer.Start(); }

    /// <summary>Called once from the constructor.</summary>
    private void SetupFeatures()
    {
        _log.LoadToday();
        LogView = new CollectionViewSource { Source = _log.Entries }.View;
        LogView.Filter = o => o is LogEntry e && (LogFilter != "Problems" || e.IsProblem);

        foreach (var p in Settings.Presets) HookPreset(p);
        Settings.Presets.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null) foreach (LaunchPreset p in e.NewItems) HookPreset(p);
            PresetHotkeysChanged?.Invoke();
        };
        _lastSweep = DateTime.Now;
        Log(ActivityLog.Info, $"RoDeck {AppVersion} started with {Accounts.Count} account{(Accounts.Count == 1 ? "" : "s")}.");
        if (DataMigration.Moved)
            Log(ActivityLog.Info, "AltDeck is now RoDeck: your accounts and settings were copied over from the AltDeck folder.");
    }

    private string DescribeTarget(LaunchTarget t)
    {
        var place = _placeNames.TryGetValue(t.PlaceId, out var n) ? n : "place " + t.PlaceId;
        if (t.PrivateCode != null) return place + " (private server)";
        if (t.JobId != null) return place + " (server " + t.JobId[..8] + ")";
        return place;
    }

    // ================================================================== activity log

    public ICollectionView LogView { get; private set; } = null!;
    [ObservableProperty] private string logFilter = "All";
    partial void OnLogFilterChanged(string value) => LogView?.Refresh();

    [RelayCommand]
    private void CopyLog()
    {
        var lines = LogView.Cast<LogEntry>().Reverse().Select(e => e.ToString()).ToList();   // oldest first reads better in a report
        if (lines.Count == 0) { StatusMessage = "The log is empty."; return; }
        try { Clipboard.SetText(string.Join(Environment.NewLine, lines)); StatusMessage = $"Copied {lines.Count} log lines."; }
        catch { StatusMessage = "Couldn't copy to the clipboard."; }
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(_log.LogDir);
            Process.Start(new ProcessStartInfo("explorer.exe", _log.LogDir) { UseShellExecute = true });
        }
        catch { }
    }

    [RelayCommand]
    private void ClearLog()
    {
        _log.ClearView();
        StatusMessage = "Cleared the list. The log files in the data folder are kept.";
    }

    // ================================================================== backup / transfer

    [RelayCommand]
    private async Task ExportBackupAsync()
    {
        if (Accounts.Count == 0) { StatusMessage = "There are no accounts to back up yet."; return; }
        var owner = Application.Current.MainWindow;

        var typed = PasswordDialog.Ask(owner, "Export backup",
            "Choose a password for the backup file. You need it to import the file, on this PC or another one.\n\n" +
            "The file holds your accounts' sign-ins: keep it somewhere private. Anyone with the file AND the password can use them.",
            new[] { "Backup password", "Repeat password" },
            v => ValidateNewPassword(v[0], v[1]));
        if (typed == null) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = BackupService.FileFilter,
            DefaultExt = BackupService.Extension,
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"RoDeck-backup-{DateTime.Now:yyyy-MM-dd}{BackupService.Extension}"
        };
        if (dlg.ShowDialog(owner) != true) return;

        var data = new BackupData
        {
            AppVersion = AppVersion,
            Accounts = Accounts.ToList(),
            Favourites = Settings.Favourites.ToList(),
            Presets = Settings.Presets.ToList(),
            Layouts = Settings.Layouts.ToList()
        };
        var path = dlg.FileName;
        var password = typed[0];
        try
        {
            StatusMessage = "Encrypting the backup…";
            await Task.Run(() => BackupService.Export(path, password, data));
            StatusMessage = $"Backed up {data.Accounts.Count} account{(data.Accounts.Count == 1 ? "" : "s")} to {Path.GetFileName(path)}.";
            Log(ActivityLog.Backup, $"Exported {data.Accounts.Count} accounts, {data.Presets.Count} presets and {data.Layouts.Count} layouts to {path}.");
        }
        catch (Exception ex)
        {
            StatusMessage = "Couldn't write the backup: " + ex.Message;
            Log(ActivityLog.Error, "Backup export failed: " + ex.Message);
        }
    }

    [RelayCommand]
    private async Task ImportBackupAsync()
    {
        var owner = Application.Current.MainWindow;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = BackupService.OpenFilter, CheckFileExists = true };
        if (dlg.ShowDialog(owner) != true) return;
        var path = dlg.FileName;

        BackupData? data = null;
        var typed = PasswordDialog.Ask(owner, "Import backup",
            $"Enter the password chosen when {Path.GetFileName(path)} was exported.\n\n" +
            "Accounts you already have here are kept as they are. Only new ones are added (and a signed-out account gets the backup's sign-in).",
            new[] { "Backup password" },
            v =>
            {
                data = BackupService.Import(path, v[0], out var error);
                return data == null ? error : null;
            });
        if (typed == null || data == null) return;

        var added = MergeBackup(data, out var kept, out var refreshed, out var presets, out var layouts);
        var parts = new List<string> { $"{added.Count} new account{(added.Count == 1 ? "" : "s")}" };
        if (kept > 0) parts.Add($"{kept} already here");
        if (refreshed > 0) parts.Add($"{refreshed} signed back in");
        if (presets > 0) parts.Add($"{presets} preset{(presets == 1 ? "" : "s")}");
        if (layouts > 0) parts.Add($"{layouts} layout{(layouts == 1 ? "" : "s")}");
        StatusMessage = "Imported: " + string.Join(", ", parts) + ".";
        Log(ActivityLog.Backup, $"Imported {path}: " + string.Join(", ", parts) + ".");

        foreach (var a in added.Concat(Accounts.Where(x => x.StatusKind == "signedout")).Distinct().ToList())
            await CheckAccountAsync(a);
        AccountsView.Refresh();
        UpdateHomeTexts();
        QueueVaultSave();
    }

    /// <summary>Adds what the backup has and this PC doesn't. Returns the accounts that were added.</summary>
    private List<Account> MergeBackup(BackupData data, out int kept, out int refreshed, out int presets, out int layouts)
    {
        kept = refreshed = presets = layouts = 0;
        var added = new List<Account>();
        var idMap = new Dictionary<string, string>();   // backup's Account.Id -> the Id used here

        foreach (var acc in data.Accounts)
        {
            if (acc.UserId == 0 || string.IsNullOrEmpty(acc.Cookie)) continue;
            var existing = Accounts.FirstOrDefault(a => a.UserId == acc.UserId);
            if (existing != null)
            {
                idMap[acc.Id] = existing.Id;
                kept++;
                if (existing.StatusKind == "signedout" && acc.Cookie != existing.Cookie)
                {
                    existing.Cookie = acc.Cookie;
                    refreshed++;
                }
                continue;
            }
            var oldId = acc.Id;
            if (string.IsNullOrEmpty(acc.Id) || Accounts.Any(a => a.Id == acc.Id)) acc.Id = Guid.NewGuid().ToString("N");
            idMap[oldId] = acc.Id;
            acc.IsSelected = false;
            Accounts.Add(acc);
            HookAccount(acc);
            added.Add(acc);
        }

        foreach (var f in data.Favourites)
            if (!Settings.Favourites.Any(x => x.Target == f.Target)) Settings.Favourites.Add(f);

        foreach (var p in data.Presets)
        {
            if (Settings.Presets.Any(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase))) continue;
            p.AccountIds = p.AccountIds.Select(id => idMap.TryGetValue(id, out var n) ? n : id)
                                       .Where(id => Accounts.Any(a => a.Id == id)).Distinct().ToList();
            if (p.HasHotkey && Settings.Presets.Any(x => string.Equals(x.Hotkey, p.Hotkey, StringComparison.OrdinalIgnoreCase)))
                p.Hotkey = "";   // would clash with a hotkey you already use
            Settings.Presets.Add(p);
            presets++;
        }

        foreach (var l in data.Layouts)
        {
            if (Settings.Layouts.Any(x => string.Equals(x.Name, l.Name, StringComparison.OrdinalIgnoreCase))) continue;
            foreach (var s in l.Slots) if (idMap.TryGetValue(s.AccountId, out var n)) s.AccountId = n;
            Settings.Layouts.Add(l);
            layouts++;
        }

        try { _vault.Save(Accounts); } catch (Exception ex) { StatusMessage = "Couldn't save vault: " + ex.Message; }
        QueueSettingsSave();
        RebuildGroups();
        UpdatePicked();
        return added;
    }

    // ================================================================== launch queue

    public ObservableCollection<QueueItem> LaunchQueue { get; } = new();
    [ObservableProperty] private bool queueVisible;
    [ObservableProperty] private bool queueRunning;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(QueuePauseText))] private bool queuePaused;
    [ObservableProperty] private string queueTitle = "";
    [ObservableProperty] private string queueSubtitle = "";
    [ObservableProperty] private double queueProgress;
    public string QueuePauseText => QueuePaused ? "Resume" : "Pause";

    private bool _cancelQueue;
    private int _queueGeneration;

    [RelayCommand]
    private void ToggleQueuePause()
    {
        if (!QueueRunning) return;
        QueuePaused = !QueuePaused;
        UpdateQueueTexts(null, null);
    }

    [RelayCommand]
    private void SkipNext() => SkipItem(LaunchQueue.FirstOrDefault(q => q.IsWaiting));

    [RelayCommand]
    private void SkipItem(QueueItem? q)
    {
        if (q == null || !q.IsWaiting) return;
        q.State = "Skipped";
        Log(ActivityLog.Launch, $"Skipped {q.Name} in the launch queue.");
        UpdateQueueTexts(null, null);
    }

    [RelayCommand]
    private void CancelQueue()
    {
        if (!QueueRunning) return;
        _cancelQueue = true;
        QueuePaused = false;
        QueueSubtitle = "Cancelling…";
    }

    [RelayCommand]
    private void DismissQueue()
    {
        if (QueueRunning) return;
        LaunchQueue.Clear();
        QueueVisible = false;
    }

    /// <summary>Puts accounts in the launch queue for this target and starts the queue if it isn't running.</summary>
    /// <param name="launcher">Started from Home / Games (RoDeck as a normal launcher): stay on the page, don't farm
    /// (no auto-rejoin / auto-restart), and if the account is already in a game, close that game first.</param>
    private async Task LaunchAccountsAsync(List<Account> accounts, string targetText, string? via = null, bool launcher = false)
    {
        bool showRunning = !launcher;
        if (accounts.Count == 0) return;
        var target = TargetParser.Parse(targetText);
        if (target == null) { StatusMessage = "Paste a game link or place ID first."; return; }

        // A roblox.com/share?code=...&type=Server link has to be looked up first (any of the accounts can do it).
        if (target.ShareCode != null)
        {
            StatusMessage = "Reading the private server link…";
            var (resolved, error) = await RobloxApi.ResolveShareLinkAsync(accounts[0].Cookie, target.ShareCode, target.ShareType ?? "Server");
            if (resolved == null) { StatusMessage = error ?? "Couldn't read that link."; return; }
            target = resolved;
        }

        // several at once: their windows go next to each other (one on its own keeps Roblox's size)
        if (accounts.Count > 1) _perf.ArrangeUntil = DateTime.Now.AddSeconds(30 + accounts.Count * Math.Max(5, Settings.LaunchDelay) * 2);
        RefreshLock();
        if (Settings.MultiInstance && !_launcher.IsActive)
        {
            // Another Roblox (one RoDeck didn't start) holds Roblox's lock. Launching would close it anyway, so close it now
            // and take the lock: this launch goes ahead, and the ones after it can run side by side.
            StatusMessage = "Closing your other Roblox client…";
            int closed = await Task.Run(() => Launcher.CloseAllRobloxClients());
            RefreshLock();
            if (!_launcher.IsActive)
            {
                StatusMessage = "Roblox is still holding its lock. In Task Manager, end RobloxPlayerBeta.exe, then press Play again.";
                return;
            }
            Log(ActivityLog.Info, $"Closed {closed} Roblox process(es) to take the multi-instance lock before launching.");
        }

        if (launcher)
        {
            // Pressing Play / Join on a game means "go there now": close the game this account is in first.
            var open = ClientMon.Clients.Where(c => c.IsAlive && c.Owner != null && accounts.Contains(c.Owner)).ToList();
            if (open.Count > 0)
            {
                StatusMessage = $"Leaving {open[0].Name}'s current game…";
                foreach (var c in open) ClientMon.KillNow(c);
                for (int i = 0; i < 25 && open.Any(c => ProcessAlive(c.Pid)); i++) await Task.Delay(200);
                foreach (var c in open) { ClientMon.Clients.Remove(c); if (c.Owner != null) c.Owner.IsPlaying = false; }
                Log(ActivityLog.Stop, $"Closed {string.Join(", ", open.Select(c => c.Name))}'s game to join another one.");
            }
        }

        if (!QueueRunning) LaunchQueue.Clear();   // the last run's finished rows
        int added = 0;
        foreach (var a in accounts)
        {
            if (a.IsPlaying || LaunchQueue.Any(q => q.Account == a && !q.IsDone)) continue;
            LaunchQueue.Add(new QueueItem { Account = a, Target = target, Farm = !launcher });
            a.IsSelected = false;
            added++;
        }
        if (added == 0)
        {
            StatusMessage = "Those accounts are already playing or waiting to launch.";
            return;
        }

        Log(ActivityLog.Launch, $"Queued {added} account{(added == 1 ? "" : "s")} for {DescribeTarget(target)}{(via != null ? " (" + via + ")" : "")}.");
        _queueGeneration++;
        QueueVisible = true;
        if (showRunning) GoMulti("Running");
        RememberRecent(target.PlaceId);
        if (QueueRunning) UpdateQueueTexts(null, null);
        else _ = RunQueueAsync();
    }

    private async Task RunQueueAsync()
    {
        QueueRunning = true;
        QueuePaused = false;
        _cancelQueue = false;
        string? perfNote = null;
        bool needDelay = false;
        UpdateQueueTexts(null, null);

        try
        {
            while (!_cancelQueue && LaunchQueue.Any(q => q.IsWaiting))
            {
                if (needDelay) await CountdownAsync(TimeSpan.FromSeconds(Math.Max(1, Settings.LaunchDelay)));
                while (QueuePaused && !_cancelQueue) { UpdateQueueTexts(null, null); await Task.Delay(250); }
                if (_cancelQueue) break;

                var next = LaunchQueue.FirstOrDefault(q => q.IsWaiting);   // "Skip" may have changed it while we waited
                if (next == null) break;
                var a = next.Account;
                if (a.IsPlaying) { next.State = "Already playing"; continue; }

                next.State = "Launching";
                UpdateQueueTexts(null, null);

                if (!await SignInLooksValidAsync(a))
                {
                    next.State = "Signed out";
                    Log(ActivityLog.SignIn, $"{a.Label} is signed out, so its launch was skipped. Add it again on the Accounts page.");
                    UpdateHomeTexts();
                    continue;
                }

                await _launchGate.WaitAsync();   // a rejoin may be launching right now: never two at once
                try
                {
                    var note = await _perf.PrepareLaunchAsync(Settings, a);   // every account, not just the first
                    perfNote ??= note;
                    if (Settings.IsolateSignIns) SignInIsolation.PrepareLaunch();
                    await _launcher.LaunchAsync(a, next.Target, ArgsFor(a));
                    if (Settings.IsolateSignIns) SignInIsolation.LaunchStarted();
                    _lastLaunchAt = DateTime.UtcNow;
                    _perf.LaunchStarted();
                    ClientMon.ExpectLaunch(a, next.Target, next.Farm);
                    foreach (var old in ClientMon.Clients.Where(c => c.Owner == a && c.IsClosed).ToList())
                        ClientMon.Clients.Remove(old);
                    _ = LoadPlaceNameAsync(a, next.Target.PlaceId);
                    next.State = "Launched";
                    Log(ActivityLog.Launch, $"Launched {a.Label} into {DescribeTarget(next.Target)}.");
                }
                catch (Exception ex)
                {
                    SetStatus(a, "check", "Launch failed");
                    next.State = "Failed";
                    StatusMessage = $"{a.Label}: {ex.Message}";
                    Log(ActivityLog.Error, $"Launch of {a.Label} failed: {ex.Message}");
                }
                finally { _launchGate.Release(); }
                needDelay = true;
                UpdateQueueTexts(null, null);
            }
        }
        catch (Exception ex)
        {
            Log(ActivityLog.Error, "The launch queue stopped: " + ex.Message);
        }
        finally
        {
            bool cancelled = _cancelQueue;
            foreach (var q in LaunchQueue.Where(q => q.IsWaiting).ToList()) q.State = "Cancelled";
            QueueRunning = false;
            QueuePaused = false;
            _cancelQueue = false;

            int launched = LaunchQueue.Count(q => q.State == "Launched");
            QueueTitle = $"{(cancelled ? "Cancelled" : "Done")}: {launched} of {LaunchQueue.Count} launched";
            QueueSubtitle = perfNote ?? "";
            QueueProgress = 100;
            if (cancelled) Log(ActivityLog.Launch, $"Launch queue cancelled after {launched} launch{(launched == 1 ? "" : "es")}.");
            StatusMessage = cancelled ? "Launch queue cancelled."
                          : string.IsNullOrEmpty(perfNote) ? "Launch finished." : "Launch finished. " + perfNote;
            _ = HideQueueLaterAsync(_queueGeneration);
        }
    }

    /// <summary>Waits between two launches. Pause holds the countdown, Cancel ends it.</summary>
    private async Task CountdownAsync(TimeSpan wait)
    {
        var end = DateTime.UtcNow + wait;
        while (!_cancelQueue)
        {
            if (QueuePaused) end = end.AddMilliseconds(250);
            var left = end - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !LaunchQueue.Any(q => q.IsWaiting)) return;
            UpdateQueueTexts(null, left);
            await Task.Delay(250);
        }
    }

    private async Task HideQueueLaterAsync(int generation)
    {
        await Task.Delay(TimeSpan.FromSeconds(12));
        if (!QueueRunning && generation == _queueGeneration)
        {
            LaunchQueue.Clear();
            QueueVisible = false;
        }
    }

    private void UpdateQueueTexts(QueueItem? next, TimeSpan? left)
    {
        int total = LaunchQueue.Count;
        int launched = LaunchQueue.Count(q => q.State == "Launched");
        int done = LaunchQueue.Count(q => q.IsDone);
        QueueTitle = $"{launched} of {total} launched";
        QueueProgress = total == 0 ? 0 : done * 100.0 / total;

        var launching = LaunchQueue.FirstOrDefault(q => q.State == "Launching");
        next ??= LaunchQueue.FirstOrDefault(q => q.IsWaiting);
        QueueSubtitle =
            QueuePaused ? (next != null ? $"Paused · next: {next.Name}" : "Paused")
            : launching != null ? $"Launching {launching.Name}…"
            : next != null && left is TimeSpan l ? $"Next: {next.Name} in {Math.Ceiling(l.TotalSeconds):0} s"
            : next != null ? $"Next: {next.Name}"
            : "";
    }

    /// <summary>Checks the sign-in just before a launch, unless it was checked in the last 10 minutes.</summary>
    private async Task<bool> SignInLooksValidAsync(Account a)
    {
        if (a.StatusKind != "signedout" && a.LastChecked is DateTime t && DateTime.Now - t < TimeSpan.FromMinutes(10))
            return true;
        try
        {
            var u = await RobloxApi.GetAuthenticatedUserAsync(a.Cookie);
            if (u != null) return true;
            SetStatus(a, "signedout", "Signed out");
            return false;
        }
        catch { return true; }   // network trouble: try the launch anyway, it reports its own error
    }

    // ================================================================== launch presets

    /// <summary>A preset was added, removed, or its hotkey changed: MainWindow registers the hotkeys again.</summary>
    public event Action? PresetHotkeysChanged;

    private void HookPreset(LaunchPreset p) =>
        p.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LaunchPreset.Hotkey)) PresetHotkeysChanged?.Invoke();
            if (e.PropertyName is nameof(LaunchPreset.Hotkey) or nameof(LaunchPreset.Name) or nameof(LaunchPreset.Target))
                QueueSettingsSave();
        };

    [RelayCommand]
    private void SavePreset()
    {
        var picked = Accounts.Where(a => a.IsSelected).ToList();
        if (picked.Count == 0) { StatusMessage = "Tick the accounts for the preset first."; return; }
        if (TargetParser.Parse(TargetText) == null) { StatusMessage = "Paste where to play first, so the preset knows the server."; return; }

        var name = PromptDialog.Ask(Application.Current.MainWindow, "Save preset",
            $"Name for these {picked.Count} account{(picked.Count == 1 ? "" : "s")} and this target (for example “Farm squad”):");
        if (name == null) return;

        var existing = Settings.Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (MessageBox.Show($"A preset called {existing.Name} exists. Replace it?", "RoDeck", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                return;
            existing.Target = TargetText.Trim();
            existing.AccountIds = picked.Select(a => a.Id).ToList();
            existing.AccountsChanged();
        }
        else
        {
            Settings.Presets.Add(new LaunchPreset { Name = name, Target = TargetText.Trim(), AccountIds = picked.Select(a => a.Id).ToList() });
        }
        QueueSettingsSave();
        StatusMessage = $"Saved preset {name}. Click it to launch, right-click for a hotkey.";
    }

    [RelayCommand]
    private Task LaunchPresetAsync(LaunchPreset? p) => p == null ? Task.CompletedTask : RunPresetAsync(p, "Play page");

    /// <summary>Launches everyone in the preset who isn't playing yet.</summary>
    public async Task RunPresetAsync(LaunchPreset p, string via)
    {
        var members = p.AccountIds.Select(id => Accounts.FirstOrDefault(a => a.Id == id)).OfType<Account>().ToList();
        if (members.Count == 0) { StatusMessage = $"None of the accounts in {p.Name} are here any more."; return; }
        var toLaunch = members.Where(a => !a.IsPlaying).ToList();
        if (toLaunch.Count == 0) { StatusMessage = $"Everyone in {p.Name} is already playing."; Notify?.Invoke(StatusMessage); return; }

        Log(ActivityLog.Preset, $"Preset {p.Name} ({via}): {toLaunch.Count} of {members.Count} accounts to launch.");
        await LaunchAccountsAsync(toLaunch, p.Target, "preset " + p.Name);
        if (via == "hotkey") Notify?.Invoke($"{p.Name}: launching {toLaunch.Count} account{(toLaunch.Count == 1 ? "" : "s")}.");
    }

    /// <summary>Ticks the preset's accounts and fills in its target, without launching.</summary>
    public void LoadPreset(LaunchPreset p)
    {
        foreach (var a in Accounts) a.IsSelected = a.CanPick && p.AccountIds.Contains(a.Id);
        TargetText = p.Target;
        GoMulti("Launch");
        StatusMessage = $"Loaded {p.Name}. Change anything, then launch.";
    }

    public void UpdatePresetFromCurrent(LaunchPreset p)
    {
        var picked = Accounts.Where(a => a.IsSelected).ToList();
        if (picked.Count == 0 || TargetParser.Parse(TargetText) == null)
        {
            StatusMessage = "Tick the accounts and paste the target first.";
            return;
        }
        p.AccountIds = picked.Select(a => a.Id).ToList();
        p.Target = TargetText.Trim();
        p.AccountsChanged();
        QueueSettingsSave();
        StatusMessage = $"{p.Name} now has {picked.Count} account{(picked.Count == 1 ? "" : "s")} and the current target.";
    }

    public void RenamePreset(LaunchPreset p)
    {
        var name = PromptDialog.Ask(Application.Current.MainWindow, "Rename preset", "New name:", p.Name);
        if (name != null) p.Name = name;
    }

    public void SetPresetHotkey(LaunchPreset p)
    {
        var keys = HotkeyDialog.Ask(Application.Current.MainWindow, "Preset hotkey",
            $"Press the keys that launch {p.Name} from any app (for example Ctrl+Shift+1).", p.Hotkey);
        if (keys == null) return;
        p.Hotkey = keys;   // MainWindow registers it right away and fills in HotkeyStatus
        StatusMessage = keys.Length == 0 ? $"{p.Name} has no hotkey now." : $"{p.Name}: {p.HotkeyStatus}";
    }

    public void DeletePreset(LaunchPreset p)
    {
        if (MessageBox.Show($"Delete the preset {p.Name}?", "RoDeck", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        Settings.Presets.Remove(p);
        QueueSettingsSave();
    }

    // ================================================================== sign-in checks

    private DateTime _lastSweep;
    private bool _sweeping;

    private void MaybeSweep()
    {
        if (Settings.HealthCheckMinutes <= 0 || _sweeping || Accounts.Count == 0) return;
        var every = TimeSpan.FromMinutes(Math.Max(5, Settings.HealthCheckMinutes));
        if (DateTime.Now - _lastSweep < every) return;
        _ = SweepAsync();
    }

    /// <summary>Quietly checks every sign-in, one by one, and says so when one has expired.</summary>
    private async Task SweepAsync()
    {
        _sweeping = true;
        try
        {
            var before = SignedOutIds();
            foreach (var a in Accounts.ToList())
            {
                if (!Accounts.Contains(a)) continue;
                await CheckAccountAsync(a);
                await Task.Delay(500);   // gentle on Roblox's rate limits
            }
            AccountsView.Refresh();
            UpdateHomeTexts();
            QueueVaultSave();
            ReportNewSignOuts(before);
        }
        catch { }
        finally
        {
            _sweeping = false;
            _lastSweep = DateTime.Now;
        }
    }

    private HashSet<string> SignedOutIds() => Accounts.Where(a => a.StatusKind == "signedout").Select(a => a.Id).ToHashSet();

    private void ReportNewSignOuts(HashSet<string> before)
    {
        var newly = Accounts.Where(a => a.StatusKind == "signedout" && !before.Contains(a.Id)).ToList();
        if (newly.Count == 0) return;
        foreach (var a in newly) Log(ActivityLog.SignIn, $"{a.Label}: the saved sign-in has expired.");

        var names = string.Join(", ", newly.Take(3).Select(a => a.Label)) + (newly.Count > 3 ? $" and {newly.Count - 3} more" : "");
        var msg = newly.Count == 1
            ? $"{names}'s sign-in has expired. Add it again on the Accounts page."
            : $"{newly.Count} sign-ins have expired ({names}). Add them again on the Accounts page.";
        StatusMessage = msg;
        Notify?.Invoke(msg);
    }

    [RelayCommand]
    private void ShowNeedsSignIn()
    {
        GoMulti("Accounts");
        StatusFilter = "Needs sign-in";
    }

    // ================================================================== window layout profiles

    public void SaveCurrentLayout()
    {
        var slots = new List<WindowSlot>();
        int minimized = 0;
        foreach (var c in ClientMon.Clients.Where(c => c.IsAlive && c.Owner != null))
        {
            var h = Win32.MainWindowOf(c.Pid);
            if (Win32.TryGetRect(h, out var x, out var y, out var w, out var hgt))
                slots.Add(new WindowSlot { AccountId = c.Owner!.Id, Name = c.Owner.Label, X = x, Y = y, W = w, H = hgt });
            else minimized++;
        }
        if (slots.Count == 0)
        {
            StatusMessage = minimized > 0
                ? "All client windows are minimized, so there's nothing to measure. Show them first."
                : "Start some clients first, then arrange their windows and save.";
            return;
        }

        var name = PromptDialog.Ask(Application.Current.MainWindow, "Save window layout",
            $"Name for this layout of {slots.Count} window{(slots.Count == 1 ? "" : "s")}" +
            (minimized > 0 ? $" ({minimized} minimized one{(minimized == 1 ? " is" : "s are")} left out)" : "") + ":",
            $"Layout {Settings.Layouts.Count + 1}");
        if (name == null) return;

        var existing = Settings.Layouts.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null) Settings.Layouts.Remove(existing);
        Settings.Layouts.Add(new LayoutProfile { Name = name, Slots = slots });
        QueueSettingsSave();
        StatusMessage = $"Saved layout {name}.";
        Log(ActivityLog.Layout, $"Saved layout {name} with {slots.Count} windows.");
    }

    public void RestoreLayout(LayoutProfile l)
    {
        var live = ClientMon.Clients.Where(c => c.IsAlive && c.Owner != null).ToList();
        int placed = 0;
        var missing = new List<string>();
        foreach (var s in l.Slots)
        {
            var c = live.FirstOrDefault(x => x.Owner!.Id == s.AccountId);
            var h = c == null ? IntPtr.Zero : Win32.MainWindowOf(c.Pid);
            if (h == IntPtr.Zero) { missing.Add(s.Name); continue; }
            Win32.Place(h, s.X, s.Y, s.W, s.H);
            placed++;
        }
        StatusMessage = placed == 0
            ? $"None of the accounts in {l.Name} are running."
            : $"Placed {placed} window{(placed == 1 ? "" : "s")} from {l.Name}." +
              (missing.Count > 0 ? $" Not running: {string.Join(", ", missing.Take(4))}{(missing.Count > 4 ? "…" : "")}." : "");
    }

    public void DeleteLayout(LayoutProfile l)
    {
        Settings.Layouts.Remove(l);
        QueueSettingsSave();
        StatusMessage = $"Deleted layout {l.Name}.";
    }

    private static bool ProcessAlive(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }
}
