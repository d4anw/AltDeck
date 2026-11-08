using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AltDeck.Controls;
using AltDeck.Models;
using AltDeck.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AltDeck.ViewModels;

/// <summary>
/// Trades page, part 1: Deals (live listings below value / RAP), Limiteds (every limited with RAP, value, demand) and
/// Inventory (what each account's limiteds are worth). Trades themselves are in MainViewModel.TradeList.cs.
/// </summary>
public partial class MainViewModel
{
    /// <summary>"Deals", "Limiteds", "Inventory" or "Trades".</summary>
    [ObservableProperty] private string tradesTab = "Deals";
    [ObservableProperty] private string valuesNote = "";

    partial void OnTradesTabChanged(string value) { MaybeLoadTrades(); RecordNav(); }
    partial void OnOpenLimitedChanged(LimitedTile? value) { if (Page == "Trades") RecordNav(); }

    private void MaybeLoadTrades()
    {
        bool deals = Page == "Trades" && TradesTab == "Deals";
        if (deals) StartDeals(); else _dealTimer.Stop();
        if (Page != "Trades") return;
        _ = LoadTradesTabAsync();
    }

    private async Task LoadTradesTabAsync()
    {
        try { await LimitedValues.EnsureAsync(CancellationToken.None); } catch { }
        ValuesNote = LimitedValues.Count == 0 ? "Values couldn't be loaded from Rolimons. RAP is used instead."
            : $"Values from Rolimons, updated {Ago(LimitedValues.LoadedAt)}.";
        switch (TradesTab)
        {
            case "Limiteds": if (_limitedsAll.Count == 0) BuildLimiteds(); break;
            case "Inventory": if (AccountWorths.Count == 0) _ = LoadWorthsAsync(); break;
            case "Trades": MaybeLoadTradeList(); break;
        }
    }

    private static string Ago(DateTime utc)
    {
        var s = (DateTime.UtcNow - utc).TotalSeconds;
        return s < 90 ? "just now" : s < 3600 ? $"{(int)(s / 60)} min ago" : s < 86400 ? $"{(int)(s / 3600)} h ago" : utc.ToLocalTime().ToString("d MMM");
    }

    // ================================================================== deals

    public ObservableCollection<DealTile> Deals { get; } = new();
    /// <summary>The latest known best price per limited: from the live feed (new listings) and the full scan.</summary>
    private readonly Dictionary<long, DealTile> _allDeals = new();
    private readonly HashSet<string> _seenDeals = new();
    public IReadOnlyList<MarketOption> DealSorts { get; } = new List<MarketOption> { new("Biggest discount", 0), new("Newest", 1), new("Biggest saving", 2) };
    [ObservableProperty] private MarketOption? dealSort;
    partial void OnDealSortChanged(MarketOption? value) => FilterDeals();

    // the full scan: every limited's best price, a batch at a time, again every few minutes while Deals is open
    private bool _scanRunning;
    private int _scanDone, _scanTotal;
    private DateTime _scanFinishedAt;
    private readonly DispatcherTimer _dealTimer = new() { Interval = TimeSpan.FromSeconds(1) };   // new listings, every second
    private DateTime _dealPauseUntil;
    private bool _dealTimerHooked, _dealPolling;

    public IReadOnlyList<MarketOption> DealThresholds { get; } = new List<MarketOption>
    {
        new("Every new listing", 0), new("10 % below or more", 10), new("20 % below or more", 20),
        new("30 % below or more", 30), new("50 % below or more", 50)
    };
    [ObservableProperty] private MarketOption? dealThreshold;
    [ObservableProperty] private bool dealsHideProjected = true;
    [ObservableProperty] private bool dealsAffordable;
    [ObservableProperty] private bool dealsValueOnly;
    /// <summary>"Value" (value, or RAP when there's none) or "RAP": what a deal is measured against.</summary>
    [ObservableProperty] private string dealBasis = "Value";
    partial void OnDealBasisChanged(string value) => FilterDeals();
    [ObservableProperty] private string dealsStatus = "Watching the market…";
    private DateTime _dealsCheckedAt;
    private int _dealErrors;

    partial void OnDealThresholdChanged(MarketOption? value) => FilterDeals();
    partial void OnDealsHideProjectedChanged(bool value) => FilterDeals();
    partial void OnDealsAffordableChanged(bool value) => FilterDeals();
    partial void OnDealsValueOnlyChanged(bool value) => FilterDeals();

