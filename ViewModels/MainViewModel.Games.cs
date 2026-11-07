using System.Collections.ObjectModel;
using System.Diagnostics;
using AltDeck.Controls;
using AltDeck.Models;
using AltDeck.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AltDeck.ViewModels;

/// <summary>Games page: charts, search, favourites, recently played, the game page and its server finder.</summary>
public partial class MainViewModel
{
    // ================================================================== list (charts / search / favourites / recent)

    public ObservableCollection<GameSort> GameCharts { get; } = new();
    public ObservableCollection<ChartChip> ChartChips { get; } = new();

    partial void OnGamesViewChanged(string value)
    {
        foreach (var c in ChartChips) c.IsSelected = c.Id == value;
    }

    private void RebuildChips()
    {
        ChartChips.Clear();
        foreach (var s in GameCharts) ChartChips.Add(new ChartChip { Id = s.Id, Name = s.Name });
        ChartChips.Add(new ChartChip { Id = "favourites", Name = "My favourites", Icon = "\uE735" });
        ChartChips.Add(new ChartChip { Id = "recent", Name = "Recently played", Icon = "\uE81C" });
        foreach (var c in ChartChips) c.IsSelected = c.Id == GamesView;
    }
    public ObservableCollection<GameTile> ShownGames { get; } = new();
    public Loader ChartsLoad { get; } = new();

    /// <summary>Which list is shown: a chart's sort ID, "favourites", "recent" or "search".</summary>
    [ObservableProperty] private string gamesView = "";
    [ObservableProperty] private string gamesTitle = "Games";
    [ObservableProperty] private string gamesInfo = "";
    [ObservableProperty] private string gameSearchText = "";

    private DateTime _chartsLoadedAt = DateTime.MinValue;

    private void MaybeLoadGames()
    {
        if (Page != "Games") return;
        if (GameCharts.Count > 0 && DateTime.Now - _chartsLoadedAt < TimeSpan.FromMinutes(10)) return;
        _ = LoadChartsAsync();
    }

    /// <summary>The "Try again" on the game page.</summary>
    [RelayCommand]
    private Task RetryOpenGame() => _lastOpened != null ? OpenGameAsync(_lastOpened) : Task.CompletedTask;
    private GameTile? _lastOpened;

    [RelayCommand]
    private Task RefreshGames() => GamesView == "search" ? SearchGamesAsync() : LoadChartsAsync();

    private async Task LoadChartsAsync()
    {
        if (ChartChips.Count == 0) RebuildChips();   // favourites and recent work even if the charts don't load
        await ChartsLoad.RunAsync(async ct =>
        {
            var sorts = await RobloxGames.GetSortsAsync(ct);
            GameCharts.Clear();
            foreach (var s in sorts) GameCharts.Add(s);
            RebuildChips();
            _chartsLoadedAt = DateTime.Now;
            var keep = GameCharts.FirstOrDefault(s => s.Id == GamesView) ?? GameCharts.FirstOrDefault();
            if (GamesView is "favourites" or "recent" or "search") { ShowSaved(GamesView); return true; }
            if (keep != null) await ShowChartAsync(keep);
            return GameCharts.Count > 0;
        }, "Loading charts…");
    }

    [RelayCommand]
    private async Task SelectChart(string? id)
    {
        if (id is "favourites" or "recent") { ShowSaved(id); return; }
        var sort = GameCharts.FirstOrDefault(s => s.Id == id);
        if (sort != null) await ShowChartAsync(sort);
    }

    private async Task ShowChartAsync(GameSort sort)
    {
        GamesView = sort.Id;
        GamesTitle = sort.Name;
        GamesInfo = sort.Info;
        await ShowTilesAsync(sort.Games);
    }

    private void ShowSaved(string which)
    {
        GamesView = which;
        var list = which == "favourites" ? Settings.FavouriteGames : Settings.RecentGames;
        GamesTitle = which == "favourites" ? "My favourites" : "Recently played";
        GamesInfo = which == "favourites"
            ? (list.Count == 0 ? "Star a game on its page to keep it here." : "Games you starred. Saved on this PC.")
            : (list.Count == 0 ? "Games you launch from AltDeck show up here." : "The games you launched from AltDeck, newest first.");
        _ = ShowTilesAsync(list.Select(s => s.Tile).ToList(), fillDetails: true);
    }

