using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using AltDeck.Controls;
using AltDeck.Models;
using AltDeck.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AltDeck.ViewModels;

/// <summary>Friends page: the friend list, friend requests, player search and profiles, as the account top right.</summary>
public partial class MainViewModel
{
    /// <summary>"Friends", "Requests" or "Find".</summary>
    [ObservableProperty] private string friendsTab = "Friends";

    // ================================================================== friend list

    public ObservableCollection<FriendTile> AllFriends { get; } = new();
    public Loader AllFriendsLoad { get; } = new();
    private ICollectionView? _friendsView;
    public ICollectionView FriendsView => _friendsView ??= MakeFriendsView();

    [ObservableProperty] private string friendsFilter = "";
    [ObservableProperty] private string friendsSummary = "";
    partial void OnFriendsFilterChanged(string value) => FriendsView.Refresh();

    private ICollectionView MakeFriendsView()
    {
        var v = new CollectionViewSource { Source = AllFriends }.View;
        v.Filter = o => o is FriendTile f && (FriendsFilter.Trim().Length == 0
            || f.Label.Contains(FriendsFilter.Trim(), StringComparison.OrdinalIgnoreCase)
            || f.Name.Contains(FriendsFilter.Trim().TrimStart('@'), StringComparison.OrdinalIgnoreCase));
        return v;
    }

    private DateTime _friendsLoadedAt = DateTime.MinValue;
    private Account? _friendsLoadedFor;

    private void MaybeLoadFriends()
    {
        if (Page != "Friends") return;
        if (_friendsLoadedFor == CurrentAccount && DateTime.Now - _friendsLoadedAt < TimeSpan.FromMinutes(2)) return;
        _ = LoadFriendsPageAsync();
    }

    [RelayCommand]
    private Task RefreshFriends() => LoadFriendsPageAsync();

    private async Task LoadFriendsPageAsync()
    {
        var a = CurrentAccount;
        _friendsLoadedFor = a;
        _friendsLoadedAt = DateTime.Now;
        AllFriends.Clear();
        Requests.Clear();
        FriendsSummary = "";
        if (a == null || string.IsNullOrEmpty(a.Cookie))
        {
            AllFriendsLoad.State = LoadState.Empty;
            RequestsLoad.State = LoadState.Empty;
            return;
        }

        var friends = AllFriendsLoad.RunAsync(async ct =>
        {
            var list = await RobloxFriends.GetAllFriendsAsync(a.Cookie, a.UserId, ct);
            if (ct.IsCancellationRequested || a != CurrentAccount) return false;
            foreach (var f in list) f.IsFavourite = Settings.FavouriteFriends.Contains(f.UserId);
            foreach (var f in FavouritesFirst(list)) AllFriends.Add(f);
            int online = list.Count(f => f.IsOnline), inGame = list.Count(f => f.PresenceType == 2);
            FriendsSummary = $"{list.Count} friend{(list.Count == 1 ? "" : "s")} · {online} online" + (inGame > 0 ? $" · {inGame} in a game" : "");
            return list.Count > 0;
        }, "Loading friends…");

        var requests = LoadRequestsAsync(a);
        await Task.WhenAll(friends, requests);
        if (AllFriendsLoad.State == LoadState.Error) Log(ActivityLog.Error, "Friends: " + AllFriendsLoad.Message);
    }

    // ================================================================== requests

    public ObservableCollection<FriendRequest> Requests { get; } = new();
    public Loader RequestsLoad { get; } = new();

    /// <summary>Incoming friend requests of the account top right (the badge on Friends).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasRequests))]
    private int requestCount;
    public bool HasRequests => RequestCount > 0;

    private Task LoadRequestsAsync(Account a) => RequestsLoad.RunAsync(async ct =>
    {
        var list = await RobloxFriends.GetRequestsAsync(a.Cookie, ct);
        if (ct.IsCancellationRequested || a != CurrentAccount) return false;
        Requests.Clear();
        foreach (var r in list) Requests.Add(r);
        RequestCount = list.Count;
        return list.Count > 0;
    }, "Loading requests…");

    /// <summary>Just the number, for the sidebar badge (on start and when switching accounts).</summary>
    private async Task RefreshRequestCountAsync()
    {
        if (CurrentAccount is not { } a || string.IsNullOrEmpty(a.Cookie)) { RequestCount = 0; return; }
        try
        {
            var n = await RobloxFriends.GetRequestCountAsync(a.Cookie, CancellationToken.None);
            if (a == CurrentAccount) RequestCount = n;
        }
        catch { }
    }

