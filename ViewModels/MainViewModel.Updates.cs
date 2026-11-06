using System.Windows.Threading;
using AltDeck.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AltDeck.ViewModels;

/// <summary>Auto-update: checks GitHub shortly after start and every 6 hours, downloads in the background, installs on close.</summary>
public partial class MainViewModel
{
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private bool _updateBusy;

    private const string UpdateBaseText =
        "Downloads new versions of AltDeck from GitHub by itself and installs them when AltDeck restarts.";
    [ObservableProperty] private string updateStatus = "";
    public string UpdateDescription => string.IsNullOrEmpty(UpdateStatus) ? UpdateBaseText : UpdateBaseText + "\n" + UpdateStatus;
    partial void OnUpdateStatusChanged(string value) => OnPropertyChanged(nameof(UpdateDescription));

    public string AboutTitle => "AltDeck " + AppVersion;

    /// <summary>A checked download is ready. The window asks whether to restart now.</summary>
    public event Action<UpdateInfo>? UpdateReady;

    /// <summary>Called once from the constructor.</summary>
    private void SetupUpdates()
    {
        Updater.CleanUp();
        if (!Updater.CanUpdate) UpdateStatus = "This copy isn't the AltDeck.exe from GitHub Releases, so it doesn't update itself.";

        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };   // let start-up and the sign-in checks go first
        first.Tick += async (_, _) => { first.Stop(); await CheckForUpdateAsync(manual: false); };
        first.Start();
        _updateTimer.Tick += async (_, _) => await CheckForUpdateAsync(manual: false);
        _updateTimer.Start();
    }

    [RelayCommand]
    private Task CheckForUpdates() => CheckForUpdateAsync(manual: true);

    private async Task CheckForUpdateAsync(bool manual)
    {
        if (_updateBusy) return;
        if (!manual && !Settings.AutoUpdate) return;
        if (!Updater.CanUpdate)
        {
            if (manual) StatusMessage = "Only the AltDeck.exe from GitHub Releases updates itself.";
            return;
        }
        if (Updater.Staged is { } ready)
        {
            UpdateReady?.Invoke(ready);
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

            UpdateStatus = $"Downloading AltDeck {u.Version}…";
            await Updater.DownloadAsync(u);
            Log(ActivityLog.Info, $"AltDeck {u.Version} downloaded. It installs when AltDeck restarts.");
            UpdateStatus = $"AltDeck {u.Version} is ready. It installs when AltDeck restarts.";
            UpdateReady?.Invoke(u);
        }
        catch (Exception ex)
        {
            UpdateStatus = "Couldn't update: " + ex.Message;
            if (manual) StatusMessage = UpdateStatus;
            Log(ActivityLog.Error, "Update check failed: " + ex.Message);
        }
        finally { _updateBusy = false; }
    }

    /// <summary>On close: swaps in a downloaded update so the next start runs the new version.</summary>
    private void InstallUpdateOnExit()
    {
        if (Updater.Staged is not { } u) return;
        try
        {
            Updater.Install();
            Log(ActivityLog.Info, $"Installed AltDeck {u.Version}.");
        }
        catch (Exception ex)
        {
            Log(ActivityLog.Error, "Couldn't install the update: " + ex.Message);
        }
    }
}
