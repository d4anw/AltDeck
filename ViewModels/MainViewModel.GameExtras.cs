using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoDeck.Controls;
using RoDeck.Models;
using RoDeck.Services;

namespace RoDeck.ViewModels;

// The tabs under a game page's pictures: Servers (the server finder), Private servers, Store (game passes) and Badges.
// Each loads the first time it's opened for that game.
public partial class MainViewModel
{
    [ObservableProperty] private string gameTab = "Servers";
    private long _gameExtrasFor;
    private readonly HashSet<string> _gameTabsLoaded = new();

    public ObservableCollection<PrivateServer> PrivateServers { get; } = new();
    public ObservableCollection<GamePassInfo> GamePasses { get; } = new();
    public ObservableCollection<BadgeInfo> GameBadges { get; } = new();
    public Loader PrivateLoad { get; } = new();
    public Loader StoreLoad { get; } = new();
    public Loader BadgesLoad { get; } = new();
    [ObservableProperty] private string badgesInfo = "";

    partial void OnGameTabChanged(string value) => _ = LoadGameTabAsync();

    /// <summary>A new game page: back to the Servers tab, nothing of the last game left.</summary>
    private void ResetGameExtras()
    {
        GameTab = "Servers";
        _gameTabsLoaded.Clear();
        _gameExtrasFor = 0;
        PrivateServers.Clear(); GamePasses.Clear(); GameBadges.Clear();
        BadgesInfo = "";
    }

    [RelayCommand]
    private Task RefreshGameTab()
    {
        _gameTabsLoaded.Remove(GameTab);
        return LoadGameTabAsync();
    }

    private Task LoadGameTabAsync()
    {
        if (OpenGame is not { } g || GameTab == "Servers") return Task.CompletedTask;
        if (_gameExtrasFor != g.UniverseId) { _gameTabsLoaded.Clear(); _gameExtrasFor = g.UniverseId; }
        if (!_gameTabsLoaded.Add(GameTab)) return Task.CompletedTask;
        var a = CurrentAccount;
        return GameTab switch
        {
            "Private" => PrivateLoad.RunAsync(async ct =>
            {
                _ = LoadServerOfferAsync(g);
                PrivateServers.Clear();
                if (a is not { UserId: > 0 }) throw new RobloxException("Pick an account top right first.");
                var list = await RobloxGameExtras.GetPrivateServersAsync(a.Cookie, g.PlaceId, a.UserId, ct);
                if (OpenGame != g) return true;
                foreach (var s in list) PrivateServers.Add(s);
                return list.Count > 0;
            }, "Looking for private servers…"),
            "Store" => StoreLoad.RunAsync(async ct =>
            {
                GamePasses.Clear();
                var list = await RobloxGameExtras.GetPassesAsync(a?.Cookie, g.UniverseId, ct);
                if (OpenGame != g) return true;
                foreach (var p in list) GamePasses.Add(p);
                _ = FillIconsAsync(list, "game-passes", "gamePassIds", p => p.Id, (p, u) => p.IconUrl = u);
                return list.Count > 0;
            }, "Opening the store…"),
            "Badges" => BadgesLoad.RunAsync(async ct =>
            {
                GameBadges.Clear();
                var list = await RobloxGameExtras.GetBadgesAsync(g.UniverseId, ct);
                if (OpenGame != g) return true;
                foreach (var b in list) GameBadges.Add(b);
                BadgesInfo = list.Count == 0 ? "" : $"{list.Count} badges";
                _ = FillIconsAsync(list, "badges/icons", "badgeIds", b => b.Id, (b, u) => b.IconUrl = u);
                if (a is { UserId: > 0 } && list.Count > 0) _ = FillOwnedBadgesAsync(a, g, list);
                return list.Count > 0;
            }, "Loading badges…"),
            _ => Task.CompletedTask
        };
    }

    private static async Task FillIconsAsync<T>(List<T> list, string service, string param, Func<T, long> id, Action<T, string> set)
    {
        try
        {
            var icons = await RobloxGameExtras.GetIconsAsync(service, param, list.Select(id), CancellationToken.None);
            foreach (var x in list) if (icons.TryGetValue(id(x), out var u)) set(x, u);
        }
        catch (Exception ex) when (ex is RobloxException or System.Text.Json.JsonException) { }
    }

    private async Task FillOwnedBadgesAsync(Account a, GameDetails g, List<BadgeInfo> list)
    {
        try
        {
            var owned = await RobloxGameExtras.GetOwnedBadgesAsync(a.Cookie, a.UserId, list.Select(b => b.Id), CancellationToken.None);
            if (OpenGame != g) return;
            foreach (var b in list) if (owned.TryGetValue(b.Id, out var d)) b.OwnedAt = d;
            int have = owned.Count;
            BadgesInfo = $"{list.Count} badges · {a.Label} has {have}";
            // yours first, then the most won
            var sorted = list.OrderByDescending(b => b.IsOwned).ThenByDescending(b => b.Awarded).ToList();
            GameBadges.Clear();
            foreach (var b in sorted) GameBadges.Add(b);
        }
        catch (Exception ex) when (ex is RobloxException or System.Text.Json.JsonException) { }
    }