    [RelayCommand]
    private Task SearchGamesAsync()
    {
        var q = (GameSearchText ?? "").Trim();
        if (q.Length == 0) return LoadChartsAsync();
        return ChartsLoad.RunAsync(async ct =>
        {
            var found = await RobloxGames.SearchAsync(q, ct);
            GamesView = "search";
            GamesTitle = $"Results for “{q}”";
            GamesInfo = found.Count == 0 ? "" : $"{found.Count} games";
            await ShowTilesAsync(found);
            return found.Count > 0;
        }, "Searching…");
    }

    private async Task ShowTilesAsync(List<GameTile> tiles, bool fillDetails = false)
    {
        ShownGames.Clear();
        if (fillDetails) { try { await RobloxHome.FillMissingAsync(tiles, CancellationToken.None); } catch { } }
        foreach (var t in tiles) ShownGames.Add(t);
        // icons after the tiles are on screen (they show a placeholder until then)
        try
        {
            var icons = await Thumbnails.GetAsync("GameIcon", tiles.Where(t => t.IconUrl.Length == 0).Select(t => t.UniverseId), "256x256");
            foreach (var t in tiles) if (icons.TryGetValue(t.UniverseId, out var url)) t.IconUrl = url;
        }
        catch { }
    }

    // ================================================================== the game page

    [ObservableProperty] private GameDetails? openGame;

    /// <summary>The game page is showing (also while it loads or shows an error).</summary>
    [ObservableProperty] private bool isGameOpen;
    public Loader GameLoad { get; } = new();

    [ObservableProperty] private string selectedMedia = "";
    [ObservableProperty] private bool openGameIsFavourite;

    public string OpenGameStats => OpenGame is not { } g ? "" :
        $"{Count(g.Playing)} playing  ·  {Count(g.Visits)} visits  ·  {Count(g.Favorites)} favourites"
        + (g.UpVotes + g.DownVotes > 0 ? $"  ·  {Math.Round(g.UpVotes * 100.0 / (g.UpVotes + g.DownVotes))}% likes" : "")
        + (g.MaxPlayers > 0 ? $"  ·  {g.MaxPlayers} per server" : "");

    public string OpenGameMeta => OpenGame is not { } g ? "" :
        string.Join("  ·  ", new[]
        {
            g.Creator.Length > 0 ? "By " + g.Creator + (g.CreatorVerified ? " ✓" : "") : "",
            g.Genre,
            g.Updated is DateTime u ? "Updated " + u.ToString("d MMM yyyy") : ""
        }.Where(x => x.Length > 0));

    /// <summary>Opens a game's page (from Games, Home or a search).</summary>
    [RelayCommand]
    private async Task OpenGameAsync(GameTile? tile)
    {
        if (tile == null || tile.UniverseId == 0) return;
        _lastOpened = tile;
        if (Page != "Games") Page = "Games";
        OpenGame = null;
        Servers.Clear();
        _allServers = new();
        ServersInfo = "";
        SelectedMedia = "";
        IsGameOpen = true;
        await GameLoad.RunAsync(async ct =>
        {
            var d = await RobloxGames.GetDetailsAsync(tile.UniverseId, ct);
            OpenGame = d;
            SelectedMedia = d.Media.FirstOrDefault() ?? "";
            OpenGameIsFavourite = Settings.FavouriteGames.Any(f => f.UniverseId == d.UniverseId);
            OnPropertyChanged(nameof(OpenGameStats));
            OnPropertyChanged(nameof(OpenGameMeta));
            return true;
        }, "Opening game…");
        if (OpenGame != null) _ = LoadServersAsync();
    }

    [RelayCommand]
    private void CloseGame()
    {
        GameLoad.Cancel();
        GameLoad.State = LoadState.Ready;
        ServersLoad.Cancel();
        OpenGame = null;
        Servers.Clear();
        IsGameOpen = false;
    }

