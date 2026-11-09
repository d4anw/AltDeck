using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using RoDeck.Controls;
using RoDeck.Models;
using RoDeck.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RoDeck.ViewModels;

/// <summary>
/// Trades → Calculator (4 slots per side, like a trade ad: pick from your inventory or browse every limited),
/// Watchlist (a notification when a limited is listed at or below your price, checked every second), trade profiles
/// (anyone's limiteds, your own too) and the check on open trades that every item is still owned.
/// </summary>
public partial class MainViewModel
{
    // ================================================================== calculator

    public ObservableCollection<AdSlot> CalcGiveSlots { get; } = new(Enumerable.Range(0, 4).Select(i => new AdSlot { Index = i, IsOffer = true, Label = "Add" }));
    public ObservableCollection<AdSlot> CalcGetSlots { get; } = new(Enumerable.Range(0, 4).Select(i => new AdSlot { Index = i, IsOffer = false, Label = "Add" }));
    [ObservableProperty] private long calcGiveRobux, calcGetRobux;
    [ObservableProperty] private string calcGiveValue = "0", calcGiveRap = "0", calcGetValue = "0", calcGetRap = "0";
    [ObservableProperty] private string calcValueDiff = "", calcRapDiff = "", calcVerdict = "Click a slot to add an item.";
    [ObservableProperty] private int calcValueDiffKind, calcRapDiffKind, calcVerdictKind;

    partial void OnCalcGiveRobuxChanged(long value) => UpdateCalc();
    partial void OnCalcGetRobuxChanged(long value) => UpdateCalc();

