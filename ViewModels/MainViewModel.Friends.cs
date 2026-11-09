using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using RoDeck.Controls;
using RoDeck.Models;
using RoDeck.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RoDeck.ViewModels;

/// <summary>Friends page: the friend list, friend requests, player search and profiles, as the account top right.</summary>
public partial class MainViewModel
{
    /// <summary>"Friends", "Requests" or "Find".</summary>
    [ObservableProperty] private string friendsTab = "Friends";

    // ================================================================== friend list

    /// <summary>The friends on screen: the current page(s) of the full list, or of the filter's matches.</summary>
    public ObservableCollection<FriendTile> AllFriends { get; } = new();
    public Loader AllFriendsLoad { get; } = new();
    private List<FriendTile> _friendsAll = new();
    private const int FriendsPageSize = 40;
    private int _friendsShown = FriendsPageSize;

    [ObservableProperty] private string friendsFilter = "";
    [ObservableProperty] private string friendsSummary = "";
    [ObservableProperty] private bool hasMoreFriends;
    [ObservableProperty] private string moreFriendsText = "";

    partial void OnFriendsFilterChanged(string value)
    {
        _friendsShown = FriendsPageSize;
        ShowFriendsPage();
    }

    [RelayCommand]
    private void ShowAllFriends()
    {
        _friendsShown = int.MaxValue;
        ShowFriendsPage();
    }

    [RelayCommand]
    private void ShowMoreFriends()
    {
        _friendsShown = (int)Math.Min(int.MaxValue, (long)_friendsShown + FriendsPageSize);
        ShowFriendsPage();
    }

