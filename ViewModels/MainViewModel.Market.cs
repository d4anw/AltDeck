using System.Collections.ObjectModel;
using System.Diagnostics;
using AltDeck.Controls;
using AltDeck.Models;
using AltDeck.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AltDeck.ViewModels;

/// <summary>
/// Marketplace: browse and search avatar items, an item page with Try on, one-click free items, and buying with Robux
/// behind a confirm card (item, price, account, balance after) and an optional spending limit.
/// </summary>
public partial class MainViewModel
{
    // ================================================================== browsing

    public ObservableCollection<MarketChip> MarketCats { get; } = new();
    public ObservableCollection<MarketChip> MarketSubs { get; } = new();
    public ObservableCollection<MarketItem> MarketItems { get; } = new();
    public Loader MarketLoad { get; } = new();

    [ObservableProperty] private string marketSearchText = "";
    [ObservableProperty] private string marketTitle = "All items";
    [ObservableProperty] private bool marketHasMore;
    [ObservableProperty] private bool marketLoadingMore;

    public IReadOnlyList<MarketOption> MarketSorts { get; } = new List<MarketOption>
    {
        new("Relevance", 0), new("Most favourited", 15), new("Bestselling this week", 23), new("Bestselling all time", 25),
        new("Recently published", 30), new("Price: low to high", 40), new("Price: high to low", 50)
    };
    public IReadOnlyList<MarketOption> MarketPrices { get; } = new List<MarketOption>
    {
        new("Any price", -1), new("Free only", 0), new("Up to R$ 25", 25), new("Up to R$ 50", 50), new("Up to R$ 100", 100),
        new("Up to R$ 250", 250), new("Up to R$ 500", 500), new("Up to R$ 1,000", 1000)
    };

    [ObservableProperty] private MarketOption? marketSort;
    [ObservableProperty] private MarketOption? marketPrice;
    [ObservableProperty] private bool marketLimitedsOnly;

    private MarketChip? _marketCat, _marketSub;
    private string? _marketNext;
    private RobloxMarket.SearchQuery? _marketQuery;
    private bool _marketReady;

    partial void OnMarketSortChanged(MarketOption? value) { if (_marketReady) _ = SearchMarketAsync(); }
    partial void OnMarketPriceChanged(MarketOption? value) { if (_marketReady) _ = SearchMarketAsync(); }
    partial void OnMarketLimitedsOnlyChanged(bool value) { if (_marketReady) _ = SearchMarketAsync(); }

    private void MaybeLoadMarket()
    {
        if (Page != "Marketplace" || MarketCats.Count > 0 || MarketLoad.State == LoadState.Loading) return;
        _ = LoadMarketAsync();
    }

    private async Task LoadMarketAsync()
    {
        await MarketLoad.RunAsync(async ct =>
        {
            var cats = await RobloxMarket.GetCategoriesAsync(ct);
            MarketCats.Clear();
            foreach (var c in cats) MarketCats.Add(c);
            MarketSort ??= MarketSorts[0];
            MarketPrice ??= MarketPrices[0];
            _marketReady = true;
            SelectCat(MarketCats.FirstOrDefault());
            return await RunSearchAsync(ct);
        }, "Loading the Marketplace…");
    }

    [RelayCommand]
    private Task RefreshMarket() => MarketCats.Count == 0 ? LoadMarketAsync() : SearchMarketAsync();

    [RelayCommand]
    private Task SelectMarketCat(MarketChip? c)
    {
        if (c == null) return Task.CompletedTask;
        SelectCat(c);
        return SearchMarketAsync();
    }

    [RelayCommand]
    private Task SelectMarketSub(MarketChip? s)
    {
        if (s == null) return Task.CompletedTask;
        _marketSub = s == _marketSub ? null : s;   // a second click on a subcategory goes back to the whole category
        foreach (var x in MarketSubs) x.IsSelected = x == _marketSub;
        MarketTitle = _marketSub != null ? $"{_marketCat?.Name} · {_marketSub.Name}" : _marketCat?.Name ?? "All items";
        return SearchMarketAsync();
    }

    private void SelectCat(MarketChip? c)
    {
        _marketCat = c;
        _marketSub = null;
        foreach (var x in MarketCats) x.IsSelected = x == c;
        MarketSubs.Clear();
        if (c != null) foreach (var s in c.Subs) { s.IsSelected = false; MarketSubs.Add(s); }
        MarketTitle = c?.Name ?? "All items";
    }

    [RelayCommand]
    private Task SearchMarketAsync()
    {
        if (!_marketReady) return LoadMarketAsync();
        if (IsMarketItemOpen) CloseMarketItem();
        return MarketLoad.RunAsync(RunSearchAsync, (MarketSearchText ?? "").Trim().Length > 0 ? "Searching…" : "Loading items…");
    }