    [RelayCommand]
    private async Task AcceptRequestAsync(FriendRequest? r)
    {
        if (r == null || CurrentAccount is not { } a) return;
        try
        {
            await RobloxFriends.AcceptAsync(a.Cookie, r.UserId);
            Requests.Remove(r);
            RequestCount = Requests.Count;
            StatusMessage = $"{a.Label} and {r.Label} are friends now.";
            Log(ActivityLog.Info, $"{a.Label} accepted {r.Label}'s friend request.");
            _friendsLoadedAt = DateTime.MinValue;   // the list reloads next time
        }
        catch (Exception ex) { StatusMessage = "Couldn't accept: " + ex.Message; }
    }

    [RelayCommand]
    private async Task DeclineRequestAsync(FriendRequest? r)
    {
        if (r == null || CurrentAccount is not { } a) return;
        try
        {
            await RobloxFriends.DeclineAsync(a.Cookie, r.UserId);
            Requests.Remove(r);
            RequestCount = Requests.Count;
            StatusMessage = $"Declined {r.Label}'s friend request.";
        }
        catch (Exception ex) { StatusMessage = "Couldn't decline: " + ex.Message; }
    }

    // ================================================================== find players

    public ObservableCollection<PlayerHit> PlayerHits { get; } = new();
    public Loader PlayersLoad { get; } = new();
    [ObservableProperty] private string playerSearchText = "";

    [RelayCommand]
    private Task SearchPlayersAsync()
    {
        var q = (PlayerSearchText ?? "").Trim();
        if (q.Length == 0) return Task.CompletedTask;
        return PlayersLoad.RunAsync(async ct =>
        {
            var hits = await RobloxFriends.SearchPlayersAsync(q, CurrentAccount?.Cookie, ct);
            PlayerHits.Clear();
            foreach (var h in hits) PlayerHits.Add(h);
            return hits.Count > 0;
        }, "Searching…");
    }

    // ================================================================== a profile

    [ObservableProperty] private PlayerProfile? openProfile;
    [ObservableProperty] private bool isProfileOpen;
    public Loader ProfileLoad { get; } = new();
    private long _lastProfileId;

    public string ProfileCounts => OpenProfile is not { } p ? "" :
        $"{p.Friends:N0} friends  ·  {p.Followers:N0} followers  ·  {p.Following:N0} following";
    public string ProfileJoined => OpenProfile?.Joined is DateTime d ? "Joined " + d.ToString("d MMMM yyyy") : "";
    public string ProfileStatus => OpenProfile is not { } p ? "" : p.PresenceType switch
    {
        2 => string.IsNullOrEmpty(p.Location) ? "In a game" : "In " + p.Location,
        3 => "In Roblox Studio",
        1 => "Online",
        _ => p.Banned ? "Banned" : "Offline"
    };
    public string ProfileStatusKind => OpenProfile?.PresenceType switch { 2 => "ingame", 1 or 3 => "online", _ => "offline" };
    public bool ProfileCanJoin => OpenProfile is { PresenceType: 2, PlaceId: > 0 };
    public bool ProfileIsMe => OpenProfile != null && CurrentAccount?.UserId == OpenProfile.UserId;
    public bool ProfileIsFavourite => OpenProfile != null && Settings.FavouriteFriends.Contains(OpenProfile.UserId);

    /// <summary>The main friendship button: what it says and does depends on the friendship with the account top right.</summary>
    public string ProfileFriendButton => OpenProfile?.Friendship switch
    {
        "Friends" => "Friends ✓",
        "RequestSent" => "Request sent",
        "RequestReceived" => "Accept request",
        "NotFriends" => "Add friend",
        _ => ""
    };

    [RelayCommand]
    private async Task OpenProfileAsync(object? who)
    {
        long id = who switch
        {
            long l => l,
            FriendTile f => f.UserId,
            FriendRequest r => r.UserId,
            PlayerHit h => h.UserId,
            _ => 0
        };
        if (id <= 0) return;
        _lastProfileId = id;
        OpenProfile = null;
        IsProfileOpen = true;
        if (Page != "Friends") Page = "Friends";
        var a = CurrentAccount;
        await ProfileLoad.RunAsync(async ct =>
        {
            var p = await RobloxFriends.GetProfileAsync(id, a?.Cookie, a?.UserId ?? 0, ct);
            OpenProfile = p;
            NotifyProfile();
            return true;
        }, "Opening profile…");
    }

