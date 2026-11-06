using System.Windows;
using System.Windows.Input;
using AltDeck.Services;

namespace AltDeck;

/// <summary>
/// "AltDeck 0.4.4 is out. Update now?" with the release notes. Update downloads with a progress bar and closes with
/// DialogResult true (the caller then restarts into the new version). Not now closes with false.
/// <see cref="DontAskAgain"/> is the checkbox.
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateInfo _update;
    private CancellationTokenSource? _download;

    public bool DontAskAgain => DontAsk.IsChecked == true;

    public UpdateWindow(UpdateInfo update, string? releaseNotes, bool askedByUser)
    {
        InitializeComponent();
        _update = update;
        var v = Short(update.Version);
        Heading.Text = $"AltDeck {v} is out";
        Sub.Text = $"You have {Short(Updater.Current)}. Update now? AltDeck restarts when it's done; your Roblox clients keep running.";
        Notes.Text = releaseNotes != null ? WhatsNewWindow.ToPlainText(releaseNotes) : "See GitHub for what changed in this version.";
        UpdateBtn.Content = $"Update to {v}";
        if (askedByUser) DontAsk.Visibility = Visibility.Collapsed;   // they asked: no "don't ask" here
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Later_Click(this, new RoutedEventArgs()); };
        Closing += (_, e) =>
        {
            if (_download != null && DialogResult != true) { _download.Cancel(); }   // closing mid-download cancels it
        };
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        if (_download != null) { _download.Cancel(); return; }   // "Cancel" while downloading
        DialogResult = false;
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        _download = new CancellationTokenSource();
        UpdateBtn.IsEnabled = false;
        DontAsk.IsEnabled = false;
        LaterBtn.Content = "Cancel";
        ProgressPanel.Visibility = Visibility.Visible;
        Small.Text = "Updating";
        Bar.Value = 0;
        PercentText.Text = "";
        ProgressText.Text = "Starting download…";

        var progress = new Progress<(long Done, long Total)>(p =>
        {
            if (p.Total > 0)
            {
                double pct = Math.Min(100, p.Done * 100.0 / p.Total);
                Bar.Value = pct;
                PercentText.Text = $"{pct:0}%";
                ProgressText.Text = $"Downloading… {Mb(p.Done)} of {Mb(p.Total)} MB";
            }
            else ProgressText.Text = $"Downloading… {Mb(p.Done)} MB";
        });

        try
        {
            await Updater.DownloadAsync(_update, ct: _download.Token, progress: progress);
            Bar.Value = 100;
            PercentText.Text = "100%";
            ProgressText.Text = "Checked. Restarting AltDeck…";
            LaterBtn.IsEnabled = false;
            await Task.Delay(600);   // let "Restarting" show for a moment
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            ResetButtons();
            ProgressPanel.Visibility = Visibility.Collapsed;
            Small.Text = "Update available";
        }
        catch (Exception ex)
        {
            ResetButtons();
            UpdateBtn.Content = "Try again";
            ProgressText.Text = "The download didn't work: " + ex.Message;
            PercentText.Text = "";
        }
    }

    private void ResetButtons()
    {
        _download = null;
        UpdateBtn.IsEnabled = true;
        DontAsk.IsEnabled = true;
        LaterBtn.Content = "Not now";
    }

    static string Short(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
    static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0");
}