    private async Task<bool> RunSearchAsync(CancellationToken ct)
    {
        var tax = (_marketSub ?? _marketCat)?.Taxonomy ?? "tZsUsd2BqGViQrJ9Vs3Wah";
        int sortValue = MarketSort?.Value ?? 0;
        int price = MarketPrice?.Value ?? -1;
        var q = new RobloxMarket.SearchQuery(tax, (MarketSearchText ?? "").Trim(), sortValue / 10, sortValue % 10,
                                             null, price < 0 ? null : price, MarketLimitedsOnly);
        var (items, next) = await RobloxMarket.SearchAsync(q, null, CurrentAccount?.Cookie, ct);
        if (ct.IsCancellationRequested) return false;
        _marketQuery = q;
        _marketNext = next;
        MarketHasMore = next != null;
        MarketItems.Clear();
        foreach (var i in items) MarketItems.Add(i);
        _ = RobloxMarket.FillThumbsAsync(items, "150x150", CancellationToken.None);
        return items.Count > 0;
    }

    [RelayCommand]
    private async Task LoadMoreMarket()
    {
        if (_marketQuery == null || _marketNext == null || MarketLoadingMore) return;
        MarketLoadingMore = true;
        try
        {
            var q = _marketQuery;
            var (items, next) = await RobloxMarket.SearchAsync(q, _marketNext, CurrentAccount?.Cookie, CancellationToken.None);
            if (q != _marketQuery) return;   // searched something else meanwhile
            _marketNext = next;
            MarketHasMore = next != null;
            var fresh = items.Where(i => !MarketItems.Any(m => m.Id == i.Id && m.ItemType == i.ItemType)).ToList();
            foreach (var i in fresh) MarketItems.Add(i);
            _ = RobloxMarket.FillThumbsAsync(fresh, "150x150", CancellationToken.None);
        }
        catch (RobloxException ex)
        {
            StatusMessage = ex.TooMany ? "Roblox allows only a few searches a minute. Try “Load more” again in a moment." : ex.Message;
        }
        finally { MarketLoadingMore = false; }
    }