    // ---- private servers

    [RelayCommand]
    private async Task JoinPrivateServer(PrivateServer? s)
    {
        if (s is not { CanJoin: true } || OpenGame is not { } g) return;
        if (CurrentAccount is not { } a) { StatusMessage = "Pick an account top right first."; return; }
        _placeNames[g.PlaceId] = g.Name;
        await LaunchAccountsAsync(new List<Account> { a }, PrivateLink(g, s), "private server " + s.Name, launcher: true);
    }

    [RelayCommand]
    private void JoinPrivateServerWithAlts(PrivateServer? s)
    {
        if (s is not { CanJoin: true } || OpenGame is not { } g) return;
        _placeNames[g.PlaceId] = g.Name;
        TargetText = PrivateLink(g, s);
        GoMulti("Launch");
        StatusMessage = $"The private server \"{s.Name}\" is filled in. Pick your alts, then launch (they need access to it too).";
    }

    private static string PrivateLink(GameDetails g, PrivateServer s) => $"https://www.roblox.com/games/{g.PlaceId}?accessCode={s.AccessCode}";

    // ---- creating one: a monthly Robux subscription, so always behind a confirm card that says so

    /// <summary>Whether this game sells private servers, and the price a month (0 = free); null while unknown.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CreateServerText), nameof(CanCreateServer))] private long? privateServerPrice;
    [ObservableProperty] private bool privateServersSold;
    /// <summary>Why this account can't create one right now ("" when it can), e.g. its free servers for this game are used up.</summary>
    [ObservableProperty] private string createServerBlocked = "";
    [ObservableProperty] private bool showCreateServer;
    [ObservableProperty] private string newServerName = "";
    [ObservableProperty] private bool createServerBusy;
    [ObservableProperty] private string createServerError = "";

    public bool CanCreateServer => PrivateServersSold && PrivateServerPrice != null && CreateServerBlocked.Length == 0;
    public string CreateServerText => PrivateServerPrice is long p ? (p == 0 ? "Create one (free)" : $"Create one · R$ {p:N0}/month") : "Create one";
    public string CreateServerConfirmText => PrivateServerPrice is long p && p > 0 ? $"Create for R$ {p:N0} a month" : "Create for free";
    public string CreateServerPriceText => PrivateServerPrice is long p && p > 0 ? $"R$ {p:N0} a month" : "Free";
    public bool CreateServerIsFree => PrivateServerPrice is 0;

    private async Task LoadServerOfferAsync(GameDetails g)
    {
        PrivateServersSold = false;
        PrivateServerPrice = null;
        CreateServerBlocked = "";
        var a = CurrentAccount;
        try
        {
            // signed in as the account: Roblox Plus members see their own (free) price
            var offer = await RobloxGameExtras.GetPrivateServerOfferAsync(g.PlaceId, a?.Cookie, CancellationToken.None);
            if (OpenGame != g || CurrentAccount != a) return;
            PrivateServersSold = offer.Sold;
            PrivateServerPrice = offer.Sold ? offer.Price : null;
            CreateServerBlocked = offer.Sold && !offer.CanCreate
                ? (offer.Price == 0 ? "You've made the most free private servers this game allows." : "Roblox doesn't let this account create one here right now.")
                : "";
            OnPropertyChanged(nameof(CanCreateServer));
        }
        catch { }
    }

    [RelayCommand]
    private void CreatePrivateServer()
    {
        if (OpenGame is not { } g) return;
        if (CurrentAccount is not { } a) { StatusMessage = "Pick an account top right first."; return; }
        if (CreateServerBlocked.Length > 0) { StatusMessage = CreateServerBlocked; return; }
        if (!CanCreateServer) { StatusMessage = $"{g.Name} doesn't sell private servers."; return; }
        NewServerName = $"{a.Label}'s server";
        long price = PrivateServerPrice ?? 0;
        CreateServerError = Settings.MarketSpendLimit > 0 && price > Settings.MarketSpendLimit ? "This is over your spending limit (Settings)."
                          : a.Robux is long have && price > have ? $"{a.Label} has only R$ {have:N0}." : "";
        OnPropertyChanged(nameof(CreateServerConfirmText));
        OnPropertyChanged(nameof(CreateServerPriceText));
        OnPropertyChanged(nameof(CreateServerIsFree));
        ShowCreateServer = true;
    }

    [RelayCommand] private void CancelCreateServer() { if (!CreateServerBusy) ShowCreateServer = false; }

