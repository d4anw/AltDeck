using System.Collections.ObjectModel;
using AltDeck.Controls;
using AltDeck.Models;
using AltDeck.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AltDeck.ViewModels;

/// <summary>Home: online friends, Continue and Recommended games for the account top right.</summary>
public partial class MainViewModel
{
    public ObservableCollection<FriendTile> OnlineFriends { get; } = new();
    public ObservableCollection<GameTile> ContinueGames { get; } = new();
    public ObservableCollection<GameTile> RecommendedGames { get; } = new();

    public Loader FriendsLoad { get; } = new();
    public Loader GamesLoad { get; } = new();

    [ObservableProperty] private string friendsTitle = "Friends online";

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
    private Task RefreshHome() => LoadHomeAsync();

    private async Task LoadHomeAsync()
    {
        var a = CurrentAccount;
        _homeLoadedFor = a;
        _homeLoadedAt = DateTime.Now;
        OnlineFriends.Clear();
        ContinueGames.Clear();
        RecommendedGames.Clear();
        FriendsTitle = "Friends online";
        if (a == null || a.UserId == 0 || string.IsNullOrEmpty(a.Cookie))
        {
            FriendsLoad.State = LoadState.Empty;
            GamesLoad.State = LoadState.Empty;
            return;
        }

        var friends = FriendsLoad.RunAsync(async ct =>
        {
            var list = await RobloxHome.GetOnlineFriendsAsync(a.Cookie, a.UserId, ct);
            if (ct.IsCancellationRequested || a != CurrentAccount) return false;
            foreach (var f in list) f.IsFavourite = Settings.FavouriteFriends.Contains(f.UserId);
            foreach (var f in FavouritesFirst(list)) OnlineFriends.Add(f);
            FriendsTitle = list.Count > 0 ? $"Friends online · {list.Count}" : "Friends online";
            return list.Count > 0;
        }, "Finding friends…");

        var games = GamesLoad.RunAsync(async ct =>
        {
            var (cont, rec) = await RobloxHome.GetHomeGamesAsync(a.Cookie, ct);
            if (ct.IsCancellationRequested || a != CurrentAccount) return false;
            foreach (var g in cont) ContinueGames.Add(g);
            foreach (var g in rec) RecommendedGames.Add(g);
            return cont.Count + rec.Count > 0;
        }, "Loading games…");

        await Task.WhenAll(friends, games);
        if (FriendsLoad.State == LoadState.Error) Log(ActivityLog.Error, "Home friends: " + FriendsLoad.Message);
        if (GamesLoad.State == LoadState.Error) Log(ActivityLog.Error, "Home games: " + GamesLoad.Message);
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
        Resort(AllFriends);
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
