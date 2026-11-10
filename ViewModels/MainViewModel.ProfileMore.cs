using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoDeck.Controls;
using RoDeck.Models;
using RoDeck.Services;

namespace RoDeck.ViewModels;

// The fuller profile: tabs under the header (About, Wearing, Friends, Communities, Badges, Favourites, Inventory), each
// loaded the first time it's opened, and Follow / Unfollow.
public partial class MainViewModel
{
    [ObservableProperty] private string profileTab = "About";
    public Loader ProfileTabLoad { get; } = new();
    private readonly HashSet<string> _profileTabsLoaded = new();
    private long _profileTabsFor;

    public ObservableCollection<WardrobeItem> ProfileWearing { get; } = new();
    public ObservableCollection<FriendTile> ProfileFriends { get; } = new();
    public ObservableCollection<CommunityTile> ProfileCommunities { get; } = new();
    public ObservableCollection<BadgeInfo> ProfileBadges { get; } = new();
    public ObservableCollection<GameTile> ProfileFavourites { get; } = new();
    public ObservableCollection<WardrobeItem> ProfileInventory { get; } = new();
    [ObservableProperty] private bool hasMoreProfileBadges;
    [ObservableProperty] private bool hasMoreProfileInventory;
    [ObservableProperty] private string profileTabInfo = "";
    private string? _badgesNext;
    private readonly Dictionary<int, string?> _invCursors = new();

    // the inventory's kinds: the wardrobe's, plus gear and emotes
    public static IReadOnlyList<WardrobeCategory> InventoryCategories { get; } =
        WardrobeCategories.Concat(new[] { new WardrobeCategory("gear", "Gear", new[] { 19 }), new WardrobeCategory("emotes", "Emotes", new[] { 61 }) }).ToList();
    public ObservableCollection<ChartChip> InventoryChips { get; } =
        new(InventoryCategories.Select(c => new ChartChip { Id = c.Key, Name = c.Name, IsSelected = c.Key == "hats" }));
    [ObservableProperty] private string inventoryCategory = "hats";

    partial void OnProfileTabChanged(string value) { _ = LoadProfileTabAsync(); if (Page == "Friends") RecordNav(); }
    partial void OnInventoryCategoryChanged(string value)
    {
        foreach (var c in InventoryChips) c.IsSelected = c.Id == value;
        _profileTabsLoaded.Remove("Inventory");
        _ = LoadProfileTabAsync();
    }

    [RelayCommand] private void SelectInventoryCategory(string? key) { if (!string.IsNullOrEmpty(key)) InventoryCategory = key; }

    /// <summary>A profile opened: back to its About tab, nothing of the last one left; and whether you follow them.</summary>
    private void ResetProfileTabs(long userId)
    {
        _profileTabsFor = userId;
        _profileTabsLoaded.Clear();
        ProfileWearing.Clear(); ProfileFriends.Clear(); ProfileCommunities.Clear(); ProfileBadges.Clear();
        ProfileFavourites.Clear(); ProfileInventory.Clear();
        HasMoreProfileBadges = HasMoreProfileInventory = false;
        ProfileTabInfo = "";
        if (!_restoring) ProfileTab = "About";
        else _ = LoadProfileTabAsync();
        _ = LoadFollowStateAsync(userId);
    }

    [RelayCommand]
    private Task RefreshProfileTab()
    {
        _profileTabsLoaded.Remove(ProfileTab);
        return LoadProfileTabAsync();
    }

