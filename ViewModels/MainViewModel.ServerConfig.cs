using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoDeck.Controls;
using RoDeck.Models;
using RoDeck.Services;

namespace RoDeck.ViewModels;

// A private server's settings (roblox.com's "Configure Private Server" page) in RoDeck: name, joining on / off,
// friends, members, the share link (copy / regenerate) and, for paid ones, renewing. Opened from the game page's
// Private servers tab and from Multi-launch → Where to? → My private servers.
public partial class MainViewModel
{
    [ObservableProperty] private bool showServerConfig;
    public Loader ServerConfigLoad { get; } = new();
    [ObservableProperty] private ServerSettings? configServer;
    [ObservableProperty] private string configName = "";
    [ObservableProperty] private string configGameIcon = "";
    [ObservableProperty] private bool configActive;
    [ObservableProperty] private bool configFriendsAllowed;
    [ObservableProperty] private bool configRenewing;
    [ObservableProperty] private string configLink = "";
    [ObservableProperty] private string configAddName = "";
    [ObservableProperty] private bool configBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ConfigRegenerateText))] private bool configRegenerateArmed;
    public ObservableCollection<CommunityMember> ConfigMembers { get; } = new();

    public string ConfigRegenerateText => ConfigRegenerateArmed ? "Click again: the old link stops working" : "Regenerate";
    public bool ConfigIsPaid => ConfigServer is { Price: > 0 };
    public string ConfigPriceText => ConfigServer is { Price: > 0 } s ? $"R$ {s.Price:N0} a month" : "Free";
    public string ConfigRenewText => ConfigServer is not { Price: > 0 } s ? "" :
        s.Expires is DateTime d
            ? (ConfigRenewing ? "Renews on " : "Ends on ") + d.ToLocalTime().ToString("d MMMM yyyy")
            : ConfigRenewing ? "Renews every month" : "Won't renew";

    /// <summary>"Free", or "R$ 10 a month  ·  Renews on 12 December 2026".</summary>
    public string ConfigPriceLine => ConfigRenewText.Length > 0 ? $"{ConfigPriceText}  ·  {ConfigRenewText}" : ConfigPriceText;

    partial void OnConfigRenewingChanged(bool value) { OnPropertyChanged(nameof(ConfigRenewText)); OnPropertyChanged(nameof(ConfigPriceLine)); }

    /// <summary>Opens a private server's settings (it must be the account's own).</summary>
    public async Task OpenServerConfigAsync(long serverId)
    {
        if (CurrentAccount is not { } a || serverId <= 0) return;
        ConfigServer = null;
        ConfigMembers.Clear();
        ConfigGameIcon = "";
        ConfigAddName = "";
        ConfigRegenerateArmed = false;
        ShowServerConfig = true;
        await ServerConfigLoad.RunAsync(async ct =>
        {
            var s = await RobloxPrivateServers.GetSettingsAsync(a.Cookie, serverId, ct);
            if (a != CurrentAccount) return true;
            ApplyConfig(s);
            if (s.Link.Length == 0 && s.Active)
            {
                try { ConfigLink = await RobloxGameExtras.GetPrivateServerLinkAsync(a.Cookie, serverId, ct); } catch (RobloxException) { }
            }
            _ = FillConfigPicturesAsync(s);
            return true;
        }, "Opening its settings…");
    }

    private void ApplyConfig(ServerSettings s)
    {
        ConfigServer = s;
        ConfigName = s.Name;
        ConfigActive = s.Active;
        ConfigFriendsAllowed = s.FriendsAllowed;
        ConfigRenewing = s.SubscriptionActive;
        ConfigLink = s.Link;
        ConfigMembers.Clear();
        foreach (var (id, name, display) in s.Members) ConfigMembers.Add(new CommunityMember { UserId = id, Name = name, DisplayName = display });
        OnPropertyChanged(nameof(ConfigIsPaid));
        OnPropertyChanged(nameof(ConfigPriceText));
        OnPropertyChanged(nameof(ConfigRenewText));
        OnPropertyChanged(nameof(ConfigPriceLine));
    }

    private async Task FillConfigPicturesAsync(ServerSettings s)
    {
        try
        {
            if (s.UniverseId > 0)
            {
                var icons = await Thumbnails.GetAsync("GameIcon", new[] { s.UniverseId }, "256x256");
                if (ConfigServer == s && icons.TryGetValue(s.UniverseId, out var u)) ConfigGameIcon = u;
            }
            var members = ConfigMembers.ToList();
            if (members.Count == 0) return;
            var heads = await Thumbnails.GetAsync("AvatarHeadShot", members.Select(m => m.UserId), "150x150");
            foreach (var m in members) if (heads.TryGetValue(m.UserId, out var h)) m.HeadshotUrl = h;
        }
        catch { }
    }

    [RelayCommand] private void CloseServerConfig() { if (!ConfigBusy) ShowServerConfig = false; }

    /// <summary>One change at a time; afterwards the private servers list shows it too.</summary>
    private async Task ConfigChangeAsync(Func<Account, ServerSettings, Task> change, string done)
    {
        if (ConfigServer is not { } s || CurrentAccount is not { } a || ConfigBusy) return;
        ConfigBusy = true;
        try
        {
            await change(a, s);
            if (done.Length > 0) StatusMessage = done;
            _gameTabsLoaded.Remove("Private");
        }
        catch (RobloxException ex) { StatusMessage = ex.Message; }
        finally { ConfigBusy = false; }
    }

    [RelayCommand]
    private Task SaveConfigName()
    {
        var name = (ConfigName ?? "").Trim();
        if (name.Length == 0 || name == ConfigServer?.Name) return Task.CompletedTask;
        if (name.Length > 50) name = name[..50];
        return ConfigChangeAsync(async (a, s) =>
        {
            await RobloxPrivateServers.RenameAsync(a.Cookie, s.Id, name, CancellationToken.None);
            ApplyConfig(await RobloxPrivateServers.GetSettingsAsync(a.Cookie, s.Id, CancellationToken.None));
        }, $"Renamed to \"{name}\".");
    }

    [RelayCommand]
    private Task ToggleConfigActive() => ConfigChangeAsync(async (a, s) =>
    {
        bool on = !ConfigActive;
        await RobloxPrivateServers.SetActiveAsync(a.Cookie, s.Id, on, CancellationToken.None);
        ConfigActive = on;
        ConfigLink = "";
        if (on) { try { ConfigLink = await RobloxGameExtras.GetPrivateServerLinkAsync(a.Cookie, s.Id, CancellationToken.None); } catch (RobloxException) { } }
    }, ConfigActive ? "Joining is off: nobody can join, and the old link stopped working." : "Joining is on again.");

    [RelayCommand]
    private Task ToggleConfigFriends() => ConfigChangeAsync(async (a, s) =>
    {
        bool on = !ConfigFriendsAllowed;
        await RobloxPrivateServers.SetFriendsAllowedAsync(a.Cookie, s.Id, on, CancellationToken.None);
        ConfigFriendsAllowed = on;
    }, ConfigFriendsAllowed ? "Friends can't join on their own anymore." : "Your friends can join now.");

    [RelayCommand]
    private Task ToggleConfigRenewing() => ConfigChangeAsync(async (a, s) =>
    {
        bool on = !ConfigRenewing;
        await RobloxPrivateServers.SetRenewingAsync(a.Cookie, s.Id, on, s.Price, CancellationToken.None);
        ConfigRenewing = on;
    }, ConfigRenewing ? "It won't renew: it ends when this month is over." : "It renews every month again.");

    [RelayCommand]
    private async Task AddConfigMember()
    {
        var name = (ConfigAddName ?? "").Trim().TrimStart('@');
        if (name.Length == 0) return;
        (long Id, string Name, string DisplayName)? who;
        try { who = await RobloxPrivateServers.FindUserAsync(name, CancellationToken.None); }
        catch (RobloxException ex) { StatusMessage = ex.Message; return; }
        if (who is not { } u) { StatusMessage = $"There's no player called {name}."; return; }
        if (ConfigMembers.Any(m => m.UserId == u.Id)) { StatusMessage = $"{u.Name} can already join."; return; }
        await ConfigChangeAsync(async (a, s) =>
        {
            await RobloxPrivateServers.AddMemberAsync(a.Cookie, s.Id, u.Id, CancellationToken.None);
            var m = new CommunityMember { UserId = u.Id, Name = u.Name, DisplayName = u.DisplayName };
            ConfigMembers.Add(m);
            ConfigAddName = "";
            try
            {
                var h = await Thumbnails.GetAsync("AvatarHeadShot", new[] { u.Id }, "150x150");
                if (h.TryGetValue(u.Id, out var url)) m.HeadshotUrl = url;
            }
            catch { }
        }, $"{u.Name} can join now.");
    }

    [RelayCommand]
    private Task RemoveConfigMember(CommunityMember? m) => m == null ? Task.CompletedTask : ConfigChangeAsync(async (a, s) =>
    {
        await RobloxPrivateServers.RemoveMemberAsync(a.Cookie, s.Id, m.UserId, CancellationToken.None);
        ConfigMembers.Remove(m);
    }, $"{m.Label} can't join anymore.");

    [RelayCommand]
    private void CopyConfigLink()
    {
        if (ConfigLink.Length == 0) return;
        try { System.Windows.Clipboard.SetText(ConfigLink); StatusMessage = "Link copied. Anyone with it can join, so share it with care."; }
        catch { StatusMessage = "Couldn't copy the link (another app is using the clipboard). Try again."; }
    }

    [RelayCommand]
    private async Task RegenerateConfigLink()
    {
        if (!ConfigRegenerateArmed)
        {
            ConfigRegenerateArmed = true;
            _ = Task.Delay(4000).ContinueWith(_ => ConfigRegenerateArmed = false, TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }
        ConfigRegenerateArmed = false;
        await ConfigChangeAsync(async (a, s) => ConfigLink = await RobloxPrivateServers.NewLinkAsync(a.Cookie, s.Id, CancellationToken.None),
                                "New link made: the old one doesn't work anymore.");
    }

    /// <summary>From the game page's Private servers tab.</summary>
    [RelayCommand]
    private Task OpenPrivateServerSettings(PrivateServer? s) => s is { IsMine: true } ? OpenServerConfigAsync(s.Id) : Task.CompletedTask;

    // ================================================================== Multi-launch → Where to?: pick a game or one of your servers

    [ObservableProperty] private bool showTargetPicker;
    [ObservableProperty] private string targetPickerTab = "Games";
    [ObservableProperty] private string targetGameSearch = "";
    public ObservableCollection<GameTile> TargetGameHits { get; } = new();
    public ObservableCollection<MyServer> MyServers { get; } = new();
    public Loader TargetGamesLoad { get; } = new();
    public Loader MyServersLoad { get; } = new();
    private Account? _myServersFor;

    partial void OnTargetPickerTabChanged(string value) { if (value == "Servers") _ = LoadMyServersAsync(false); }

    [RelayCommand]
    private void OpenTargetPicker(string? tab)
    {
        TargetPickerTab = tab is "Servers" ? "Servers" : "Games";
        ShowTargetPicker = true;
        if (TargetPickerTab == "Servers") _ = LoadMyServersAsync(false);
    }

    [RelayCommand] private void CloseTargetPicker() => ShowTargetPicker = false;

    [RelayCommand]
    private Task SearchTargetGames()
    {
        var q = (TargetGameSearch ?? "").Trim();
        if (q.Length == 0) { TargetGameHits.Clear(); TargetGamesLoad.State = LoadState.Ready; return Task.CompletedTask; }
        return TargetGamesLoad.RunAsync(async ct =>
        {
            TargetGameHits.Clear();
            var hits = await RobloxGames.SearchAsync(q, ct);
            foreach (var g in hits.Take(30)) TargetGameHits.Add(g);
            var icons = await Thumbnails.GetAsync("GameIcon", TargetGameHits.Select(g => g.UniverseId), "256x256", ct);
            foreach (var g in TargetGameHits) if (icons.TryGetValue(g.UniverseId, out var u)) g.IconUrl = u;
            return TargetGameHits.Count > 0;
        }, "Searching…");
    }

    [RelayCommand]
    private void PickTargetGame(GameTile? g)
    {
        if (g is not { PlaceId: > 0 }) return;
        _placeNames[g.PlaceId] = g.Name;
        TargetText = $"https://www.roblox.com/games/{g.PlaceId}";
        ShowTargetPicker = false;
        StatusMessage = $"{g.Name} is filled in.";
    }

    [RelayCommand]
    private Task RefreshMyServers() => LoadMyServersAsync(true);

    private Task LoadMyServersAsync(bool force)
    {
        if (!force && _myServersFor == CurrentAccount && MyServers.Count > 0) return Task.CompletedTask;
        return MyServersLoad.RunAsync(async ct =>
        {
            MyServers.Clear();
            if (CurrentAccount is not { } a) throw new RobloxException("Pick an account top right first.");
            var list = await RobloxPrivateServers.GetMineAsync(a.Cookie, ct);
            if (a != CurrentAccount) return true;
            _myServersFor = a;
            foreach (var s in list.OrderByDescending(s => s.Active).ThenBy(s => s.GameName)) MyServers.Add(s);
            try
            {
                var icons = await Thumbnails.GetAsync("GameIcon", list.Select(s => s.UniverseId).Where(i => i > 0), "256x256", ct);
                foreach (var s in list) if (icons.TryGetValue(s.UniverseId, out var u)) s.IconUrl = u;
            }
            catch (RobloxException) { }
            return list.Count > 0;
        }, "Loading your private servers…");
    }

    /// <summary>Fills in the server's share link: works for every account you launch (anyone with the link can join).</summary>
    [RelayCommand]
    private async Task PickMyServer(MyServer? s)
    {
        if (s == null || CurrentAccount is not { } a) return;
        if (!s.Active) { StatusMessage = $"Joining is off for \"{s.Name}\". Turn it on in its settings first."; return; }
        try
        {
            var link = await RobloxGameExtras.GetPrivateServerLinkAsync(a.Cookie, s.Id, CancellationToken.None);
            if (link.Length == 0) { StatusMessage = "Roblox didn't give a link for this server. Try again in a moment."; return; }
            if (s.PlaceId > 0) _placeNames[s.PlaceId] = s.GameName;
            TargetText = link;
            ShowTargetPicker = false;
            StatusMessage = $"\"{s.Name}\" ({s.GameName}) is filled in.";
        }
        catch (RobloxException ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    private Task OpenMyServerSettings(MyServer? s) => s == null ? Task.CompletedTask : OpenServerConfigAsync(s.Id);
}