    [RelayCommand]
    private void ShowMedia(string? url) { if (!string.IsNullOrEmpty(url)) SelectedMedia = url; }

    [RelayCommand]
    private void ToggleFavouriteGame()
    {
        if (OpenGame is not { } g) return;
        var existing = Settings.FavouriteGames.FirstOrDefault(f => f.UniverseId == g.UniverseId);
        if (existing != null) Settings.FavouriteGames.Remove(existing);
        else Settings.FavouriteGames.Insert(0, new SavedGame { UniverseId = g.UniverseId, PlaceId = g.PlaceId, Name = g.Name });
        OpenGameIsFavourite = existing == null;
        StatusMessage = existing == null ? $"Added {g.Name} to your favourites." : $"Removed {g.Name} from your favourites.";
        QueueSettingsSave();
        if (GamesView == "favourites") ShowSaved("favourites");
    }

    [RelayCommand]
    private Task PlayOpenGame() => OpenGame is { } g
        ? PlayGameAsync(new GameTile { UniverseId = g.UniverseId, PlaceId = g.PlaceId, Name = g.Name })
        : Task.CompletedTask;

    [RelayCommand]
    private void PlayOpenGameWithAlts()
    {
        if (OpenGame is { } g) PlayGameWithAlts(new GameTile { UniverseId = g.UniverseId, PlaceId = g.PlaceId, Name = g.Name });
    }

    [RelayCommand]
    private void OpenGameOnWeb(object? arg)
    {
        long place = arg switch { GameTile t => t.PlaceId, _ => OpenGame?.PlaceId ?? 0 };
        if (place == 0) return;
        try { Process.Start(new ProcessStartInfo($"https://www.roblox.com/games/{place}") { UseShellExecute = true }); } catch { }
    }

    /// <summary>Called after every launch: remembers the game for "Recently played".</summary>
    private async void RememberRecent(long placeId)
    {
        if (placeId <= 0) return;
        try
        {
            var existing = Settings.RecentGames.FirstOrDefault(r => r.PlaceId == placeId);
            long universe = existing?.UniverseId ?? 0;
            string name = existing?.Name ?? (_placeNames.TryGetValue(placeId, out var n) ? n : "");
            if (universe == 0) universe = await RobloxGames.GetUniverseIdAsync(placeId, CancellationToken.None);
            if (universe == 0) return;
            Settings.RecentGames.RemoveAll(r => r.PlaceId == placeId || r.UniverseId == universe);
            Settings.RecentGames.Insert(0, new SavedGame { UniverseId = universe, PlaceId = placeId, Name = name, When = DateTime.Now });
            if (Settings.RecentGames.Count > 30) Settings.RecentGames.RemoveRange(30, Settings.RecentGames.Count - 30);
            QueueSettingsSave();
        }
        catch { }   // "recently played" is a nicety: never bother the launch with it
    }

    // ================================================================== server finder

    public ObservableCollection<ServerEntry> Servers { get; } = new();
    public Loader ServersLoad { get; } = new();
    private List<ServerEntry> _allServers = new();

    /// <summary>"Ping", "Fewest", "Most" or "Friends".</summary>
    [ObservableProperty] private string serverSort = "Ping";
    [ObservableProperty] private string serversInfo = "";

    partial void OnServerSortChanged(string value)
    {
        // Roblox lists servers by player count; "fewest" needs the other end of that list
        bool needAsc = value == "Fewest";
        if (needAsc != _serversAsc && OpenGame != null) _ = LoadServersAsync();
        else ApplyServerSort();
    }

    private bool _serversAsc;

    [RelayCommand]
    private Task RefreshServers() => LoadServersAsync();

    private async Task LoadServersAsync()
    {
        if (OpenGame is not { } g) return;
        _serversAsc = ServerSort == "Fewest";
        Servers.Clear();
        await ServersLoad.RunAsync(async ct =>
        {
            var progress = new Progress<int>(n => ServersLoad.Message = $"Finding servers… {n}");
            var list = await RobloxGames.GetServersAsync(g.PlaceId, _serversAsc, maxPages: 5, progress, ct);
            await MarkFriendsAsync(list, g, ct);
            _allServers = list;
            ApplyServerSort();
            return list.Count > 0;
        }, "Finding servers…");
    }