    private Task LoadProfileTabAsync()
    {
        if (OpenProfile is not { } p || ProfileTab == "About" || p.UserId != _profileTabsFor) return Task.CompletedTask;
        if (!_profileTabsLoaded.Add(ProfileTab)) return Task.CompletedTask;
        var a = CurrentAccount;
        var tab = ProfileTab;
        long id = p.UserId;
        bool Gone() => OpenProfile?.UserId != id || ProfileTab != tab;
        ProfileTabInfo = "";
        return ProfileTabLoad.RunAsync(async ct =>
        {
            switch (tab)
            {
                case "Wearing":
                {
                    ProfileWearing.Clear();
                    var items = await RobloxProfiles.GetWearingAsync(id, ct);
                    if (Gone()) return true;
                    foreach (var i in items) ProfileWearing.Add(i);
                    _ = FillItemPicturesAsync(items);
                    return items.Count > 0;
                }
                case "Friends":
                {
                    ProfileFriends.Clear();
                    if (a is not { } acc) throw new RobloxException("Pick an account top right first.");
                    var friends = await RobloxFriends.GetFriendListAsync(acc.Cookie, id, ct);
                    if (Gone()) return true;
                    foreach (var f in friends) ProfileFriends.Add(f);
                    ProfileTabInfo = $"{friends.Count:N0} friends";
                    _ = FillHeadshotsAsync(friends);
                    return friends.Count > 0;
                }
                case "Communities":
                {
                    ProfileCommunities.Clear();
                    var list = await RobloxCommunities.GetMineAsync(id, ct);
                    if (Gone()) return true;
                    foreach (var c in list.OrderByDescending(c => c.Rank).ThenByDescending(c => c.Members)) ProfileCommunities.Add(c);
                    ProfileTabInfo = list.Count == 1 ? "1 community" : $"{list.Count} communities";
                    _ = FillCommunityIconsAsync(list);
                    return list.Count > 0;
                }
                case "Badges":
                {
                    ProfileBadges.Clear();
                    _badgesNext = null;
                    List<BadgeInfo> badges;
                    string? next;
                    try { (badges, next) = await RobloxProfiles.GetBadgesAsync(a?.Cookie, id, null, ct); }
                    catch (RobloxException ex) when (ex.Status == System.Net.HttpStatusCode.Forbidden)
                    {
                        throw new RobloxException("Their badges are private.");
                    }
                    if (Gone()) return true;
                    foreach (var b in badges) ProfileBadges.Add(b);
                    _badgesNext = next;
                    HasMoreProfileBadges = next != null;
                    _ = FillIconsAsync(badges, "badges/icons", "badgeIds", b => b.Id, (b, u) => b.IconUrl = u);
                    return badges.Count > 0;
                }
                case "Favourites":
                {
                    ProfileFavourites.Clear();
                    var games = await RobloxProfiles.GetFavouriteGamesAsync(id, ct);
                    if (Gone()) return true;
                    foreach (var g in games) ProfileFavourites.Add(g);
                    _ = FillCommunityGamesAsync(games);
                    return games.Count > 0;
                }
                default:   // Inventory
                {
                    ProfileInventory.Clear();
                    _invCursors.Clear();
                    HasMoreProfileInventory = false;
                    bool mine = a?.UserId == id;
                    if (!mine && !await RobloxProfiles.CanViewInventoryAsync(id, ct)) throw new RobloxException("Their inventory is private.");
                    var cat = InventoryCategories.FirstOrDefault(c => c.Key == InventoryCategory) ?? InventoryCategories[0];
                    var items = await LoadInventoryPageAsync(a, id, cat, more: false, ct);
                    if (Gone()) return true;
                    foreach (var i in items) ProfileInventory.Add(i);
                    _ = FillItemPicturesAsync(items);
                    return items.Count > 0;
                }
            }
        }, "Loading…");
    }

    private async Task<List<WardrobeItem>> LoadInventoryPageAsync(Account? a, long userId, WardrobeCategory cat, bool more, CancellationToken ct)
    {
        var types = more ? cat.Types.Where(t => _invCursors.TryGetValue(t, out var c) && c != null).ToArray() : cat.Types;
        var pages = await Task.WhenAll(types.Select(async t =>
        {
            _invCursors.TryGetValue(t, out var cursor);
            var (items, next) = await RobloxAvatar.GetInventoryAsync(a?.Cookie ?? "", userId, t, more ? cursor : null, ct);
            return (Type: t, Items: items, Next: next);
        }));
        foreach (var p in pages) _invCursors[p.Type] = p.Next;
        HasMoreProfileInventory = _invCursors.Values.Any(c => c != null);
        return pages.SelectMany(p => p.Items).GroupBy(i => i.AssetId).Select(g => g.First()).ToList();
    }

