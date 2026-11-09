using System.Collections.ObjectModel;
using RoDeck.Controls;
using RoDeck.Models;
using RoDeck.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RoDeck.ViewModels;

/// <summary>Home: online friends, Continue and Recommended games for the account top right.</summary>
public partial class MainViewModel
{
    public ObservableCollection<FriendTile> OnlineFriends { get; } = new();
    public ObservableCollection<GameTile> ContinueGames { get; } = new();
    public ObservableCollection<GameTile> RecommendedGames { get; } = new();
    /// <summary>Games starred in RoDeck, then the account's favourites on Roblox.</summary>
    public ObservableCollection<GameTile> FavoriteGames { get; } = new();

    public Loader FriendsLoad { get; } = new();
    public Loader GamesLoad { get; } = new();

    [ObservableProperty] private string friendsTitle = "Friends online";

    /// <summary>The big banner: the game you played last (or Roblox's top pick), with a wide picture of it.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasHeroGame))] private GameTile? heroGame;
    [ObservableProperty] private string heroImage = "";
    [ObservableProperty] private string heroCaption = "Jump back in";
    public bool HasHeroGame => HeroGame != null;

    /// <summary>"Good morning", "Good afternoon", "Good evening" or "Good night", by the PC's clock.</summary>
    public string Greeting => DateTime.Now.Hour switch
    {
        >= 5 and < 12 => "Good morning",
        >= 12 and < 18 => "Good afternoon",
        >= 18 and < 23 => "Good evening",
        _ => "Good night"
    };

    /// <summary>A game's first screenshot, wide (768 × 432), for the banner. "" when Roblox has none.</summary>
    private static async Task<string> GetWidePictureAsync(long universeId)
    {
        try
        {
            using var doc = await RobloxHttp.GetJsonAsync(
                $"https://thumbnails.roblox.com/v1/games/multiget/thumbnails?universeIds={universeId}&countPerUniverse=1&size=768x432&format=Png");
            foreach (var g in doc.RootElement.GetProperty("data").EnumerateArray())
                foreach (var t in g.GetProperty("thumbnails").EnumerateArray())
                    if (t.TryGetProperty("state", out var st) && st.GetString() == "Completed" && t.TryGetProperty("imageUrl", out var u))
                        return u.GetString() ?? "";
        }
        catch (RobloxException) { }
        catch (System.Text.Json.JsonException) { }
        catch (KeyNotFoundException) { }
        return "";
    }

    /// <summary>A stat card on Home: Friends, Requests, Trades or Multi.</summary>
    [RelayCommand]
    private void OpenHomeCard(string? where)
    {
        if (where == "Multi") GoMulti(RunningCount > 0 ? "Running" : "Launch");
        else if (where != null) OpenNotification(where);
    }

    private DateTime _homeLoadedAt = DateTime.MinValue;
    private Account? _homeLoadedFor;

    /// <summary>Loads Home when it's shown, the account changed, or the data is over 3 minutes old.</summary>
    private void MaybeLoadHome()
    {
        if (Page != "Home") return;
        if (_homeLoadedFor == CurrentAccount && DateTime.Now - _homeLoadedAt < TimeSpan.FromMinutes(3)) return;
        _ = LoadHomeAsync();
    }

    [RelayCommand]
    private Task RefreshHome() => LoadHomeAsync(force: true);

    private async Task LoadHomeAsync(bool force = false)
    {
        var a = CurrentAccount;
        _homeLoadedFor = a;
        _homeLoadedAt = DateTime.Now;
        OnlineFriends.Clear();
        ContinueGames.Clear();
        RecommendedGames.Clear();
        FavoriteGames.Clear();
        FriendsTitle = "Friends online";
        OnPropertyChanged(nameof(Greeting));
        HeroGame = null;
        HeroImage = "";
        _ = RefreshInboundCountAsync();   // the "trades waiting" card
        _ = RefreshRequestCountAsync();
        if (a == null || a.UserId == 0 || string.IsNullOrEmpty(a.Cookie))
        {
            FriendsLoad.State = LoadState.Empty;
            GamesLoad.State = LoadState.Empty;
            return;
        }

        var friends = FriendsLoad.RunAsync(async ct =>
        {
            var all = await GetFriendsAsync(a, force: force);   // shared with the Friends page and the server finder
            if (ct.IsCancellationRequested || a != CurrentAccount) return false;
            var online = FavouritesFirst(RobloxFriends.ByPresence(all.Where(f => f.IsOnline)));
            foreach (var f in online) OnlineFriends.Add(f);
            FriendsTitle = online.Count > 0 ? $"Friends online · {online.Count}" : "Friends online";
            _ = RobloxFriends.FillHeadshotsAsync(online, CancellationToken.None);
            return online.Count > 0;
        }, "Finding friends…");

        var games = GamesLoad.RunAsync(async ct =>
        {
            var (cont, rec) = await RobloxHome.GetHomeGamesAsync(a.Cookie, ct);
            if (ct.IsCancellationRequested || a != CurrentAccount) return false;
            foreach (var g in cont) ContinueGames.Add(g);
            foreach (var g in rec) RecommendedGames.Add(g);
            HeroGame = cont.FirstOrDefault() ?? rec.FirstOrDefault();
            HeroCaption = cont.Count > 0 ? "Jump back in" : "Recommended for you";
            if (HeroGame is { } hero)
                _ = Task.Run(async () =>
                {
                    var url = await GetWidePictureAsync(hero.UniverseId);
                    if (url.Length > 0)
                        System.Windows.Application.Current.Dispatcher.Invoke(() => { if (HeroGame == hero) HeroImage = url; });
                });
            return cont.Count + rec.Count > 0;
        }, "Loading games…");

        var favs = LoadHomeFavoritesAsync(a);

        await Task.WhenAll(friends, games, favs);
        if (FriendsLoad.State == LoadState.Error) Log(ActivityLog.Error, "Home friends: " + FriendsLoad.Message);
        if (GamesLoad.State == LoadState.Error) Log(ActivityLog.Error, "Home games: " + GamesLoad.Message);
    }