    private async Task MarkFriendsAsync(List<ServerEntry> list, GameDetails g, CancellationToken ct)
    {
        if (CurrentAccount is not { } a || string.IsNullOrEmpty(a.Cookie)) return;
        List<FriendTile> friends;
        try
        {
            friends = _homeLoadedFor == a && OnlineFriends.Count > 0
                ? OnlineFriends.ToList()
                : await RobloxHome.GetOnlineFriendsAsync(a.Cookie, a.UserId, ct);
        }
        catch { return; }   // the list still works without friend badges
        foreach (var grp in friends.Where(f => f.JobId != null && f.PlaceId == g.PlaceId).GroupBy(f => f.JobId!))
        {
            var names = string.Join(", ", grp.Select(f => f.Label));
            var s = list.FirstOrDefault(x => x.JobId == grp.Key);
            if (s != null) s.Friends = names;
        }
    }

    private void ApplyServerSort()
    {
        IEnumerable<ServerEntry> sorted = ServerSort switch
        {
            "Fewest" => _allServers.OrderBy(s => s.Playing),
            "Most" => _allServers.OrderByDescending(s => s.Playing),
            "Friends" => _allServers.OrderByDescending(s => s.HasFriends).ThenBy(s => s.Ping ?? double.MaxValue),
            _ => _allServers.OrderBy(s => s.Ping ?? double.MaxValue).ThenByDescending(s => s.Playing)
        };
        Servers.Clear();
        foreach (var s in sorted.Take(150)) Servers.Add(s);
        int withFriends = _allServers.Count(s => s.HasFriends);
        ServersInfo = _allServers.Count == 0 ? ""
            : $"{_allServers.Count} servers with room" + (withFriends > 0 ? $" · friends in {withFriends}" : "")
              + (_allServers.Count > Servers.Count ? $" · showing the top {Servers.Count}" : "");
    }

    /// <summary>The lowest-ping server with room, joined with the account top right.</summary>
    [RelayCommand]
    private async Task JoinBestServerAsync()
    {
        var best = _allServers.Where(s => s.FreeSlots > 0).OrderBy(s => s.Ping ?? double.MaxValue).ThenByDescending(s => s.Playing).FirstOrDefault();
        if (best == null) { StatusMessage = "No servers with room found yet. Press Refresh."; return; }
        await JoinServerAsync(best);
    }

    [RelayCommand]
    private async Task JoinServerAsync(ServerEntry? s)
    {
        if (s == null || OpenGame is not { } g) return;
        if (CurrentAccount is not { } a) { StatusMessage = "Pick an account top right first."; return; }
        _placeNames[g.PlaceId] = g.Name;
        await LaunchAccountsAsync(new List<Account> { a }, ServerLink(g, s), "server finder", showRunning: false);
    }

    [RelayCommand]
    private void JoinServerWithAlts(ServerEntry? s)
    {
        if (OpenGame is not { } g) return;
        s ??= _allServers.Where(x => x.FreeSlots > 0).OrderBy(x => x.Ping ?? double.MaxValue).FirstOrDefault();
        if (s == null) return;
        _placeNames[g.PlaceId] = g.Name;
        TargetText = ServerLink(g, s);
        GoMulti("Launch");
        StatusMessage = $"That {g.Name} server is filled in ({s.FreeSlots} free slots, {s.PingText}). Pick your alts, then launch.";
    }

    private static string ServerLink(GameDetails g, ServerEntry s) => $"https://www.roblox.com/games/{g.PlaceId}?gameInstanceId={s.JobId}";

    private static string Count(long n) => n switch
    {
        >= 1_000_000_000 => $"{n / 1_000_000_000.0:0.#}B",
        >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
        >= 1_000 => $"{n / 1_000.0:0.#}K",
        _ => n.ToString()
    };
}
