using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoDeck.Controls;
using RoDeck.Models;
using RoDeck.Services;

namespace RoDeck.ViewModels;

// Communities (Roblox's new name for groups): the account's communities, search, and a community's page with its shout,
// about, games, store and members; join and leave.
public partial class MainViewModel
{
    public ObservableCollection<CommunityTile> MyCommunities { get; } = new();
    public ObservableCollection<CommunityTile> CommunityResults { get; } = new();
    public Loader CommunitiesLoad { get; } = new();
    public Loader CommunitySearchLoad { get; } = new();

    [ObservableProperty] private string communitySearchText = "";
    [ObservableProperty] private bool isCommunitySearch;

    private Account? _communitiesFor;
    private DateTime _communitiesAt;

    private void MaybeLoadCommunities()
    {
        if (Page != "Communities") return;
        if (_communitiesFor == CurrentAccount && DateTime.Now - _communitiesAt < TimeSpan.FromMinutes(5) && MyCommunities.Count > 0) return;
        _ = LoadMyCommunitiesAsync();
    }

    [RelayCommand]
    private Task RefreshCommunities() => IsCommunityOpen ? OpenCommunityByIdAsync(_lastCommunityId) : LoadMyCommunitiesAsync();

    private Task LoadMyCommunitiesAsync() => CommunitiesLoad.RunAsync(async ct =>
    {
        MyCommunities.Clear();
        if (CurrentAccount is not { UserId: > 0 } a) throw new RobloxException("Pick an account top right first.");
        var list = await RobloxCommunities.GetMineAsync(a.UserId, ct);
        if (a != CurrentAccount) return true;
        _communitiesFor = a;
        _communitiesAt = DateTime.Now;
        foreach (var c in list.OrderByDescending(c => c.Rank).ThenByDescending(c => c.Members)) MyCommunities.Add(c);
        _ = FillCommunityIconsAsync(list);
        return list.Count > 0;
    }, "Loading your communities…");

    [RelayCommand]
    private Task SearchCommunities()
    {
        var q = (CommunitySearchText ?? "").Trim();
        if (q.Length == 0) { IsCommunitySearch = false; CommunityResults.Clear(); return Task.CompletedTask; }
        IsCommunitySearch = true;
        if (IsCommunityOpen) CloseCommunity();
        return CommunitySearchLoad.RunAsync(async ct =>
        {
            CommunityResults.Clear();
            var list = await RobloxCommunities.SearchAsync(q, ct);
            var mine = MyCommunities.Select(c => c.Id).ToHashSet();
            foreach (var c in list) CommunityResults.Add(c);
            _ = FillCommunityIconsAsync(list);
            return list.Count > 0;
        }, "Searching…");
    }

    [RelayCommand]
    private void ClearCommunitySearch()
    {
        CommunitySearchText = "";
        IsCommunitySearch = false;
        CommunityResults.Clear();
    }

    private static async Task FillCommunityIconsAsync(List<CommunityTile> list)
    {
        try
        {
            var icons = await RobloxCommunities.GetIconsAsync(list.Select(c => c.Id), CancellationToken.None);
            foreach (var c in list) if (icons.TryGetValue(c.Id, out var u)) c.IconUrl = u;
        }
        catch (Exception ex) when (ex is RobloxException or System.Text.Json.JsonException) { }
    }

    // ================================================================== a community's page