    private void NotifyProfile()
    {
        foreach (var n in new[] { nameof(ProfileCounts), nameof(ProfileJoined), nameof(ProfileStatus), nameof(ProfileStatusKind),
                                  nameof(ProfileCanJoin), nameof(ProfileIsMe), nameof(ProfileIsFavourite), nameof(ProfileFriendButton) })
            OnPropertyChanged(n);
    }

    [RelayCommand]
    private Task RetryOpenProfile() => _lastProfileId > 0 ? OpenProfileAsync(_lastProfileId) : Task.CompletedTask;

    [RelayCommand]
    private void CloseProfile()
    {
        ProfileLoad.Cancel();
        ProfileLoad.State = LoadState.Ready;
        OpenProfile = null;
        IsProfileOpen = false;
    }

    /// <summary>Add friend / Accept request. (Unfriending is asked for in a menu first, see the page.)</summary>
    [RelayCommand]
    private async Task ProfileFriendActionAsync()
    {
        if (OpenProfile is not { } p || CurrentAccount is not { } a) return;
        try
        {
            switch (p.Friendship)
            {
                case "NotFriends":
                    await RobloxFriends.SendRequestAsync(a.Cookie, p.UserId);
                    p.Friendship = "RequestSent";
                    StatusMessage = $"Sent a friend request to {p.Label} from {a.Label}.";
                    Log(ActivityLog.Info, $"{a.Label} sent a friend request to {p.Label}.");
                    break;
                case "RequestReceived":
                    await RobloxFriends.AcceptAsync(a.Cookie, p.UserId);
                    p.Friendship = "Friends";
                    var r = Requests.FirstOrDefault(x => x.UserId == p.UserId);
                    if (r != null) Requests.Remove(r);
                    RequestCount = Math.Max(0, RequestCount - 1);
                    StatusMessage = $"{a.Label} and {p.Label} are friends now.";
                    _friendsLoadedAt = DateTime.MinValue;
                    break;
                default:
                    return;
            }
            NotifyProfile();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    /// <summary>Called from the page after the person confirmed in the menu.</summary>
    public async Task UnfriendAsync(long userId, string name)
    {
        if (CurrentAccount is not { } a) return;
        try
        {
            await RobloxFriends.UnfriendAsync(a.Cookie, userId);
            var f = AllFriends.FirstOrDefault(x => x.UserId == userId);
            if (f != null) AllFriends.Remove(f);
            var o = OnlineFriends.FirstOrDefault(x => x.UserId == userId);
            if (o != null) OnlineFriends.Remove(o);
            if (OpenProfile?.UserId == userId) { OpenProfile.Friendship = "NotFriends"; NotifyProfile(); }
            StatusMessage = $"{a.Label} and {name} are no longer friends.";
            Log(ActivityLog.Info, $"{a.Label} unfriended {name}.");
        }
        catch (Exception ex) { StatusMessage = "Couldn't unfriend: " + ex.Message; }
    }

    [RelayCommand]
    private Task ProfileJoinAsync() => OpenProfile is { } p && ProfileCanJoin
        ? JoinFriendAsync(ProfileAsTile(p))
        : Task.CompletedTask;

    [RelayCommand]
    private void ProfileJoinWithAlts()
    {
        if (OpenProfile is { } p && ProfileCanJoin) JoinFriendWithAlts(ProfileAsTile(p));
    }

    [RelayCommand]
    private void ProfileToggleFavourite()
    {
        if (OpenProfile is not { } p) return;
        var tile = AllFriends.FirstOrDefault(f => f.UserId == p.UserId)
                ?? OnlineFriends.FirstOrDefault(f => f.UserId == p.UserId)
                ?? ProfileAsTile(p);
        tile.IsFavourite = Settings.FavouriteFriends.Contains(p.UserId);
        ToggleFavouriteFriend(tile);
        OnPropertyChanged(nameof(ProfileIsFavourite));
    }

    [RelayCommand]
    private void ProfileOnWeb()
    {
        if (OpenProfile is not { } p) return;
        try { Process.Start(new ProcessStartInfo($"https://www.roblox.com/users/{p.UserId}/profile") { UseShellExecute = true }); } catch { }
    }

    private static FriendTile ProfileAsTile(PlayerProfile p) => new()
    {
        UserId = p.UserId, Name = p.Name, DisplayName = p.DisplayName, PresenceType = p.PresenceType,
        Location = p.Location, PlaceId = p.PlaceId, JobId = p.JobId
    };
}
