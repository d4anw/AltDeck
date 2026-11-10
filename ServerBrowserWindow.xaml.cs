using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RoDeck.Services;

namespace RoDeck;

/// <summary>One line in the server list.</summary>
public sealed class ServerRow
{
    public string Id { get; }
    public string PlayersText { get; }
    public string PingText { get; }
    public string FpsText { get; }
    public string ShortId { get; }

    public ServerRow(RobloxApi.ServerInfo s)
    {
        Id = s.Id;
        PlayersText = s.MaxPlayers > 0 ? $"{s.Playing} / {s.MaxPlayers}" : s.Playing.ToString();
        PingText = s.Ping is double p ? $"{p:0} ms" : "ping ?";
        FpsText = s.Fps is double f ? $"{f:0} fps" : "";
        ShortId = s.Id.Length > 13 ? s.Id.Substring(0, 8) + "…" + s.Id.Substring(s.Id.Length - 4) : s.Id;
    }
}

/// <summary>Lists a game's open public servers; the chosen server's job ID is returned in <see cref="SelectedJobId"/>.</summary>
public partial class ServerBrowserWindow : Window
{
    private readonly long _placeId;
    private int _loading;
    private ObservableCollection<ServerRow> Rows { get; } = new();

    public string? SelectedJobId { get; private set; }

    public ServerBrowserWindow(long placeId, string gameName)
    {
        InitializeComponent();
        _placeId = placeId;
        Subtitle.Text = (string.IsNullOrEmpty(gameName) ? "" : gameName + " · ") + "place " + placeId;
        ServerList.ItemsSource = Rows;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (Interlocked.Exchange(ref _loading, 1) == 1) return;
        try
        {
            StatusText.Text = "Loading servers…";
            UseBtn.IsEnabled = false;
            Rows.Clear();

            int sort = SortBox.SelectedIndex;
            var (servers, error) = await RobloxApi.GetPublicServersAsync(_placeId, fewestPlayersFirst: sort == 0);
            IEnumerable<RobloxApi.ServerInfo> ordered = sort == 2 ? servers.OrderBy(s => s.Ping ?? double.MaxValue) : servers;
            foreach (var s in ordered) Rows.Add(new ServerRow(s));

            StatusText.Text = error ?? (Rows.Count == 0 ? "No open public servers right now." : $"{Rows.Count} open servers");
        }
        finally { Interlocked.Exchange(ref _loading, 0); }
    }

    private async void Sort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        await LoadAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => UseBtn.IsEnabled = ServerList.SelectedItem is ServerRow;

    private void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ServerList.SelectedItem is ServerRow) Use_Click(sender, e);
    }

    /// <summary>The Select button on a row: that server.</summary>
    private void Select_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ServerRow row) return;
        SelectedJobId = row.Id;
        DialogResult = true;
    }

    private void Use_Click(object sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not ServerRow row) return;
        SelectedJobId = row.Id;
        DialogResult = true;
    }
}