    // ---- the picker (over the tab, like the trade ad's)

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CalcPickerTitle))] private bool isCalcPickerOpen;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CalcPickerTitle))] private AdSlot? calcPickerSlot;
    /// <summary>"Inventory" (your own limiteds) or "Browse" (every limited).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CalcPickerIsInventory))] private string calcPickerSource = "Browse";
    [ObservableProperty] private string calcPickerSearch = "";
    [ObservableProperty] private bool calcPickerAscending;
    [ObservableProperty] private string calcPickerMin = "", calcPickerMax = "";
    public ObservableCollection<LimitedTile> CalcPickerItems { get; } = new();
    public Loader CalcInventoryLoad { get; } = new();
    public bool CalcPickerIsInventory => CalcPickerSource == "Inventory";
    public string CalcPickerTitle => CalcPickerSlot is not { } s ? "" : $"{(s.IsOffer ? "You give" : "You get")}: slot {s.Index + 1}";
    private List<LimitedTile> _calcInventory = new();
    private Account? _calcInventoryFor;

    partial void OnCalcPickerSourceChanged(string value) { FillCalcPicker(); if (value == "Inventory") _ = LoadCalcInventoryAsync(); }
    partial void OnCalcPickerSearchChanged(string value) => FillCalcPicker();
    partial void OnCalcPickerAscendingChanged(bool value) { OnPropertyChanged(nameof(CalcPickerSortText)); FillCalcPicker(); }
    public string CalcPickerSortText => CalcPickerAscending ? "Lowest first" : "Highest first";
    [RelayCommand] private void ToggleCalcPickerSort() => CalcPickerAscending = !CalcPickerAscending;
    partial void OnCalcPickerMinChanged(string value) => FillCalcPicker();
    partial void OnCalcPickerMaxChanged(string value) => FillCalcPicker();

    private async Task OpenCalculatorAsync()
    {
        try { await LimitedValues.EnsureAsync(CancellationToken.None); } catch { }
        UpdateCalc();
    }

    /// <summary>A filled slot: click empties it. An empty slot: the picker, on your inventory for "You give" and on
    /// every limited for "You get".</summary>
    [RelayCommand]
    private void OpenCalcSlot(AdSlot? slot)
    {
        if (slot == null) return;
        if (slot.Item != null) { slot.Clear(); UpdateCalc(); return; }
        CalcPickerSlot = slot;
        CalcPickerSearch = "";
        CalcPickerSource = slot.IsOffer ? "Inventory" : "Browse";
        IsCalcPickerOpen = true;
        FillCalcPicker();
        if (CalcPickerIsInventory) _ = LoadCalcInventoryAsync();
    }

    [RelayCommand] private void CloseCalcPicker() => IsCalcPickerOpen = false;
    [RelayCommand] private void CalcPickerFrom(string? source) => CalcPickerSource = source == "Inventory" ? "Inventory" : "Browse";

    private async Task LoadCalcInventoryAsync()
    {
        if (CurrentAccount is not { } a || (_calcInventoryFor == a && _calcInventory.Count > 0)) return;
        await CalcInventoryLoad.RunAsync(async ct =>
        {
            await LimitedValues.EnsureAsync(ct);
            var items = await RobloxTrades.GetCollectiblesAsync(a.Cookie, a.UserId, ct);
            _calcInventory = items;
            _calcInventoryFor = a;
            FillCalcPicker();
            return items.Count > 0;
        }, "Loading your limiteds…");
    }

    private void FillCalcPicker()
    {
        if (CalcPickerSlot == null) return;
        var q = (CalcPickerSearch ?? "").Trim();
        long? min = long.TryParse(CalcPickerMin, out var mn) ? mn : null, max = long.TryParse(CalcPickerMax, out var mx) ? mx : null;
        IEnumerable<LimitedTile> source = CalcPickerIsInventory
            ? _calcInventory.GroupBy(t => (t.AssetId, t.IsBundle)).Select(g => g.First())
            : LimitedValues.All.Select(v => LimitedTile.From(v.AssetId, v.Name, v.Rap));
        var list = source.Where(t =>
                (q.Length == 0 || t.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(LimitedValues.Get(t.AssetId)?.Acronym, q, StringComparison.OrdinalIgnoreCase)
                 || t.AssetId.ToString() == q)
                && (min == null || t.Worth >= min) && (max == null || t.Worth <= max));
        list = CalcPickerAscending ? list.OrderBy(t => t.Worth) : list.OrderByDescending(t => t.Worth);
        var shown = list.Take(120).ToList();
        CalcPickerItems.Clear();
        foreach (var t in shown) CalcPickerItems.Add(t);
        _ = FillLimitedThumbsAsync(shown.Where(t => t.ThumbUrl.Length == 0).ToList());
    }

    [RelayCommand]
    private void PickCalcItem(LimitedTile? t)
    {
        if (t == null || CalcPickerSlot is not { } slot) return;
        // a copy of its own: the same item may be in two slots (you can own two)
        var copy = LimitedTile.From(t.AssetId, t.Name, t.Rap, bundle: t.IsBundle);
        copy.ThumbUrl = t.ThumbUrl;
        slot.Item = copy;
        IsCalcPickerOpen = false;
        UpdateCalc();
    }

    [RelayCommand]
    private void ClearCalcSlot()
    {
        CalcPickerSlot?.Clear();
        IsCalcPickerOpen = false;
        UpdateCalc();
    }

    [RelayCommand]
    private void CalcClear()
    {
        foreach (var s in CalcGiveSlots.Concat(CalcGetSlots)) s.Clear();
        CalcGiveRobux = 0; CalcGetRobux = 0;
        UpdateCalc();
    }

    /// <summary>Swaps the sides: the trade from the other person's view.</summary>
    [RelayCommand]
    private void CalcSwap()
    {
        for (int i = 0; i < 4; i++) (CalcGiveSlots[i].Item, CalcGetSlots[i].Item) = (CalcGetSlots[i].Item, CalcGiveSlots[i].Item);
        (CalcGiveRobux, CalcGetRobux) = (CalcGetRobux, CalcGiveRobux);
        UpdateCalc();
    }

    /// <summary>Totals per side and the difference; Robux you get count after Roblox's 30 % fee, as in a real trade.</summary>
    private void UpdateCalc()
    {
        var give = CalcGiveSlots.Where(s => s.Item != null).Select(s => s.Item!).ToList();
        var get = CalcGetSlots.Where(s => s.Item != null).Select(s => s.Item!).ToList();
        long giveRobux = Math.Max(0, CalcGiveRobux), getRobux = (long)(Math.Max(0, CalcGetRobux) * 0.7);
        long giveV = give.Sum(t => t.Worth) + giveRobux, getV = get.Sum(t => t.Worth) + getRobux;
        long giveR = give.Sum(t => t.Rap) + giveRobux, getR = get.Sum(t => t.Rap) + getRobux;
        CalcGiveValue = giveV.ToString("N0"); CalcGetValue = getV.ToString("N0");
        CalcGiveRap = giveR.ToString("N0"); CalcGetRap = getR.ToString("N0");
        CalcValueDiff = Limited.Diff(getV - giveV, giveV);
        CalcRapDiff = Limited.Diff(getR - giveR, giveR);
        CalcValueDiffKind = Math.Sign(getV - giveV);
        CalcRapDiffKind = Math.Sign(getR - giveR);
        (CalcVerdict, CalcVerdictKind) = giveV == 0 && getV == 0 ? ("Click a slot to add an item.", 0)
                                       : giveV == 0 || getV == 0 ? ("Add something to both sides.", 0)
                                       : Verdict(giveV, getV, getR - giveR);
    }

    // ================================================================== watchlist

    public ObservableCollection<WatchRow> WatchRows { get; } = new();
    public ObservableCollection<LimitedTile> WatchHits { get; } = new();
    [ObservableProperty] private string watchSearch = "";
    [ObservableProperty] private string watchStatus = "";
    private readonly DispatcherTimer _watchTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _watchFeedBusy, _watchFullBusy;
    private int _watchTick;
    public bool HasWatchRows => WatchRows.Count > 0;

    /// <summary>Limiteds whose name, acronym or ID matches; the most valuable first.</summary>
    private static List<LimitedTile> FindLimiteds(string? query, int take)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0) return new();
        return LimitedValues.All
            .Where(v => v.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || v.Acronym.Equals(q, StringComparison.OrdinalIgnoreCase) || v.AssetId.ToString() == q)
            .OrderByDescending(v => v.Acronym.Equals(q, StringComparison.OrdinalIgnoreCase)).ThenByDescending(v => v.Worth)
            .Take(take).Select(v => LimitedTile.From(v.AssetId, v.Name, v.Rap)).ToList();
    }

    partial void OnWatchSearchChanged(string value)
    {
        WatchHits.Clear();
        foreach (var t in FindLimiteds(value, 8)) WatchHits.Add(t);
        _ = FillLimitedThumbsAsync(WatchHits.ToList());
    }

    /// <summary>Started once: the saved list, checked every second (even with RoDeck in the tray).</summary>
    private void SetupWatchlist()
    {
        foreach (var e in Settings.Watchlist) AddWatchRow(e.AssetId, e.MaxPrice, e.Name);
        WatchRows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasWatchRows));
        _watchTimer.Tick += (_, _) => WatchTick();
        _watchTimer.Start();
    }

    private WatchRow AddWatchRow(long id, long maxPrice, string name)
    {
        var v = LimitedValues.Get(id);
        var row = new WatchRow { AssetId = id, Name = v?.Name is { Length: > 0 } n ? n : name, Rap = v?.Rap ?? 0, Value = v?.Value ?? -1, MaxPrice = maxPrice };
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(WatchRow.MaxPrice)) return;
            row.NotifiedPrice = 0;
            SaveWatchlist();
            Announce(row);   // already cheap enough for the new price?
        };
        WatchRows.Add(row);
        _ = FillWatchThumbsAsync(new[] { row });
        return row;
    }

    private static async Task FillWatchThumbsAsync(IEnumerable<WatchRow> rows)
    {
        var list = rows.Where(r => r.ThumbUrl.Length == 0).ToList();
        if (list.Count == 0) return;
        var pics = await Thumbnails.GetAsync("Asset", list.Select(r => r.AssetId), "150x150");
        foreach (var r in list) if (pics.TryGetValue(r.AssetId, out var u)) r.ThumbUrl = u;
    }

    [RelayCommand]
    private async Task WatchAdd(LimitedTile? t)
    {
        if (t == null) return;
        if (WatchRows.Any(r => r.AssetId == t.AssetId)) { StatusMessage = $"{t.Name} is already on your watchlist."; return; }
        long start = Math.Max(1, (long)Math.Round(t.Worth * 0.9 / 10.0) * 10);   // 10 % under what it's worth; change it any time
        AddWatchRow(t.AssetId, start, t.Name);
        WatchSearch = "";
        SaveWatchlist();
        await WatchFullCheckAsync();
    }

    [RelayCommand]
    private void WatchRemove(WatchRow? r)
    {
        if (r == null) return;
        WatchRows.Remove(r);
        SaveWatchlist();
    }

    [RelayCommand]
    private Task WatchOpen(WatchRow? r) => r == null ? Task.CompletedTask : OpenLimitedInMarketAsync(r.AssetId, r.Name, buy: false);

    private void SaveWatchlist()
    {
        Settings.Watchlist = WatchRows.Select(r => new WatchEntry { AssetId = r.AssetId, MaxPrice = r.MaxPrice, Name = r.Name }).ToList();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>
    /// Every second: the live listing feed (each new lowest price, a second after it's listed). Every 30 seconds,
    /// and right after a change: the cheapest price of each watched limited straight from Roblox, which also notices
    /// when a cheap copy has been bought.
    /// </summary>
    private void WatchTick()
    {
        if (WatchRows.Count == 0) return;
        _ = WatchFeedAsync();
        if (_watchTick++ % 30 == 0) _ = WatchFullCheckAsync();
    }

    private async Task WatchFeedAsync()
    {
        if (_watchFeedBusy) return;
        _watchFeedBusy = true;
        try
        {
            var fresh = await LimitedValues.GetNewPricesAsync(CancellationToken.None);
            foreach (var (id, price, _) in fresh)
                foreach (var r in WatchRows.Where(r => r.AssetId == id))
                    if (!r.Checked || r.BestPrice == 0 || price < r.BestPrice) { r.BestPrice = price; r.Checked = true; Announce(r); }
            WatchStatus = $"Live · new listings checked every second · last {DateTime.Now:HH:mm:ss}";
        }
        catch (RobloxException) { WatchStatus = "Listings feed busy; trying again…"; }
        catch (System.Text.Json.JsonException) { }
        catch (HttpRequestException) { }
        finally { _watchFeedBusy = false; }
    }

    private async Task WatchFullCheckAsync()
    {
        if (_watchFullBusy || WatchRows.Count == 0) return;
        _watchFullBusy = true;
        try
        {
            try { await LimitedValues.EnsureAsync(CancellationToken.None, maxAge: TimeSpan.FromMinutes(10)); } catch { }
            var rows = WatchRows.ToList();
            var prices = new Dictionary<long, long>();
            foreach (var chunk in rows.Select(r => r.AssetId).Distinct().Chunk(100))
                foreach (var (id, p) in await LimitedValues.GetBestPricesAsync(chunk, null, CancellationToken.None)) prices[id] = p;
            foreach (var r in rows)
            {
                if (LimitedValues.Get(r.AssetId) is { } v) { r.Rap = v.Rap; r.Value = v.Value; }
                r.BestPrice = prices.TryGetValue(r.AssetId, out var p) ? p : 0;
                r.Checked = true;
                Announce(r);
            }
        }
        catch (RobloxException) { }
        catch (System.Text.Json.JsonException) { }
        finally { _watchFullBusy = false; }
    }

    /// <summary>A Windows notification once per new price at or below yours.</summary>
    private void Announce(WatchRow r)
    {
        if (!r.IsHit || r.NotifiedPrice == r.BestPrice) return;
        r.NotifiedPrice = r.BestPrice;
        Notification?.Invoke($"Watchlist: {r.Name} for R$ {r.BestPrice:N0}",
                             $"At or below your price of R$ {r.MaxPrice:N0} (RAP {r.Rap:N0}).", "Watchlist");
    }

    // ================================================================== items that are gone

    [ObservableProperty] private string tradeGoneNote = "";

    /// <summary>
    /// An open trade only goes through if both still have every item. What each side can trade right now is compared
    /// with the copies in the trade; a copy that isn't there anymore is marked, with a note above the trade.
    /// </summary>
    private async Task CheckTradeItemsAsync(Account a, TradeRow r, TradeDetail d)
    {
        try
        {
            var theirs = d.Theirs.Items.Where(i => i.InstanceId.Length > 0).ToList();
            var mine = d.Mine.Items.Where(i => i.InstanceId.Length > 0).ToList();
            var theirTask = theirs.Count > 0 ? RobloxTrades.GetTradableAsync(a.Cookie, r.PartnerId, CancellationToken.None) : null;
            var myTask = mine.Count > 0 ? RobloxTrades.GetTradableAsync(a.Cookie, a.UserId, CancellationToken.None) : null;
            var theirIds = theirTask != null ? (await theirTask).Select(i => i.InstanceId).ToHashSet() : new HashSet<string>();
            var myIds = myTask != null ? (await myTask).Select(i => i.InstanceId).ToHashSet() : new HashSet<string>();
            var theirGone = theirs.Where(i => !theirIds.Contains(i.InstanceId)).ToList();
            var myGone = mine.Where(i => !myIds.Contains(i.InstanceId)).ToList();
            foreach (var i in theirGone.Concat(myGone)) i.IsGone = true;
            r.HasGone = theirGone.Count + myGone.Count > 0;
            if (OpenTradeRow != r) return;
            var notes = new List<string>();
            if (theirGone.Count > 0) notes.Add($"{r.PartnerLabel} doesn't have {Names(theirGone)} anymore.");
            if (myGone.Count > 0) notes.Add($"You don't have {Names(myGone)} anymore.");
            TradeGoneNote = notes.Count == 0 ? "" : string.Join(" ", notes) + " This trade can't go through as it is.";
        }
        catch (RobloxException) { }   // couldn't check (Roblox busy): say nothing rather than guess
    }

    private static string Names(List<LimitedTile> items)
    {
        var names = items.Select(i => i.Name).Distinct().ToList();
        return names.Count switch
        {
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1]
        };
    }

    // ================================================================== trade profiles

    [ObservableProperty, NotifyPropertyChangedFor(nameof(TradeProfileIsMe))] private long tradeProfileId;
    [ObservableProperty] private string tradeProfileName = "", tradeProfileUser = "", tradeProfileHead = "";
    [ObservableProperty] private string tradeProfileValue = "–", tradeProfileRap = "–", tradeProfileCount = "–";
    /// <summary>Limiteds still coming in (they appear one by one), with a line saying how many so far.</summary>
    [ObservableProperty] private bool tradeProfileLoading;
    [ObservableProperty] private string tradeProfileLoadingText = "";
    [ObservableProperty] private string tradeProfileNote = "";
    public ObservableCollection<LimitedTile> TradeProfileItems { get; } = new();
    public bool TradeProfileIsMe => CurrentAccount != null && CurrentAccount.UserId == TradeProfileId;
    private readonly Queue<LimitedTile> _profileQueue = new();
    private readonly DispatcherTimer _profilePop = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private int _profileGen;

    /// <summary>The tab the profile was opened from (its Back button returns there).</summary>
    private string _profileFrom = "";
    public string TradeProfileBackText => _profileFrom switch
    {
        "Trades" => "Back to my trades",
        "Inventory" => "Back to inventory value",
        "Calculator" => "Back to the calculator",
        "Watchlist" => "Back to the watchlist",
        "Deals" => "Back to deals",
        "Ads" => "Back to trade ads",
        _ => "Back to limiteds"
    };

    /// <summary>Someone's trade profile, at once: from a trade (row or open trade), an account, or a user ID.</summary>
    [RelayCommand]
    private void OpenTradeProfile(object? x)
    {
        if (CurrentAccount is not { } a) return;
        var (id, name, user, head) = x switch
        {
            TradeRow r => (r.PartnerId, r.PartnerLabel, r.PartnerName, r.HeadshotUrl),
            Account acc => (acc.UserId, acc.Label, acc.Username, acc.AvatarUrl),
            long l => (l, "", "", ""),
            _ => OpenTradeRow is { } o ? (o.PartnerId, o.PartnerLabel, o.PartnerName, o.HeadshotUrl) : (0L, "", "", "")
        };
        if (id <= 0) return;
        if (TradesTab != "Profile") _profileFrom = TradesTab;
        OnPropertyChanged(nameof(TradeProfileBackText));
        _ = ShowTradeProfileAsync(a, id, name, user, head);   // the page shows right away; the items fill in
        TradesTab = "Profile";
    }

    [RelayCommand]
    private void OpenMyTradeProfile() { if (CurrentAccount is { } a) OpenTradeProfile(a); }

    [RelayCommand]
    private void CloseTradeProfile() => TradesTab = _profileFrom is { Length: > 0 } f && f != "Profile" ? f : "Trades";

    [RelayCommand]
    private void TradeWithProfile()
    {
        if (TradeProfileId <= 0 || TradeProfileIsMe) return;
        _counterOf = null;
        BuildTitle = "New trade";
        StartBuild(TradeProfileId, TradeProfileName, TradeProfileHead);
    }

    [RelayCommand]
    private void OpenProfileOnRoblox() { if (TradeProfileId > 0) Web($"https://www.roblox.com/users/{TradeProfileId}/profile"); }

    [RelayCommand]
    private void OpenProfileOnRolimons() { if (TradeProfileId > 0) Web($"https://www.rolimons.com/player/{TradeProfileId}"); }

    private async Task ShowTradeProfileAsync(Account a, long id, string name, string user, string head)
    {
        bool same = TradeProfileId == id && (TradeProfileItems.Count > 0 || TradeProfileLoading);
        TradeProfileId = id;
        if (name.Length > 0 || user.Length > 0) { TradeProfileName = name.Length > 0 ? name : user; TradeProfileUser = user; }
        if (head.Length > 0) TradeProfileHead = head;
        if (same) return;   // already showing them (back / forward)

        int gen = ++_profileGen;
        if (name.Length == 0 && user.Length == 0) { TradeProfileName = "…"; TradeProfileUser = ""; _ = FillProfileNameAsync(id); }
        if (head.Length == 0) { TradeProfileHead = ""; _ = FillProfileHeadAsync(id); }
        TradeProfileItems.Clear();
        _profileQueue.Clear();
        TradeProfileValue = TradeProfileRap = TradeProfileCount = "–";
        TradeProfileNote = "";
        TradeProfileLoading = true;
        TradeProfileLoadingText = "Loading limiteds…";
        if (!_profilePopSetUp) { _profilePopSetUp = true; _profilePop.Tick += (_, _) => PopProfileItems(); }
        _profilePop.Start();

        List<LimitedTile> items;
        try
        {
            try { await LimitedValues.EnsureAsync(CancellationToken.None); } catch { }
            var sofar = new List<LimitedTile>();
            items = await RobloxTrades.GetCollectiblesAsync(a.Cookie, id, CancellationToken.None, arrived =>
            {
                if (gen != _profileGen) return;
                foreach (var t in arrived) _profileQueue.Enqueue(t);
                sofar.AddRange(arrived);
                // the totals count up as the items come in
                TradeProfileValue = sofar.Sum(i => i.Worth).ToString("N0");
                TradeProfileRap = sofar.Sum(i => i.Rap).ToString("N0");
                TradeProfileCount = sofar.Count.ToString("N0");
                TradeProfileLoadingText = $"Loading limiteds… {sofar.Count} so far · looking for faces and newer limiteds";
                _ = FillLimitedThumbsAsync(arrived.ToList());
            });
        }
        catch (Exception ex)
        {
            if (gen != _profileGen) return;
            TradeProfileLoading = false;
            TradeProfileNote = ex is RobloxException { Status: System.Net.HttpStatusCode.Forbidden } or RobloxException { Status: System.Net.HttpStatusCode.Unauthorized, SignedOut: false }
                ? "Their inventory is private, so their limiteds can't be shown."
                : ex is RobloxException { TooMany: true } ? "Roblox is busy. Open the profile again in a moment."
                : ex is RobloxException re ? re.Message
                : "Roblox didn't answer in time. Open the profile again.";
            return;
        }
        if (gen != _profileGen) return;
        TradeProfileLoading = false;
        TradeProfileValue = items.Sum(i => i.Worth).ToString("N0");
        TradeProfileRap = items.Sum(i => i.Rap).ToString("N0");
        TradeProfileCount = items.Count.ToString("N0");
        if (items.Count == 0) TradeProfileNote = "No limiteds.";
        else _ = RobloxTrades.MarkHoldsAsync(a.Cookie, id, items, CancellationToken.None);   // "On hold" labels follow a moment later
    }

    private bool _profilePopSetUp;

    /// <summary>Puts the items that came in onto the page a few at a time, each in its place (most valuable first).</summary>
    private void PopProfileItems()
    {
        if (_profileQueue.Count == 0) { if (!TradeProfileLoading) _profilePop.Stop(); return; }
        for (int n = 0; n < 3 && _profileQueue.Count > 0; n++)
        {
            var t = _profileQueue.Dequeue();
            int i = 0;
            while (i < TradeProfileItems.Count && TradeProfileItems[i].Worth >= t.Worth) i++;
            TradeProfileItems.Insert(i, t);
        }
    }

    private async Task FillProfileNameAsync(long id)
    {
        try
        {
            using var doc = await RobloxHttp.GetJsonAsync($"https://users.roblox.com/v1/users/{id}", null, CancellationToken.None);
            var r = doc.RootElement;
            string user = r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            string display = r.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "";
            if (TradeProfileId != id) return;
            TradeProfileName = display.Length > 0 ? display : user;
            TradeProfileUser = user;
        }
        catch (RobloxException) { }
    }

    private async Task FillProfileHeadAsync(long id)
    {
        var pics = await Thumbnails.GetAsync("AvatarHeadShot", new[] { id }, "150x150");
        if (pics.TryGetValue(id, out var u) && TradeProfileId == id) TradeProfileHead = u;
    }
}
