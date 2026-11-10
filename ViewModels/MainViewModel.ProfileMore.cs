using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoDeck.Controls;
using RoDeck.Models;
using RoDeck.Services;

namespace RoDeck.ViewModels;

// The fuller profile: tabs under the header (About, Wearing, Friends, Communities, Badges, Favourites, Inventory), each
// loaded the first time it's opened; and from a profile to the same player's trade profile, or a new trade with them.
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

    /// <summary>A profile opened: back to its About tab, nothing of the last one left.</summary>
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
    }

    [RelayCommand]
    private Task RefreshProfileTab()
    {
        _profileTabsLoaded.Remove(ProfileTab);
        return LoadProfileTabAsync();
    }

    private async Task LoadProfileTabAsync()
    {
        if (OpenProfile is not { } p || ProfileTab == "About" || p.UserId != _profileTabsFor) return;
        if (!_profileTabsLoaded.Add(ProfileTab)) return;
        var a = CurrentAccount;
        var tab = ProfileTab;
        long id = p.UserId;
        bool Gone() => OpenProfile?.UserId != id || ProfileTab != tab;
        ProfileTabInfo = "";
        await ProfileTabLoad.RunAsync(async ct =>
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
                    if (a?.UserId != id) _ = MarkOwnedAsync(items);
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
                    if (!mine) _ = MarkOwnedAsync(items);
                    return items.Count > 0;
                }
            }
        }, "Loading…");
        // cut short (another tab or profile meanwhile) or failed: load it again next time it's opened
        if (ProfileTab != tab || OpenProfile?.UserId != id || ProfileTabLoad.State == LoadState.Error) _profileTabsLoaded.Remove(tab);
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
            if (CurrentAccount?.UserId != p.UserId) _ = MarkOwnedAsync(add);
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

    // ================================================================== to the trade side

    /// <summary>The player's trade profile (their limiteds); its Back button comes back to this profile.</summary>
    [RelayCommand]
    private void OpenTradeProfileFromProfile()
    {
        if (OpenProfile is not { } p || CurrentAccount is not { } a) return;
        _profileFrom = "Profile";
        _profileReturnId = p.UserId;
        _profileReturnName = p.DisplayName.Length > 0 ? p.DisplayName : p.Name;
        OnPropertyChanged(nameof(TradeProfileBackText));
        Page = "Trades";
        _ = ShowTradeProfileAsync(a, p.UserId, _profileReturnName, p.Name, "");
        TradesTab = "Profile";
    }

    /// <summary>A new trade with this player, in the trade maker.</summary>
    [RelayCommand]
    private async Task SendTradeFromProfile()
    {
        if (OpenProfile is not { } p || ProfileIsMe || CurrentAccount is not { } a) return;
        var name = p.DisplayName.Length > 0 ? p.DisplayName : p.Name;
        if (await RobloxTrades.CannotTradeReasonAsync(a.Cookie, p.UserId, name, a.Label, CancellationToken.None) is { } why)
        {
            StatusMessage = why;
            return;
        }
        if (OpenProfile != p) return;
        Page = "Trades";
        _counterOf = null;
        BuildTitle = "New trade";
        StartBuild(p.UserId, name, "");
    }

    /// <summary>On a trade profile: the same player's normal profile.</summary>
    [RelayCommand]
    private Task OpenProfileFromTrade() => TradeProfileId > 0 ? OpenProfileAsync(TradeProfileId) : Task.CompletedTask;

    private long _profileReturnId;
    private string _profileReturnName = "";
}