    [ObservableProperty] private bool isCommunityOpen;
    [ObservableProperty] private CommunityDetails? openCommunity;
    [ObservableProperty] private string openCommunityIcon = "";
    [ObservableProperty] private string communityTab = "Games";
    /// <summary>"Member", "Pending" (asked to join) or "" (not in it).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CommunityJoinText))] private string communityMembership = "";
    [ObservableProperty] private string communityRole = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CommunityJoinText))] private bool communityLeaveArmed;
    [ObservableProperty] private bool communityBusy;
    public Loader CommunityLoad { get; } = new();

    public string CommunityJoinText => CommunityMembership switch
    {
        "Member" => CommunityLeaveArmed ? "Click again to leave" : "Leave",
        "Pending" => "Request sent",
        _ => OpenCommunity is { PublicEntry: false } ? "Ask to join" : "Join",
    };

    public ObservableCollection<GameTile> CommunityGames { get; } = new();
    public ObservableCollection<MarketItem> CommunityStore { get; } = new();
    public ObservableCollection<CommunityMember> CommunityMembers { get; } = new();
    public ObservableCollection<CommunityRole> CommunityRoles { get; } = new();
    [ObservableProperty] private CommunityRole? communityRoleFilter;
    [ObservableProperty] private bool hasMoreMembers;
    public Loader CommunityTabLoad { get; } = new();

    private long _lastCommunityId;
    private string? _membersNext;
    private readonly HashSet<string> _communityTabsLoaded = new();

    partial void OnCommunityTabChanged(string value) { _ = LoadCommunityTabAsync(); RecordNav(); }
    partial void OnCommunityRoleFilterChanged(CommunityRole? value) { if (CommunityTab == "Members") { _communityTabsLoaded.Remove("Members"); _ = LoadCommunityTabAsync(); } }

    [RelayCommand]
    private Task OpenCommunityTile(CommunityTile? c) => c == null ? Task.CompletedTask : OpenCommunityByIdAsync(c.Id, c.IconUrl);

    /// <summary>Opens a community's page (from the list, a search, or a link elsewhere in RoDeck).</summary>
    public async Task OpenCommunityByIdAsync(long id, string icon = "")
    {
        if (id <= 0) return;
        _lastCommunityId = id;
        OpenCommunity = null;
        OpenCommunityIcon = icon;
        CommunityGames.Clear(); CommunityStore.Clear(); CommunityMembers.Clear(); CommunityRoles.Clear();
        _communityTabsLoaded.Clear();
        CommunityLeaveArmed = false;
        CommunityRoleFilter = null;
        IsCommunityOpen = true;
        if (Page != "Communities") Page = "Communities";
        var a = CurrentAccount;
        await CommunityLoad.RunAsync(async ct =>
        {
            var d = await RobloxCommunities.GetAsync(id, ct);
            if (_lastCommunityId != id) return true;
            OpenCommunity = d;
            var mine = a is { UserId: > 0 } ? MyCommunities.FirstOrDefault(c => c.Id == id) : null;
            if (mine == null && a is { UserId: > 0 } && _communitiesFor != a)
            {
                try { mine = (await RobloxCommunities.GetMineAsync(a.UserId, ct)).FirstOrDefault(c => c.Id == id); } catch (RobloxException) { }
            }
            CommunityMembership = mine != null ? "Member" : "";
            CommunityRole = mine?.Role ?? "";
            if (OpenCommunityIcon.Length == 0)
                _ = Task.Run(async () =>
                {
                    try { return await RobloxCommunities.GetIconsAsync(new[] { id }, CancellationToken.None); }
                    catch { return new Dictionary<long, string>(); }
                }).ContinueWith(t => { if (_lastCommunityId == id && t.Result.TryGetValue(id, out var u)) OpenCommunityIcon = u; },
                                TaskScheduler.FromCurrentSynchronizationContext());
            return true;
        }, "Opening community…");
        RecordNav();
        if (OpenCommunity != null) _ = LoadCommunityTabAsync();
    }

    [RelayCommand]
    private void CloseCommunity()
    {
        CommunityLoad.Cancel();
        CommunityLoad.State = LoadState.Ready;
        IsCommunityOpen = false;
        OpenCommunity = null;
        RecordNav();
    }

    private Task LoadCommunityTabAsync()
    {
        if (OpenCommunity is not { } c || !_communityTabsLoaded.Add(CommunityTab)) return Task.CompletedTask;
        var tab = CommunityTab;
        return CommunityTabLoad.RunAsync(async ct =>
        {
            switch (tab)
            {
                case "Games":
                {
                    CommunityGames.Clear();
                    var games = await RobloxCommunities.GetGamesAsync(c.Id, ct);
                    if (OpenCommunity != c) return true;
                    foreach (var g in games) CommunityGames.Add(g);
                    _ = FillCommunityGamesAsync(games);
                    return games.Count > 0;
                }
                case "Store":
                {
                    CommunityStore.Clear();
                    var (items, _) = await RobloxMarket.SearchAsync(
                        new RobloxMarket.SearchQuery("tZsUsd2BqGViQrJ9Vs3Wah", "", 0, 0, null, null, false, c.Name), null, CurrentAccount?.Cookie, ct);
                    if (OpenCommunity != c) return true;
                    var theirs = items.Where(i => i.CreatorType == "Group" && i.CreatorId == c.Id).ToList();
                    foreach (var i in theirs) CommunityStore.Add(i);
                    _ = RobloxMarket.FillThumbsAsync(theirs, "150x150", CancellationToken.None);
                    return theirs.Count > 0;
                }
                default:
                {
                    if (CommunityRoles.Count == 0)
                        foreach (var r in await RobloxCommunities.GetRolesAsync(c.Id, ct)) CommunityRoles.Add(r);
                    CommunityMembers.Clear();
                    _membersNext = null;
                    var (members, next) = await RobloxCommunities.GetMembersAsync(c.Id, CommunityRoleFilter?.Id, null, ct);
                    if (OpenCommunity != c) return true;
                    foreach (var m in members) CommunityMembers.Add(m);
                    _membersNext = next;
                    HasMoreMembers = next != null;
                    _ = FillMemberHeadshotsAsync(members);
                    return members.Count > 0;
                }
            }
        }, "Loading…");
    }

    [RelayCommand] private void ClearRoleFilter() => CommunityRoleFilter = null;

    [RelayCommand]
    private async Task MoreMembers()
    {
        if (OpenCommunity is not { } c || _membersNext == null) return;
        try
        {
            var (members, next) = await RobloxCommunities.GetMembersAsync(c.Id, CommunityRoleFilter?.Id, _membersNext, CancellationToken.None);
            if (OpenCommunity != c) return;
            foreach (var m in members) CommunityMembers.Add(m);
            _membersNext = next;
            HasMoreMembers = next != null;
            _ = FillMemberHeadshotsAsync(members);
        }
        catch (RobloxException ex) { StatusMessage = ex.Message; }
    }

    private static async Task FillCommunityGamesAsync(List<GameTile> games)
    {
        try
        {
            var icons = await Thumbnails.GetAsync("GameIcon", games.Select(g => g.UniverseId), "256x256");
            foreach (var g in games) if (icons.TryGetValue(g.UniverseId, out var u)) g.IconUrl = u;
        }
        catch { }
    }

    private static async Task FillMemberHeadshotsAsync(List<CommunityMember> members)
    {
        try
        {
            var pics = await Thumbnails.GetAsync("AvatarHeadShot", members.Select(m => m.UserId), "150x150");
            foreach (var m in members) if (pics.TryGetValue(m.UserId, out var u)) m.HeadshotUrl = u;
        }
        catch { }
    }

    [RelayCommand]
    private Task OpenCommunityMember(CommunityMember? m) => m == null ? Task.CompletedTask : OpenProfileAsync(m.UserId);

    [RelayCommand]
    private Task OpenCommunityOwner() => OpenCommunity is { OwnerId: > 0 } c ? OpenProfileAsync(c.OwnerId) : Task.CompletedTask;

    [RelayCommand]
    private void OpenCommunityStoreItem(MarketItem? item)
    {
        if (item == null) return;
        _marketDirect = true;
        try { Page = "Marketplace"; } finally { _marketDirect = false; }
        _ = OpenMarketItemAsync(item);
    }

    [RelayCommand]
    private void OpenCommunityOnWeb()
    {
        if (OpenCommunity is not { } c) return;
        try { Process.Start(new ProcessStartInfo($"https://www.roblox.com/communities/{c.Id}") { UseShellExecute = true }); } catch { }
    }

    /// <summary>Join / Ask to join, or Leave (asked twice). Roblox's security check, when it wants one, happens on its own page.</summary>
    [RelayCommand]
    private async Task JoinOrLeaveCommunity()
    {
        if (OpenCommunity is not { } c || CommunityBusy) return;
        if (CurrentAccount is not { UserId: > 0 } a) { StatusMessage = "Pick an account top right first."; return; }
        if (CommunityMembership == "Pending") return;
        if (CommunityMembership == "Member" && c.OwnerId == a.UserId) { StatusMessage = "You own this community: it can't be left from here."; return; }
        if (CommunityMembership == "Member" && !CommunityLeaveArmed)
        {
            CommunityLeaveArmed = true;
            _ = Task.Delay(4000).ContinueWith(_ => CommunityLeaveArmed = false, TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }
        CommunityBusy = true;
        try
        {
            if (CommunityMembership == "Member")
            {
                await RobloxCommunities.LeaveAsync(a.Cookie, c.Id, a.UserId, CancellationToken.None);
                CommunityMembership = "";
                CommunityRole = "";
                if (MyCommunities.FirstOrDefault(x => x.Id == c.Id) is { } gone) MyCommunities.Remove(gone);
                StatusMessage = $"{a.Label} left {c.Name}.";
            }
            else
            {
                await RobloxCommunities.JoinAsync(a.Cookie, c.Id, CancellationToken.None);
                var mine = (await RobloxCommunities.GetMineAsync(a.UserId, CancellationToken.None)).FirstOrDefault(x => x.Id == c.Id);
                CommunityMembership = mine != null ? "Member" : "Pending";
                CommunityRole = mine?.Role ?? "";
                if (mine != null) { mine.IconUrl = OpenCommunityIcon; MyCommunities.Insert(0, mine); }
                StatusMessage = mine != null ? $"{a.Label} joined {c.Name}." : $"Asked to join {c.Name}: its admins decide.";
            }
        }
        catch (RobloxException ex) when (ex.Challenge)
        {
            OpenRobloxPageRequested?.Invoke(a, $"https://www.roblox.com/communities/{c.Id}", c.Name);
            StatusMessage = "Roblox wants a quick security check: finish it on the community's page.";
        }
        catch (RobloxException ex) { StatusMessage = ex.Message; }
        finally { CommunityBusy = false; CommunityLeaveArmed = false; }
    }
}