    private void StartDeals()
    {
        DealThreshold ??= DealThresholds[1];
        DealSort ??= DealSorts[1];   // newest first
        if (!_dealTimerHooked) { _dealTimer.Tick += async (_, _) => { await PollDealsAsync(); MaybeScan(); }; _dealTimerHooked = true; }
        if (!_dealTimer.IsEnabled) { _dealTimer.Start(); _ = PollDealsAsync(); MaybeScan(); }
    }

    private void MaybeScan()
    {
        if (_scanRunning || DateTime.Now - _scanFinishedAt < TimeSpan.FromMinutes(2)) return;
        _ = ScanAllAsync();
    }

    /// <summary>Checks the best price of every limited (most valuable first), adding each batch's deals as it comes in.</summary>
    private async Task ScanAllAsync()
    {
        _scanRunning = true;
        try
        {
            await LimitedValues.EnsureAsync(CancellationToken.None);
            var ids = LimitedValues.All.Where(v => v.Worth > 0).OrderByDescending(v => v.Worth).Select(v => v.AssetId).ToList();
            _scanTotal = ids.Count;
            _scanDone = 0;
            foreach (var chunk in ids.Chunk(100))
            {
                if (!_dealTimer.IsEnabled) return;   // left the Deals tab: stop, carry on next time
                Dictionary<long, long> prices;
                try { prices = await LimitedValues.GetBestPricesAsync(chunk, CurrentAccount?.Cookie, CancellationToken.None); }
                catch (RobloxException ex) when (ex.TooMany) { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(ex.WaitSeconds, 5, 60))); continue; }
                catch (RobloxException) { continue; }
                var now = DateTime.Now;
                var added = new List<DealTile>();
                foreach (var (id, price) in prices)
                    if (AddDeal(id, price, now, scanned: true) is { } d) added.Add(d);
                foreach (var id in chunk) if (!prices.ContainsKey(id)) _allDeals.Remove(id);   // nobody sells it anymore
                _scanDone += chunk.Length;
                await FillDealThumbsAsync(added);
                FilterDeals();
            }
            _scanFinishedAt = DateTime.Now;
        }
        finally { _scanRunning = false; FilterDeals(); }
    }

    /// <summary>Records a best price; returns the new tile when it changed (null when it's the same as before).</summary>
    private DealTile? AddDeal(long id, long price, DateTime at, bool scanned)
    {
        var v = LimitedValues.Get(id);
        if (v == null || v.Worth <= 0) return null;
        if (_allDeals.TryGetValue(id, out var old) && old.Price == price) return null;
        var d = new DealTile
        {
            AssetId = id, Name = v.Name, Price = price, Rap = v.Rap, Value = v.Value, Demand = v.Demand, Projected = v.Projected,
            // a scan only knows the price is this low now, not when it was listed: keep the older time if the price only went up
            At = scanned && old != null && price >= old.Price ? old.At : at,
            Listed = !scanned || (old?.Listed == true && price >= old.Price),
            ByRap = DealBasis == "RAP",
            ThumbUrl = old?.ThumbUrl ?? ""
        };
        _allDeals[id] = d;
        return d;
    }

    private static async Task FillDealThumbsAsync(List<DealTile> tiles)
    {
        var missing = tiles.Where(t => t.ThumbUrl.Length == 0).ToList();
        if (missing.Count == 0) return;
        var pics = await Thumbnails.GetAsync("Asset", missing.Select(d => d.AssetId), "150x150");
        foreach (var d in missing) if (pics.TryGetValue(d.AssetId, out var u)) d.ThumbUrl = u;
    }

    /// <summary>Asks for the newest listings (every 12 s while Deals is open) and adds the new ones to the top.</summary>
    private async Task PollDealsAsync()
    {
        if (_dealPolling || DateTime.Now < _dealPauseUntil) return;
        _dealPolling = true;
        try
        {
            // deals are only as right as the RAP / value they're measured with: keep those at most a minute old here
            await LimitedValues.EnsureAsync(CancellationToken.None, maxAge: TimeSpan.FromMinutes(1));
            var fresh = await LimitedValues.GetNewPricesAsync(CancellationToken.None);
            var added = new List<DealTile>();
            foreach (var (id, price, at) in fresh.OrderBy(f => f.At))
            {
                if (!_seenDeals.Add($"{id}:{price}:{at.Ticks}")) continue;
                if (AddDeal(id, price, at, scanned: false) is { } d) added.Add(d);
            }
            if (_seenDeals.Count > 4000) _seenDeals.Clear();
            _dealErrors = 0;
            _dealsCheckedAt = DateTime.Now;
            await FillDealThumbsAsync(added);
            FilterDeals();
        }
        catch (Exception ex) when (ex is RobloxException or System.Text.Json.JsonException)
        {
            _dealErrors++;
            // something's wrong on their side: ask less often for a bit instead of every second
            _dealPauseUntil = DateTime.Now.AddSeconds(Math.Min(60, 5 * _dealErrors));
            DealsStatus = _dealErrors > 2 ? "Rolimons isn't answering right now. Still trying…" : DealsStatus;
        }
        finally { _dealPolling = false; }
    }

    private bool PassesDeal(DealTile d)
    {
        int min = DealThreshold?.Value ?? 20;
        if (DealsHideProjected && d.Projected && d.Basis == "RAP") return false;   // a pushed-up RAP makes fake "deals"
        if (DealsValueOnly && !d.HasValue) return false;
        if (DealsAffordable && CurrentAccount?.Robux is long r && d.Price > r) return false;
        return min == 0 || d.Discount * 100 >= min;
    }

    private void FilterDeals()
    {
        // measure every deal against the newest RAP / value (they move all the time), not the ones from when it was found
        foreach (var (id, d) in _allDeals.ToList())
        {
            var v = LimitedValues.Get(id);
            if (v == null || (v.Rap == d.Rap && v.Value == d.Value && v.Projected == d.Projected && d.ByRap == (DealBasis == "RAP"))) continue;
            _allDeals[id] = new DealTile
            {
                AssetId = id, Name = d.Name, Price = d.Price, Rap = v.Rap, Value = v.Value, Demand = v.Demand,
                Projected = v.Projected, At = d.At, Listed = d.Listed, ThumbUrl = d.ThumbUrl, ByRap = DealBasis == "RAP"
            };
        }
        foreach (var d in _allDeals.Values) d.Tick();
        IEnumerable<DealTile> list = _allDeals.Values.Where(PassesDeal);
        list = (DealSort?.Value ?? 0) switch
        {
            // real listings by when they were listed (like Rolimons); deals the price check found have no listing time, so they go after
            1 => list.OrderByDescending(d => d.Listed).ThenByDescending(d => d.Listed ? d.At.Ticks : 0).ThenByDescending(d => d.Discount),
            2 => list.OrderByDescending(d => d.Saving),
            _ => list.OrderByDescending(d => d.Discount)
        };
        var shown = list.Take(150).ToList();
        if (!shown.SequenceEqual(Deals))
        {
            Deals.Clear();
            foreach (var d in shown) Deals.Add(d);
        }
        var scan = _scanRunning ? $"checking every limited's price: {_scanDone:N0} of {_scanTotal:N0}"
                 : _scanFinishedAt == default ? "starting" : $"all {_scanTotal:N0} limiteds checked at {_scanFinishedAt:HH:mm}";
        DealsStatus = $"Live · new listings checked {(_dealsCheckedAt == default ? "…" : _dealsCheckedAt.ToString("HH:mm:ss"))} · {scan} · {Deals.Count} shown";
    }

    /// <summary>Opens the limited in the Marketplace with its sellers, and the cheapest copy's confirm card.</summary>
    [RelayCommand]
    private Task BuyDeal(DealTile? d) => d == null ? Task.CompletedTask : OpenLimitedInMarketAsync(d.AssetId, d.Name, buy: true);

    // ================================================================== limiteds catalog

    private List<LimitedTile> _limitedsAll = new();
    private List<LimitedTile> _limitedsFiltered = new();
    public ObservableCollection<LimitedTile> LimitedsShown { get; } = new();
    [ObservableProperty] private string limitedsSearch = "";
    [ObservableProperty] private bool limitedsHasMore;
    public IReadOnlyList<MarketOption> LimitedSorts { get; } = new List<MarketOption>
    {
        new("Value: high to low", 0), new("RAP: high to low", 1), new("Demand", 2), new("Value: low to high", 3), new("Name", 4)
    };
    [ObservableProperty] private MarketOption? limitedsSort;

    partial void OnLimitedsSortChanged(MarketOption? value) => FilterLimiteds();

    private void BuildLimiteds()
    {
        LimitedsSort ??= LimitedSorts[0];
        _limitedsAll = LimitedValues.All.Select(v => LimitedTile.From(v.AssetId, v.Name, v.Rap)).ToList();
        FilterLimiteds();
    }

    [RelayCommand]
    private void SearchLimiteds() => FilterLimiteds();

    private void FilterLimiteds()
    {
        var q = (LimitedsSearch ?? "").Trim();
        IEnumerable<LimitedTile> list = _limitedsAll;
        if (q.Length > 0)
            list = list.Where(t => t.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(LimitedValues.Get(t.AssetId)?.Acronym, q, StringComparison.OrdinalIgnoreCase));
        list = (LimitedsSort?.Value ?? 0) switch
        {
            1 => list.OrderByDescending(t => t.Rap),
            2 => list.OrderByDescending(t => t.Demand).ThenByDescending(t => t.Worth),
            3 => list.OrderBy(t => t.Worth),
            4 => list.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase),
            _ => list.OrderByDescending(t => t.Worth)
        };
        _limitedsFiltered = list.ToList();
        LimitedsShown.Clear();
        ShowMoreLimiteds();
    }

    [RelayCommand]
    private void ShowMoreLimiteds()
    {
        var next = _limitedsFiltered.Skip(LimitedsShown.Count).Take(60).ToList();
        foreach (var t in next) LimitedsShown.Add(t);
        LimitedsHasMore = LimitedsShown.Count < _limitedsFiltered.Count;
        _ = FillLimitedThumbsAsync(next);
    }

    private static async Task FillLimitedThumbsAsync(IList<LimitedTile> tiles)
    {
        var pics = await Thumbnails.GetAsync("Asset", tiles.Select(t => t.AssetId), "150x150");
        foreach (var t in tiles) if (pics.TryGetValue(t.AssetId, out var u)) t.ThumbUrl = u;
    }

    // ---- one limited

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsLimitedOpen), nameof(OpenLimitedInfo))] private LimitedTile? openLimited;
    public bool IsLimitedOpen => OpenLimited != null;
    public LimitedInfo? OpenLimitedInfo => OpenLimited != null ? LimitedValues.Get(OpenLimited.AssetId) : null;
    public Loader LimitedLoad { get; } = new();
    [ObservableProperty] private ItemStats? openStats;
    [ObservableProperty] private string openLimitedPicture = "";
    [ObservableProperty] private PointCollection rapChart = new();
    [ObservableProperty] private string rapChartRange = "";

    [RelayCommand]
    private async Task OpenLimitedTile(LimitedTile? t)
    {
        if (t == null) return;
        OpenLimited = t;
        OpenStats = null;
        RapChart = new PointCollection();
        RapChartRange = "";
        OpenLimitedPicture = t.ThumbUrl;
        await LimitedLoad.RunAsync(async ct =>
        {
            var pic = Thumbnails.GetAsync("Asset", new[] { t.AssetId }, "420x420", ct);
            var stats = await RobloxTrades.GetItemStatsAsync(t.AssetId, ct);
            if (OpenLimited != t) return true;
            OpenStats = stats;
            DrawRapChart(stats.History);
            if ((await pic).TryGetValue(t.AssetId, out var u)) OpenLimitedPicture = u;
            return true;
        }, "Loading sales…");
    }

    /// <summary>Back from an item's page: to the trade / inventory it was opened from, or to the list of limiteds.</summary>
    [RelayCommand]
    private void CloseLimited()
    {
        OpenLimited = null;
        if (_limitedFrom.Length > 0) TradesTab = _limitedFrom;
        _limitedFrom = "";
        OnPropertyChanged(nameof(LimitedBackText));
    }

    /// <summary>Which tab the open item page goes back to ("" = the Limiteds list).</summary>
    private string _limitedFrom = "";
    public string LimitedBackText => _limitedFrom switch
    {
        "Trades" => "Back to the trade",
        "Inventory" => "Back to inventory value",
        _ => "Back to limiteds"
    };

    /// <summary>A limited in an inventory or a trade: its item page on the Limiteds tab, with Back returning there.</summary>
    [RelayCommand]
    private Task OpenLimitedFromInventory(LimitedTile? t)
    {
        if (t == null) return Task.CompletedTask;
        _limitedFrom = TradesTab == "Limiteds" ? "" : TradesTab;
        OnPropertyChanged(nameof(LimitedBackText));
        TradesTab = "Limiteds";   // the tab switch and the item page become one step in the history
        return OpenLimitedTile(t);
    }

    /// <summary>RAP over time as a line in a 600 × 140 box.</summary>
    private void DrawRapChart(List<(DateTime Day, long Price)> history)
    {
        var since = DateTime.UtcNow.AddDays(-365);
        var pts = history.Where(p => p.Price > 0 && p.Day >= since).ToList();
        if (pts.Count < 2) pts = history.Where(p => p.Price > 0).TakeLast(60).ToList();
        if (pts.Count < 2) { RapChart = new PointCollection(); RapChartRange = ""; return; }
        long lo = pts.Min(p => p.Price), hi = pts.Max(p => p.Price);
        if (hi == lo) hi = lo + 1;
        var pc = new PointCollection();
        for (int i = 0; i < pts.Count; i++)
            pc.Add(new Point(i * 600.0 / (pts.Count - 1), 136 - (pts[i].Price - lo) * 132.0 / (hi - lo)));
        pc.Freeze();
        RapChart = pc;
        RapChartRange = $"{pts[0].Day:d MMM yyyy} – {pts[^1].Day:d MMM yyyy} · low R$ {lo:N0} · high R$ {hi:N0}";
    }

    [RelayCommand]
    private Task BuyOpenLimited() => OpenLimited is { } t ? OpenLimitedInMarketAsync(t.AssetId, t.Name, buy: false) : Task.CompletedTask;

    [RelayCommand]
    private void OpenLimitedOnRolimons()
    {
        if (OpenLimited is { } t) Web($"https://www.rolimons.com/item/{t.AssetId}");
    }

    private static void Web(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    /// <summary>Shows a limited on the Marketplace page (sellers list). With <paramref name="buy"/>, the cheapest copy's confirm card opens too.</summary>
    private async Task OpenLimitedInMarketAsync(long assetId, string name, bool buy)
    {
        _autoBuyFor = buy ? assetId : 0;
        if (MarketCats.Count == 0 && MarketLoad.State != LoadState.Loading) _ = LoadMarketAsync();
        Page = "Marketplace";
        await OpenMarketItemAsync(new MarketItem { Id = assetId, ItemType = "Asset", Name = name, IsLimited = true });
    }

    // ================================================================== inventory value per account

    public ObservableCollection<AccountWorth> AccountWorths { get; } = new();
    public ObservableCollection<LimitedTile> WorthItems { get; } = new();
    [ObservableProperty] private string worthTotal = "";
    [ObservableProperty] private bool worthsLoading;
    [ObservableProperty] private AccountWorth? selectedWorth;

    [RelayCommand]
    private async Task LoadWorthsAsync()
    {
        if (WorthsLoading) return;
        WorthsLoading = true;
        try
        {
            await LimitedValues.EnsureAsync(CancellationToken.None);
            var keep = SelectedWorth?.Account;
            AccountWorths.Clear();
            foreach (var a in Accounts.Where(a => a.UserId > 0)) AccountWorths.Add(new AccountWorth { Account = a, Loading = true });
            UpdateWorthTotal();
            foreach (var w in AccountWorths.ToList())
            {
                try
                {
                    var items = await RobloxTrades.GetCollectiblesAsync(w.Account.Cookie, w.Account.UserId, CancellationToken.None);
                    w.Items = items.OrderByDescending(i => i.Worth).ToList();
                    w.Count = items.Count;
                    w.Rap = items.Sum(i => i.Rap);
                    w.Value = items.Sum(i => i.Worth);
                    w.Note = items.Count == 0 ? "No limiteds" : $"{items.Count} limited{(items.Count == 1 ? "" : "s")}";
                }
                catch (RobloxException ex) { w.Note = ex.SignedOut ? "Signed out" : ex.TooMany ? "Roblox is busy, refresh in a moment" : ex.Message; }
                finally { w.Loading = false; }
                UpdateWorthTotal();
            }
            // richest first
            var sorted = AccountWorths.OrderByDescending(w => w.Value).ToList();
            AccountWorths.Clear();
            foreach (var w in sorted) AccountWorths.Add(w);
            SelectWorth(AccountWorths.FirstOrDefault(w => w.Account == keep) ?? AccountWorths.FirstOrDefault(w => w.Count > 0));
        }
        finally { WorthsLoading = false; }
    }

    private void UpdateWorthTotal()
    {
        var done = AccountWorths.Where(w => !w.Loading).ToList();
        WorthTotal = $"R$ {done.Sum(w => w.Value):N0} value · R$ {done.Sum(w => w.Rap):N0} RAP · {done.Sum(w => w.Count):N0} limiteds"
                   + (done.Count < AccountWorths.Count ? $" · checking {AccountWorths.Count - done.Count} more…" : "");
    }

    [RelayCommand]
    private void SelectWorth(AccountWorth? w)
    {
        foreach (var x in AccountWorths) x.IsSelected = x == w;
        SelectedWorth = w;
        WorthItems.Clear();
        if (w == null) return;
        foreach (var i in w.Items) WorthItems.Add(i);
        _ = FillLimitedThumbsAsync(w.Items.Where(i => i.ThumbUrl.Length == 0).ToList());
    }
}