    private async Task LoadHomeFavoritesAsync(Account a)
    {
        var list = await FavouriteTilesAsync();
        try { await RobloxHome.FillMissingAsync(list, CancellationToken.None); }
        catch (RobloxException) { }
        catch (System.Text.Json.JsonException) { }
        if (a != CurrentAccount) return;
        FavoriteGames.Clear();
        foreach (var t in list.Where(t => t.PlaceId > 0)) FavoriteGames.Add(t);
    }

    /// <summary>Play a game with the account top right.</summary>
    [RelayCommand]
    private async Task PlayGameAsync(GameTile? g)
    {
        if (g == null || g.PlaceId == 0) return;
        if (CurrentAccount is not { } a) { StatusMessage = "Pick an account top right first."; return; }
        _placeNames[g.PlaceId] = g.Name;
        await LaunchAccountsAsync(new List<Account> { a }, $"https://www.roblox.com/games/{g.PlaceId}", "Home", launcher: true);
    }

    /// <summary>Open the game in Multi-launch → Launch, to pick several alts.</summary>
    [RelayCommand]
    private void PlayGameWithAlts(GameTile? g)
    {
        if (g == null || g.PlaceId == 0) return;
        _placeNames[g.PlaceId] = g.Name;
        TargetText = $"https://www.roblox.com/games/{g.PlaceId}";
        GoMulti("Launch");
        StatusMessage = $"{g.Name} is filled in. Pick your alts, then launch.";
    }

    /// <summary>Follow a friend into their server with the account top right.</summary>
    [RelayCommand]
    private async Task JoinFriendAsync(FriendTile? f)
    {
        if (f == null) return;
        if (!f.CanJoin)
        {
            StatusMessage = f.PresenceType == 2
                ? $"{f.Label} is in a game, but their privacy settings hide which one."
                : $"{f.Label} is {f.StatusText.ToLowerInvariant()}, not in a game you can join.";
            return;
        }
        if (CurrentAccount is not { } a) { StatusMessage = "Pick an account top right first."; return; }
        if (!string.IsNullOrEmpty(f.Location)) _placeNames[f.PlaceId] = f.Location;
        await LaunchAccountsAsync(new List<Account> { a }, FriendTarget(f), "join " + f.Label, launcher: true);
    }

    [RelayCommand]
    private void JoinFriendWithAlts(FriendTile? f)
    {
        if (f == null || !f.CanJoin) return;
        if (!string.IsNullOrEmpty(f.Location)) _placeNames[f.PlaceId] = f.Location;
        TargetText = FriendTarget(f);
        GoMulti("Launch");
        StatusMessage = $"{f.Label}'s server is filled in. Pick your alts, then launch.";
    }

    private static string FriendTarget(FriendTile f) =>
        string.IsNullOrEmpty(f.JobId)
            ? $"https://www.roblox.com/games/{f.PlaceId}"
            : $"https://www.roblox.com/games/{f.PlaceId}?gameInstanceId={f.JobId}";

    // ------------------------------------------------------------------ favourite friends

    /// <summary>Favourites first; the rest keep their order (in a game, Studio, online).</summary>
    private static List<FriendTile> FavouritesFirst(IEnumerable<FriendTile> list) =>
        list.Select((f, i) => (f, i)).OrderBy(x => x.f.IsFavourite ? 0 : 1).ThenBy(x => x.i).Select(x => x.f).ToList();

    [RelayCommand]
    private void ToggleFavouriteFriend(FriendTile? f)
    {
        if (f == null) return;
        bool fav = !f.IsFavourite;
        if (fav) { if (!Settings.FavouriteFriends.Contains(f.UserId)) Settings.FavouriteFriends.Add(f.UserId); }
        else Settings.FavouriteFriends.Remove(f.UserId);
        QueueSettingsSave();

        // the same friend can be a tile on Home and on Friends
        f.IsFavourite = fav;
        foreach (var t in OnlineFriends.Concat(AllFriends).Where(t => t.UserId == f.UserId)) t.IsFavourite = fav;
        Resort(OnlineFriends);
        if (_friendsAll.Count > 0)
        {
            foreach (var t in _friendsAll.Where(t => t.UserId == f.UserId)) t.IsFavourite = fav;
            _friendsAll = FavouritesFirst(_friendsAll);
            ShowFriendsPage();
        }
        if (OpenProfile?.UserId == f.UserId) OnPropertyChanged(nameof(ProfileIsFavourite));
        StatusMessage = fav ? $"{f.Label} is a favourite friend now." : $"{f.Label} is no longer a favourite.";
    }

    private static void Resort(ObservableCollection<FriendTile> list)
    {
        var sorted = FavouritesFirst(list);
        for (int i = 0; i < sorted.Count; i++)
        {
            int at = list.IndexOf(sorted[i]);
            if (at != i) list.Move(at, i);
        }
    }
}