    [RelayCommand]
    private async Task MoreProfileInventory()
    {
        if (OpenProfile is not { } p) return;
        var cat = InventoryCategories.FirstOrDefault(c => c.Key == InventoryCategory) ?? InventoryCategories[0];
        try
        {
            var items = await LoadInventoryPageAsync(CurrentAccount, p.UserId, cat, more: true, CancellationToken.None);
            if (OpenProfile != p) return;
            var have = ProfileInventory.Select(i => i.AssetId).ToHashSet();
            var add = items.Where(i => !have.Contains(i.AssetId)).ToList();
            foreach (var i in add) ProfileInventory.Add(i);
            _ = FillItemPicturesAsync(add);
        }
        catch (RobloxException ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task MoreProfileBadges()
    {
        if (OpenProfile is not { } p || _badgesNext == null) return;
        try
        {
            var (badges, next) = await RobloxProfiles.GetBadgesAsync(CurrentAccount?.Cookie, p.UserId, _badgesNext, CancellationToken.None);
            if (OpenProfile != p) return;
            foreach (var b in badges) ProfileBadges.Add(b);
            _badgesNext = next;
            HasMoreProfileBadges = next != null;
            _ = FillIconsAsync(badges, "badges/icons", "badgeIds", b => b.Id, (b, u) => b.IconUrl = u);
        }
        catch (RobloxException ex) { StatusMessage = ex.Message; }
    }

    private static async Task FillHeadshotsAsync(List<FriendTile> friends)
    {
        try
        {
            foreach (var chunk in friends.Chunk(100))
            {
                var pics = await Thumbnails.GetAsync("AvatarHeadShot", chunk.Select(f => f.UserId), "150x150");
                foreach (var f in chunk) if (pics.TryGetValue(f.UserId, out var u)) f.HeadshotUrl = u;
            }
        }
        catch { }
    }

    /// <summary>An item on a profile: its Marketplace page (limiteds get the limited page there).</summary>
    [RelayCommand]
    private void OpenProfileItem(WardrobeItem? i)
    {
        if (i == null) return;
        _marketDirect = true;
        try { Page = "Marketplace"; } finally { _marketDirect = false; }
        _ = OpenMarketItemAsync(new MarketItem { Id = i.AssetId, Name = i.Name, ItemType = "Asset", ThumbUrl = i.ThumbUrl });
    }

    [RelayCommand]
    private Task OpenProfileFriend(FriendTile? f) => f == null ? Task.CompletedTask : OpenProfileAsync(f.UserId);

    // ================================================================== follow

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ProfileFollowText))] private bool? profileFollowing;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ProfileFollowText))] private bool unfollowArmed;
    [ObservableProperty] private bool followBusy;
    public string ProfileFollowText => ProfileFollowing == true ? (UnfollowArmed ? "Click again to unfollow" : "Following ✓") : "Follow";

    private async Task LoadFollowStateAsync(long userId)
    {
        ProfileFollowing = null;
        UnfollowArmed = false;
        if (CurrentAccount is not { UserId: > 0 } a || a.UserId == userId) return;
        var f = await RobloxProfiles.IsFollowingAsync(a.Cookie, userId, CancellationToken.None);
        if (OpenProfile?.UserId == userId || _lastProfileId == userId) ProfileFollowing = f ?? false;
    }

    [RelayCommand]
    private async Task ToggleFollow()
    {
        if (OpenProfile is not { } p || CurrentAccount is not { UserId: > 0 } a || a.UserId == p.UserId || FollowBusy) return;
        bool follow = ProfileFollowing != true;
        if (!follow && !UnfollowArmed)
        {
            UnfollowArmed = true;
            _ = Task.Delay(4000).ContinueWith(_ => UnfollowArmed = false, TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }
        UnfollowArmed = false;
        FollowBusy = true;
        var who = p.DisplayName.Length > 0 ? p.DisplayName : p.Name;
        try
        {
            await RobloxProfiles.SetFollowingAsync(a.Cookie, p.UserId, follow, CancellationToken.None);
            ProfileFollowing = follow;
            p.Followers = Math.Max(0, p.Followers + (follow ? 1 : -1));
            OnPropertyChanged(nameof(ProfileCounts));
            StatusMessage = follow ? $"{a.Label} follows {who} now." : $"{a.Label} unfollowed {who}.";
        }
        catch (RobloxException ex) when (!ex.TooMany && !ex.SignedOut)
        {
            // following can ask for Roblox's puzzle check, which only its own page can show
            Log(ActivityLog.Info, $"{(follow ? "Following" : "Unfollowing")} {who} from RoDeck needs Roblox's page ({(int?)ex.Status}: {ex.Message}).");
            OpenRobloxPageRequested?.Invoke(a, $"https://www.roblox.com/users/{p.UserId}/profile", who);
            StatusMessage = $"Roblox wants a quick check: press {(follow ? "Follow" : "Unfollow")} on {who}'s page there (the ⋯ menu), then close the window.";
            _ = RecheckFollowLaterAsync(a, p.UserId);
        }
        catch (RobloxException ex) { StatusMessage = ex.Message; }
        finally { FollowBusy = false; }
    }

    private async Task RecheckFollowLaterAsync(Account a, long userId)
    {
        for (int i = 0; i < 18; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            if (OpenProfile?.UserId != userId || CurrentAccount != a) return;
            if (await RobloxProfiles.IsFollowingAsync(a.Cookie, userId, CancellationToken.None) is bool f && f != ProfileFollowing)
            {
                ProfileFollowing = f;
                return;
            }
        }
    }
}