    // ================================================================== one item

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsMarketItemOpen))]
    private MarketItem? openMarketItem;
    public bool IsMarketItemOpen => OpenMarketItem != null;
    public Loader MarketItemLoad { get; } = new();
    [ObservableProperty] private string marketItemPicture = "";
    private MarketItem? _lastMarketItem;

    partial void OnOpenMarketItemChanged(MarketItem? value) { RecordNav(); NotifyBuy(); }

    [RelayCommand]
    private Task OpenMarket(MarketItem? item) => item == null ? Task.CompletedTask : OpenMarketItemAsync(item);

    private async Task OpenMarketItemAsync(MarketItem item)
    {
        _lastMarketItem = item;
        StopTryOn();
        ShowBuyConfirm = false;
        MarketItemPicture = item.ThumbUrl;
        OpenMarketItem = item;   // show what we know at once; the details fill in
        await MarketItemLoad.RunAsync(async ct =>
        {
            var a = CurrentAccount;
            var full = await RobloxMarket.GetItemAsync(item.Id, item.IsBundle, a?.Cookie, ct);
            if (_lastMarketItem != item) return true;
            full.ThumbUrl = item.ThumbUrl;
            OpenMarketItem = full;
            var big = full.IsBundle
                ? await Thumbnails.GetAsync("BundleThumbnail", new[] { full.Id }, "420x420", ct)
                : await Thumbnails.GetAsync("Asset", new[] { full.Id }, "420x420", ct);
            if (big.TryGetValue(full.Id, out var url) && OpenMarketItem == full) MarketItemPicture = url;
            return true;
        }, "Loading item…");
    }

    [RelayCommand]
    private Task RetryOpenMarket() => _lastMarketItem != null ? OpenMarketItemAsync(_lastMarketItem) : Task.CompletedTask;

    [RelayCommand]
    private void CloseMarketItem()
    {
        StopTryOn();
        ShowBuyConfirm = false;
        OpenMarketItem = null;
    }

    [RelayCommand]
    private void OpenMarketOnWeb(MarketItem? item)
    {
        item ??= OpenMarketItem;
        if (item == null) return;
        try { Process.Start(new ProcessStartInfo(item.WebUrl) { UseShellExecute = true }); } catch { }
    }

    /// <summary>"Hat · by Roblox ✓ · 300,751 favourites"</summary>
    public string MarketItemMeta => OpenMarketItem is not { } i ? ""
        : $"{i.KindText}{(i.IsLimited ? " · Limited" : "")} · {i.Favorites:N0} favourites";

    /// <summary>The account switched: ownership and try-on are per account.</summary>
    private void MarketAccountChanged()
    {
        StopTryOn();
        ShowBuyConfirm = false;
        NotifyBuy();
        if (OpenMarketItem != null && _lastMarketItem != null) _ = OpenMarketItemAsync(_lastMarketItem);
    }

    // ================================================================== trying on

    [ObservableProperty] private bool isTryingOn;
    [ObservableProperty] private bool tryOnBusy;
    [ObservableProperty] private string tryOnPicture = "";
    private int _tryOnGeneration;

    private void StopTryOn()
    {
        _tryOnGeneration++;
        IsTryingOn = false;
        TryOnBusy = false;
        TryOnPicture = "";
    }

    /// <summary>Draws the current account wearing its look plus this item, without changing anything on the account.</summary>
    [RelayCommand]
    private async Task TryOn()
    {
        if (IsTryingOn) { StopTryOn(); return; }
        if (OpenMarketItem is not { } item || CurrentAccount is not { } a) return;
        int gen = ++_tryOnGeneration;
        IsTryingOn = true;
        TryOnBusy = true;
        try
        {
            var look = await CurrentLookAsync(a);
            var tried = new AvatarState { Type = look.Type, Scales = look.Scales, Colors = look.Colors, Assets = WithItem(look.Assets, item) };
            var url = await RobloxMarket.RenderLookAsync(a.Cookie, tried, CancellationToken.None);
            if (gen != _tryOnGeneration) return;
            if (url.Length == 0) { StatusMessage = "Roblox took too long to draw the try-on. Try again."; StopTryOn(); return; }
            TryOnPicture = url;
        }
        catch (RobloxException ex)
        {
            if (gen != _tryOnGeneration) return;
            StatusMessage = "Couldn't try it on: " + ex.Message;
            StopTryOn();
        }
        finally { if (gen == _tryOnGeneration) TryOnBusy = false; }
    }

    /// <summary>The account's look: the Avatar page's when it's loaded for this account (it may have unsaved clicks), else fresh.</summary>
    private async Task<AvatarState> CurrentLookAsync(Account a)
    {
        _rules ??= await RobloxAvatar.GetRulesAsync(CancellationToken.None);
        if (AvatarLook != null && _avatarLoadedFor == a) return AvatarLook;
        return await RobloxAvatar.GetAvatarAsync(a.UserId, CancellationToken.None);
    }

    /// <summary>The look's items with this item (or every part of a bundle) put on, by the same rules as the Avatar page.</summary>
    private List<WornAsset> WithItem(List<WornAsset> current, MarketItem item)
    {
        var parts = item.IsBundle
            ? item.Parts.Select(p => (p.Id, p.Type, p.Name)).ToList()
            : new List<(long Id, int Type, string Name)> { (item.Id, item.AssetType, item.Name) };
        var list = current.ToList();
        foreach (var (id, type, name) in parts)
        {
            if (list.Any(w => w.Id == id)) continue;
            if (AccessoryTypes.Contains(type))
            {
                var acc = list.Where(w => AccessoryTypes.Contains(w.TypeId)).ToList();
                while (acc.Count >= MaxAccessories) { list.Remove(acc[0]); acc.RemoveAt(0); }
            }
            else
            {
                int max = _rules != null && _rules.Wearable.TryGetValue(type, out var r) ? Math.Max(1, r.Max) : 1;
                var same = list.Where(w => w.TypeId == type).ToList();
                while (same.Count >= max) { list.Remove(same[0]); same.RemoveAt(0); }
            }
            list.Add(new WornAsset { Id = id, Name = name, TypeId = type });
        }
        return list;
    }

    /// <summary>Owned: put it on the account for real.</summary>
    [RelayCommand]
    private async Task WearMarketItem()
    {
        if (OpenMarketItem is not { IsOwned: true } item || CurrentAccount is not { } a) return;
        try
        {
            var look = await RobloxAvatar.GetAvatarAsync(a.UserId, CancellationToken.None);
            _rules ??= await RobloxAvatar.GetRulesAsync(CancellationToken.None);
            var refused = await RobloxAvatar.SetWearingAsync(a.Cookie, WithItem(look.Assets, item));
            _avatarLoadedAt = DateTime.MinValue;   // the Avatar page reloads next time
            StatusMessage = refused.Count == 0 ? $"{a.Label} is now wearing {item.Name}." : $"Roblox didn't allow {item.Name} with {a.Label}'s look.";
            StopTryOn();
        }
        catch (RobloxException ex) { StatusMessage = "Couldn't put it on: " + ex.Message; }
    }

    // ================================================================== getting and buying

    [ObservableProperty] private bool showBuyConfirm;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanConfirmBuy))] private bool buyBusy;
    [ObservableProperty] private string buyError = "";

    partial void OnShowBuyConfirmChanged(bool value) { BuyError = ""; NotifyBuy(); }

    private void NotifyBuy()
    {
        foreach (var n in new[] { nameof(BuyPrice), nameof(BuyAfterText), nameof(BuyNotEnough), nameof(BuyOverLimit),
                                  nameof(CanConfirmBuy), nameof(BuyButtonText), nameof(MarketItemMeta), nameof(CanGetOrBuy), nameof(MarketAsText) })
            OnPropertyChanged(n);
    }

    /// <summary>"As Main · R$ 1,234. Switch accounts top right."</summary>
    public string MarketAsText => CurrentAccount is not { } a ? "Add an account to get or buy items."
        : $"As {a.Label}{(a.Robux is long r ? $" · R$ {r:N0}" : "")}. Switch accounts top right.";

    public long BuyPrice => OpenMarketItem?.Price ?? 0;
    public string BuyAfterText => CurrentAccount?.Robux is long r
        ? $"R$ {r:N0}  →  R$ {Math.Max(0, r - BuyPrice):N0}"
        : "Unknown (Roblox didn't say)";
    public bool BuyNotEnough => CurrentAccount?.Robux is long r && r < BuyPrice;
    public bool BuyOverLimit => Settings.MarketSpendLimit > 0 && BuyPrice > Settings.MarketSpendLimit;
    public bool CanConfirmBuy => !BuyBusy && !BuyNotEnough && !BuyOverLimit && OpenMarketItem is { ForSale: true, IsOwned: false };

    /// <summary>"Get for free", "Buy for R$ 95", "Owned", "Off sale".</summary>
    public string BuyButtonText => OpenMarketItem switch
    {
        null => "",
        { IsOwned: true } => "Owned",
        { IsFree: true } => "Get for free",
        { Price: long p } => $"Buy for R$ {p:N0}",
        _ => "Not for sale"
    };
    public bool CanGetOrBuy => OpenMarketItem is { ForSale: true, IsOwned: false } && CurrentAccount != null && !BuyBusy;

    /// <summary>Free: get it at once. Robux: open the confirm card first, never buy on one click.</summary>
    [RelayCommand]
    private async Task GetOrBuy()
    {
        if (!CanGetOrBuy || OpenMarketItem is not { } item) return;
        if (item.IsFree) { await DoBuyAsync(item, 0); return; }
        ShowBuyConfirm = true;
    }

    [RelayCommand] private void CancelBuy() => ShowBuyConfirm = false;

    [RelayCommand]
    private async Task ConfirmBuy()
    {
        if (!CanConfirmBuy || OpenMarketItem is not { Price: long price } item) return;
        await DoBuyAsync(item, price);
    }

    private async Task DoBuyAsync(MarketItem item, long price)
    {
        if (CurrentAccount is not { } a) return;
        if (Settings.MarketSpendLimit > 0 && price > Settings.MarketSpendLimit) { BuyError = "This is over your spending limit."; return; }
        BuyBusy = true;
        BuyError = "";
        NotifyBuy();
        try
        {
            var result = await RobloxMarket.BuyAsync(a.Cookie, a.UserId, item, price, CancellationToken.None);
            if (result.Ok)
            {
                item.Owned = true;
                ShowBuyConfirm = false;
                StatusMessage = price == 0 ? $"{a.Label} got {item.Name}." : $"{a.Label} bought {item.Name} for R$ {price:N0}.";
                Log(ActivityLog.Purchase, price == 0 ? $"{a.Label} got {item.Name} (free)." : $"{a.Label} bought {item.Name} for R$ {price:N0}.");
                _avatarLoadedAt = DateTime.MinValue;   // the Avatar page's wardrobe reloads with the new item
                _wardrobeAll = new();
                if (price > 0 && await RobloxApi.GetRobuxAsync(a.Cookie, a.UserId) is long r) a.Robux = r;
            }
            else
            {
                if (result.Message.Contains("already owns")) item.Owned = true;
                if (ShowBuyConfirm) BuyError = result.Message; else StatusMessage = result.Message;
            }
        }
        catch (RobloxException ex)
        {
            var msg = ex.TooMany ? "Roblox is busy. Nothing was bought. Try again in a moment." : ex.Message;
            if (ShowBuyConfirm) BuyError = msg; else StatusMessage = msg;
        }
        finally
        {
            BuyBusy = false;
            NotifyBuy();
        }
    }
}