    [RelayCommand]
    private async Task ConfirmCreateServer()
    {
        if (OpenGame is not { } g || CurrentAccount is not { } a || CreateServerBusy || CreateServerError.Length > 0) return;
        var name = (NewServerName ?? "").Trim();
        if (name.Length == 0) { CreateServerError = "Give it a name."; return; }
        if (name.Length > 50) name = name[..50];
        long price = PrivateServerPrice ?? 0;
        CreateServerBusy = true;
        try
        {
            await RobloxGameExtras.CreatePrivateServerAsync(a.Cookie, g.UniverseId, name, price, CancellationToken.None);
            ShowCreateServer = false;
            var msg = price > 0 ? $"{a.Label} created the private server \"{name}\" for {g.Name} (R$ {price:N0} a month, renews until you cancel it on Roblox)."
                                : $"{a.Label} created the free private server \"{name}\" for {g.Name}.";
            StatusMessage = msg;
            Log(ActivityLog.Purchase, msg);
            if (price > 0) await RefreshRobuxAsync(a);
            _gameTabsLoaded.Remove("Private");
            await LoadGameTabAsync();
        }
        catch (RobloxException ex) when (ex.Challenge)
        {
            ShowCreateServer = false;
            OpenRobloxPageRequested?.Invoke(a, $"https://www.roblox.com/games/{g.PlaceId}#!/game-instances", g.Name);
            StatusMessage = "Roblox wants a quick security check: create it on the game's Servers tab there.";
        }
        catch (RobloxException ex) { CreateServerError = ex.Message; }
        finally { CreateServerBusy = false; }
    }

    /// <summary>Your own private server: copies its share link (anyone with the link can join).</summary>
    [RelayCommand]
    private async Task CopyPrivateServerLink(PrivateServer? s)
    {
        if (s == null || CurrentAccount is not { } a) return;
        if (!s.IsMine) { StatusMessage = "Only the owner can share a private server's link."; return; }
        try
        {
            var link = await RobloxGameExtras.GetPrivateServerLinkAsync(a.Cookie, s.Id, CancellationToken.None);
            if (link.Length == 0) { StatusMessage = "Roblox didn't give a link for this server. Try again in a moment."; return; }
            try { System.Windows.Clipboard.SetText(link); StatusMessage = $"Link to \"{s.Name}\" copied. Anyone with it can join, so share it with care."; }
            catch { StatusMessage = "Couldn't copy the link (another app is using the clipboard). Try again."; }
        }
        catch (RobloxException ex) { StatusMessage = ex.Message; }
    }

    // ---- the store

    [ObservableProperty] private GamePassInfo? passToBuy;
    [ObservableProperty] private bool showPassBuy;
    [ObservableProperty] private bool passBuyBusy;
    [ObservableProperty] private string passBuyError = "";

    [RelayCommand]
    private void BuyPass(GamePassInfo? p)
    {
        if (p is not { CanBuy: true }) return;
        if (CurrentAccount is not { } a) { StatusMessage = "Pick an account top right first."; return; }
        PassToBuy = p;
        PassBuyError = Settings.MarketSpendLimit > 0 && p.Price > Settings.MarketSpendLimit ? "This is over your spending limit (Settings)."
                     : a.Robux is long have && p.Price > have ? $"{a.Label} has only R$ {have:N0}." : "";
        ShowPassBuy = true;
    }

    [RelayCommand] private void CancelPassBuy() { if (!PassBuyBusy) { ShowPassBuy = false; PassToBuy = null; } }

    [RelayCommand]
    private async Task ConfirmPassBuy()
    {
        if (PassToBuy is not { } p || PassBuyBusy || PassBuyError.Length > 0) return;
        if (CurrentAccount is not { } a) return;
        PassBuyBusy = true;
        try
        {
            var r = await RobloxGameExtras.BuyPassAsync(a.Cookie, a.UserId, OpenGame?.UniverseId ?? 0, p, CancellationToken.None);
            if (r.Ok)
            {
                p.Owned = true;
                ShowPassBuy = false;
                var msg = $"{a.Label} bought {p.Name} for R$ {p.Price:N0}.";
                StatusMessage = msg;
                Log(ActivityLog.Purchase, msg + (OpenGame is { } g ? $" ({g.Name})" : ""));
                await RefreshRobuxAsync(a);
            }
            else PassBuyError = r.Message;
        }
        catch (RobloxException ex) when (ex.Challenge || ex.Status == System.Net.HttpStatusCode.Forbidden)
        {
            ShowPassBuy = false;
            OpenRobloxPageRequested?.Invoke(a, $"https://www.roblox.com/game-pass/{p.Id}", p.Name);
            StatusMessage = "Roblox wants a quick security check: finish buying on the pass's page.";
        }
        catch (RobloxException ex) { PassBuyError = ex.Message; }
        finally { PassBuyBusy = false; }
    }

    [RelayCommand]
    private void OpenPassOnWeb(GamePassInfo? p)
    {
        if (p == null) return;
        try { Process.Start(new ProcessStartInfo($"https://www.roblox.com/game-pass/{p.Id}") { UseShellExecute = true }); } catch { }
    }
}
