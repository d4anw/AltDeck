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
        if (Page != "Communities") { ResetCommunitiesPage(); return; }
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
    [ObservableProperty] private string communityTab = "Posts";
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

    public ObservableCollection<CommunityPost> CommunityPosts { get; } = new();
    [ObservableProperty] private bool hasMorePosts;
    private string? _postsNext;
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
        CommunityPosts.Clear(); CommunityGames.Clear(); CommunityStore.Clear(); CommunityMembers.Clear(); CommunityRoles.Clear();
        _communityTabsLoaded.Clear();
        HasMorePosts = false;
        if (!_restoring) CommunityTab = "Posts";
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

    private async Task LoadCommunityTabAsync()
    {
        if (OpenCommunity is not { } c || !_communityTabsLoaded.Add(CommunityTab)) return;
        var tab = CommunityTab;
        await CommunityTabLoad.RunAsync(async ct =>
        {
            switch (tab)
            {
                case "Posts":
                {
                    CommunityPosts.Clear();
                    _postsNext = null;
                    var (posts, next) = await RobloxGameExtras.GetPostsAsync(c.Id, CurrentAccount?.Cookie, null, ct);
                    if (OpenCommunity != c) return true;
                    foreach (var p in posts) CommunityPosts.Add(p);
                    _postsNext = next;
                    HasMorePosts = next != null;
                    _ = FillPostPicturesAsync(posts);
                    return posts.Count > 0;
                }
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
        // cut short (another tab or community meanwhile) or failed: load it again next time it's opened
        if (CommunityTab != tab || OpenCommunity != c || CommunityTabLoad.State == LoadState.Error) _communityTabsLoaded.Remove(tab);
    }

    [RelayCommand] private void ClearRoleFilter() => CommunityRoleFilter = null;

    /// <summary>After joining on Roblox's page: the button follows once Roblox has it.</summary>
    private async Task RecheckMembershipLaterAsync(Account a, CommunityDetails c)
    {
        for (int i = 0; i < 18; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            if (OpenCommunity?.Id != c.Id || CurrentAccount != a) return;
            try
            {
                var mine = (await RobloxCommunities.GetMineAsync(a.UserId, CancellationToken.None)).FirstOrDefault(x => x.Id == c.Id);
                if (mine == null) continue;
                CommunityMembership = "Member";
                CommunityRole = mine.Role;
                if (MyCommunities.All(x => x.Id != c.Id)) { mine.IconUrl = OpenCommunityIcon; MyCommunities.Insert(0, mine); }
                return;
            }
            catch (RobloxException) { }
        }
    }

    [RelayCommand]
    private async Task MorePosts()
    {
        if (OpenCommunity is not { } c || _postsNext == null) return;
        try
        {
            var (posts, next) = await RobloxGameExtras.GetPostsAsync(c.Id, CurrentAccount?.Cookie, _postsNext, CancellationToken.None);
            if (OpenCommunity != c) return;
            foreach (var p in posts) CommunityPosts.Add(p);
            _postsNext = next;
            HasMorePosts = next != null;
            _ = FillPostPicturesAsync(posts);
        }
        catch (RobloxException ex) { StatusMessage = ex.Message; }
    }

    /// <summary>A post's picture (an image on Roblox) and its writer's headshot.</summary>
    private static async Task FillPostPicturesAsync(List<CommunityPost> posts)
    {
        try
        {
            var withImage = posts.Where(p => p.HasImage).ToList();
            if (withImage.Count > 0)
            {
                var pics = await Thumbnails.GetAsync("Asset", withImage.Select(p => p.ImageAssetId), "420x420");
                foreach (var p in withImage) if (pics.TryGetValue(p.ImageAssetId, out var u)) p.ImageUrl = u;
            }
            var heads = await Thumbnails.GetAsync("AvatarHeadShot", posts.Select(p => p.AuthorId).Where(i => i > 0), "150x150");
            foreach (var p in posts) if (heads.TryGetValue(p.AuthorId, out var u)) p.AuthorHeadshot = u;
        }
        catch { }
    }

    [RelayCommand]
    private Task OpenPostAuthor(CommunityPost? p) => p is { AuthorId: > 0 } ? OpenProfileAsync(p.AuthorId) : Task.CompletedTask;

    /// <summary>Leaving Communities: coming back starts at your list again (back / forward still return to where you were).</summary>
    private void ResetCommunitiesPage()
    {
        if (IsCommunityOpen) { CommunityLoad.Cancel(); CommunityLoad.State = LoadState.Ready; IsCommunityOpen = false; OpenCommunity = null; }
        if (IsCommunitySearch) { CommunitySearchText = ""; IsCommunitySearch = false; CommunityResults.Clear(); }
    }

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
        catch (RobloxException ex) when (ex.Challenge || (ex.Status == System.Net.HttpStatusCode.Forbidden && CommunityMembership != "Member"))
        {
            // joining asks for Roblox's puzzle check (only its own page can show it): the community's page, signed in
            Log(ActivityLog.Info, $"Joining {c.Name} from RoDeck needs Roblox's check ({(int?)ex.Status}: {ex.Message}); opened Roblox's page instead.");
            OpenRobloxPageRequested?.Invoke(a, $"https://www.roblox.com/communities/{c.Id}", c.Name);
            StatusMessage = $"Roblox wants a quick check before joining: press Join on {c.Name}'s page there, then close the window.";
            _ = RecheckMembershipLaterAsync(a, c);
        }
        catch (RobloxException ex) { StatusMessage = ex.Message; }
        finally { CommunityBusy = false; CommunityLeaveArmed = false; }
    }
}
