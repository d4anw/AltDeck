using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using RoDeck.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RoDeck.ViewModels;

/// <summary>
/// Updates: when RoDeck opens it checks GitHub, and if a newer version is out the window asks whether to update
/// (with a download progress bar, then a restart into the new version). "Not now" asks again next start;
/// "Don't notify me about updates again" switches the start-up check off. Settings → About can always check by hand.
/// </summary>
public partial class MainViewModel
{
    private bool _updateBusy;
    private bool _updateOffered;                                       // the update window was shown this run
    private readonly TaskCompletionSource _startCheckDone = new();    // the start-up update check has finished

    private const string UpdateBaseText =
        "When a new version of RoDeck is out, asks when RoDeck opens whether to update. Off: check by hand under About.";
    [ObservableProperty] private string updateStatus = "";
    public string UpdateDescription => string.IsNullOrEmpty(UpdateStatus) ? UpdateBaseText : UpdateBaseText + "\n" + UpdateStatus;
    partial void OnUpdateStatusChanged(string value) => OnPropertyChanged(nameof(UpdateDescription));

    public string AboutTitle => "RoDeck " + AppVersion;

    /// <summary>A newer version is out: the window asks whether to update. Args: the update, its release notes
    /// (null if GitHub didn't give them) and whether the person pressed "Check for updates".</summary>
    public event Action<UpdateInfo, string?, bool>? UpdateAvailable;

    /// <summary>This copy was just updated: the window shows these release notes once.</summary>
    public event Action<Version, string>? WhatsNew;

    /// <summary>Called once from the constructor.</summary>
    private void SetupUpdates()
    {
        Updater.CleanUp();

        // "What's new" once per version. A fresh install has nothing to compare with; settings from 0.4.2 and older
        // have no LastSeenVersion yet, so an existing settings.json without one counts as an update too.
        var last = Settings.LastSeenVersion;
        if (last != AppVersion)
        {
            bool updated = last.Length > 0 || SettingsStore.ExistedAtStart;
            Settings.LastSeenVersion = AppVersion;
            if (updated) _ = ShowWhatsNewAsync();
        }

        if (!Updater.CanUpdate) UpdateStatus = "This copy isn't the RoDeck.exe from GitHub Releases, so it doesn't update itself.";
    }

    /// <summary>Called by the window once it's shown.</summary>
    public async Task CheckForUpdatesAtStartAsync()
    {
        try { await CheckForUpdateAsync(manual: false); }
        finally { _startCheckDone.TrySetResult(); }
    }

    /// <summary>The person chose Update and the download is done: don't show "What's new" for a version they just read about.</summary>
    public void UpdateAccepted(UpdateInfo u)
    {
        Settings.LastSeenVersion = $"{u.Version.Major}.{u.Version.Minor}.{Math.Max(u.Version.Build, 0)}";
        Log(ActivityLog.Info, $"Updating to RoDeck {Settings.LastSeenVersion}.");
    }

    /// <summary>The "Don't notify me about updates again" box.</summary>
    public void StopUpdatePrompts()
    {
        Settings.AutoUpdate = false;
        Log(ActivityLog.Info, "Update prompts switched off. Check by hand under Settings → About.");
    }

    private async Task ShowWhatsNewAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));   // after the main window is up
            // An update is waiting? Then these notes are about to be out of date: the update window shows the new ones instead.
            await Task.WhenAny(_startCheckDone.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            if (_updateOffered) return;
            var notes = await Updater.GetReleaseNotesAsync(Updater.Current);
            if (notes != null) WhatsNew?.Invoke(Updater.Current, notes);
        }
        catch { }   // offline or GitHub busy: skip it, it's only a nicety
    }

    /// <summary>Opens a new GitHub issue with the versions filled in, and copies the Activity log to paste into it.</summary>
    [RelayCommand]
    private void ReportBug()
    {
        var lines = _log.Entries.Take(80).Reverse().Select(e => e.ToString()).ToList();   // newest 80, oldest first
        bool copied = false;
        if (lines.Count > 0)
            try { Clipboard.SetText(string.Join(Environment.NewLine, lines)); copied = true; } catch { }

        var nl = "\n";
        var body =
            "**What happened?**" + nl + nl + nl +
            "**What did you expect to happen?**" + nl + nl + nl +
            "**Steps to make it happen again**" + nl + "1. " + nl + "2. " + nl + nl +
            "<details><summary>Activity log</summary>" + nl + nl + "```" + nl +
            (copied ? "(press Ctrl+V here; check it for account names first)" : "(open the Activity page, press Copy and paste it here)") +
            nl + "```" + nl + "</details>" + nl + nl +
            "---" + nl + $"RoDeck {AppVersion} · {RuntimeInformation.OSDescription.Trim()} · {RuntimeInformation.OSArchitecture}";
        var url = $"https://github.com/{Updater.Repo}/issues/new?labels=bug&body={Uri.EscapeDataString(body)}";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { StatusMessage = "Couldn't open your browser."; return; }

        StatusMessage = copied
            ? "Opened a bug report on GitHub. Your Activity log is copied: check it for names, then paste it in."
            : "Opened a bug report on GitHub.";
    }

    [RelayCommand]
    private Task CheckForUpdates() => CheckForUpdateAsync(manual: true);

    private async Task CheckForUpdateAsync(bool manual)
    {
        if (_updateBusy) return;
        if (!manual && !Settings.AutoUpdate) return;
        if (!Updater.CanUpdate)
        {
            if (manual) StatusMessage = "Only the RoDeck.exe from GitHub Releases updates itself.";
            return;
        }
        _updateBusy = true;
        try
        {
            if (manual) UpdateStatus = "Checking for updates…";
            var u = await Updater.CheckAsync();
            if (u == null)
            {
                UpdateStatus = manual ? $"You have the newest version ({AppVersion})." : "";
                if (manual) StatusMessage = UpdateStatus;
                return;
            }

            string? notes = null;
            try { notes = await Updater.GetReleaseNotesAsync(u.Version); } catch { }
            UpdateStatus = $"RoDeck {u.Version} is available.";
            _updateOffered = true;
            UpdateAvailable?.Invoke(u, notes, manual);
        }
        catch (Exception ex)
        {
            UpdateStatus = "Couldn't update: " + ex.Message;
            if (manual) StatusMessage = UpdateStatus;
            Log(ActivityLog.Error, "Update check failed: " + ex.Message);
        }
        finally { _updateBusy = false; }
    }

    /// <summary>On close: swaps in the update the person chose to download, so the restart runs the new version.</summary>
    private void InstallUpdateOnExit()
    {
        if (Updater.Staged is not { } u) return;
        try
        {
            Updater.Install();
            Log(ActivityLog.Info, $"Installed RoDeck {u.Version}.");
        }
        catch (Exception ex)
        {
            Log(ActivityLog.Error, "Couldn't install the update: " + ex.Message);
        }
    }
}