    /// <summary>Puts the first pages of friends (or of the filter's matches) on screen and loads just their pictures.</summary>
    private void ShowFriendsPage()
    {
        var q = (FriendsFilter ?? "").Trim().TrimStart('@');
        var source = q.Length == 0 ? _friendsAll : _friendsAll.Where(f =>
            f.Label.Contains(q, StringComparison.OrdinalIgnoreCase) || f.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        var page = source.Take(_friendsShown).ToList();
        AllFriends.Clear();
        foreach (var f in page) AllFriends.Add(f);
        int left = source.Count - page.Count;
        HasMoreFriends = left > 0;
        MoreFriendsText = left > 0 ? $"Show more ({left} left)" : "";
        _ = RobloxFriends.FillHeadshotsAsync(page, CancellationToken.None);
    }

    private DateTime _friendsLoadedAt = DateTime.MinValue;
    private Account? _friendsLoadedFor;

    private void MaybeLoadFriends()
    {
        if (Page != "Friends")
        {
            // leaving Friends forgets "Show more": coming back shows the first page again
            if (_friendsShown > FriendsPageSize) { _friendsShown = FriendsPageSize; ShowFriendsPage(); }
            return;
        }
        if (_friendsLoadedFor == CurrentAccount && DateTime.Now - _friendsLoadedAt < TimeSpan.FromMinutes(2)) return;
        _ = LoadFriendsPageAsync();
    }

    [RelayCommand]
    private Task RefreshFriends() => LoadFriendsPageAsync(force: true);

    private async Task LoadFriendsPageAsync(bool force = false)
    {
        var a = CurrentAccount;
        // the refresh button keeps "Show more"; another account or a fresh visit starts at the first page
        bool keepShown = force && _friendsLoadedFor == a;
        _friendsLoadedFor = a;
        _friendsLoadedAt = DateTime.Now;
        AllFriends.Clear();
        _friendsAll = new();
        HasMoreFriends = false;
        Requests.Clear();
        FriendsSummary = "";
        if (a == null || string.IsNullOrEmpty(a.Cookie))
        {
            AllFriendsLoad.State = LoadState.Empty;
            RequestsLoad.State = LoadState.Empty;
            return;
        }

        if (!keepShown) _friendsShown = FriendsPageSize;
        var friends = AllFriendsLoad.RunAsync(async ct =>
        {
            // names first: the list shows right away, the status dots fill in when Roblox answers
            var list = await GetFriendsAsync(a, names =>
            {
                if (a != CurrentAccount || names.Count == 0) return;
                _friendsAll = FavouritesFirst(RobloxFriends.ByPresence(names));
                ShowFriendsPage();
                FriendsSummary = $"{names.Count} friend{(names.Count == 1 ? "" : "s")} · checking who's online…";
                AllFriendsLoad.State = LoadState.Ready;
            }, force);
            if (ct.IsCancellationRequested || a != CurrentAccount) return false;
            _friendsAll = FavouritesFirst(RobloxFriends.ByPresence(list));
            ShowFriendsPage();
            int online = list.Count(f => f.IsOnline), inGame = list.Count(f => f.PresenceType == 2);
            FriendsSummary = $"{list.Count} friend{(list.Count == 1 ? "" : "s")} · {online} online" + (inGame > 0 ? $" · {inGame} in a game" : "");
            return list.Count > 0;
        }, "Loading friends…");

        var requests = LoadRequestsAsync(a, force);
        await Task.WhenAll(friends, requests);
        if (AllFriendsLoad.State == LoadState.Error) Log(ActivityLog.Error, "Friends: " + AllFriendsLoad.Message);
    }

    // ================================================================== requests

    public ObservableCollection<FriendRequest> Requests { get; } = new();
    public Loader RequestsLoad { get; } = new();

    /// <summary>Incoming friend requests of the account top right (the badge on Friends).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasRequests)), NotifyPropertyChangedFor(nameof(BellCount))]
    private int requestCount;
    public bool HasRequests => RequestCount > 0;

    // the last requests list per account: shown at once next time, while a fresh copy loads quietly
    private readonly Dictionary<string, (DateTime At, List<FriendRequest> List)> _requestsStore = new();

    private async Task LoadRequestsAsync(Account a, bool force = false)
    {
        if (!force && _requestsStore.TryGetValue(a.Id, out var known))
        {
            ShowRequests(known.List);
            RequestsLoad.State = known.List.Count > 0 ? LoadState.Ready : LoadState.Empty;
            if (DateTime.Now - known.At < TimeSpan.FromSeconds(30)) return;
            try
            {
                var fresh = await RobloxFriends.GetRequestsAsync(a.Cookie, CancellationToken.None);
                _requestsStore[a.Id] = (DateTime.Now, fresh);
                if (a != CurrentAccount) return;
                ShowRequests(fresh);
                RequestsLoad.State = fresh.Count > 0 ? LoadState.Ready : LoadState.Empty;
            }
            catch { }   // keep showing the remembered list
            return;
        }

        await RequestsLoad.RunAsync(async ct =>
        {
            var list = await RobloxFriends.GetRequestsAsync(a.Cookie, ct);
            _requestsStore[a.Id] = (DateTime.Now, list);
            if (ct.IsCancellationRequested || a != CurrentAccount) return false;
            ShowRequests(list);
            return list.Count > 0;
        }, "Loading requests…");
    }

    private void ShowRequests(List<FriendRequest> list)
    {
        Requests.Clear();
        foreach (var r in list) Requests.Add(r);
        RequestCount = list.Count;
    }

    /// <summary>Keeps the remembered list in step after Accept / Decline.</summary>
    private void ForgetRequest(Account a, long userId)
    {
        if (_requestsStore.TryGetValue(a.Id, out var known))
            _requestsStore[a.Id] = (known.At, known.List.Where(r => r.UserId != userId).ToList());
    }

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
            ForgetRequest(a, r.UserId);
            ForgetFriends(a);
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
            ForgetRequest(a, r.UserId);
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
                    ForgetRequest(a, p.UserId);
                    ForgetFriends(a);
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
            ForgetFriends(a);
            _friendsAll.RemoveAll(x => x.UserId == userId);
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
