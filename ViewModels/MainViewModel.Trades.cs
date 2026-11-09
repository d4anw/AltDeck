using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using RoDeck.Controls;
using RoDeck.Models;
using RoDeck.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RoDeck.ViewModels;

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
            // built again when Rolimons' values were refreshed since, so RAP and values in the table are current
            case "Limiteds": if (_limitedRowsAll.Count == 0 || _limitedRowsFrom != LimitedValues.LoadedAt) BuildLimiteds(); else _ = LoadLimitedStatsAsync(); break;
            case "Inventory": if (AccountWorths.Count == 0) _ = LoadWorthsAsync(); break;
            case "Trades": MaybeLoadTradeList(); break;
            case "Ads": LoadTradeAdTab(); break;
            case "Calculator": _ = OpenCalculatorAsync(); break;
            case "Watchlist": _ = WatchFullCheckAsync(); break;
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
    [ObservableProperty] private string dealBasis = "RAP";
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
        DealSort ??= DealSorts[1];   // newest first, like Rolimons' deals
        if (!_dealTimerHooked) { _dealTimer.Tick += (_, _) => { MaybeScan(); FilterDeals(); }; _dealTimerHooked = true; }
        if (!_dealTimer.IsEnabled) { _dealTimer.Start(); MaybeScan(); FilterDeals(); _ = FillDealThumbsAsync(_allDeals.Values.ToList()); }
    }

    // ---- new listings, collected from the moment RoDeck starts (Rolimons' feed only goes back about two minutes),
    // and remembered on disk, so the list is full when you open Deals, also after a restart
    private readonly DispatcherTimer _dealFeedTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private static string DealsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoDeck", "cache", "deals.json");
    private DateTime _dealsSavedAt;
    private bool _dealsChanged;
    private static readonly TimeSpan KeepListings = TimeSpan.FromHours(12);

    private void SetupDealFeed()
    {
        LoadSavedDeals();
        _dealFeedTimer.Tick += async (_, _) => await Quietly("Deals", PollDealsAsync);
        _dealFeedTimer.Start();
    }

    private sealed record SavedDeal(long Id, long Price, DateTime At);

    private void LoadSavedDeals()
    {
        try
        {
            if (!File.Exists(DealsFile)) return;
            var list = System.Text.Json.JsonSerializer.Deserialize<List<SavedDeal>>(File.ReadAllText(DealsFile)) ?? new();
            foreach (var d in list.Where(d => DateTime.Now - d.At < KeepListings))
                AddDeal(d.Id, d.Price, d.At, scanned: false);
        }
        catch { }
    }

    private void SaveDealsSoon()
    {
        _dealsChanged = true;
        if (DateTime.Now - _dealsSavedAt < TimeSpan.FromSeconds(30)) return;
        SaveDealsNow();
    }

    private void SaveDealsNow()
    {
        if (!_dealsChanged) return;
        _dealsChanged = false;
        _dealsSavedAt = DateTime.Now;
        try
        {
            var list = _allDeals.Values.Where(d => d.Listed && DateTime.Now - d.At < KeepListings).Select(d => new SavedDeal(d.AssetId, d.Price, d.At)).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(DealsFile)!);
            File.WriteAllText(DealsFile, System.Text.Json.JsonSerializer.Serialize(list));
        }
        catch { }
    }

    // ---- the full scan: every limited's cheapest price, non-stop while Deals is open
    // Two Roblox services at once, each within its own allowance: the batch item details (100 limiteds a request,
    // about 10 requests a minute) and the per-item details (about 1,000 a minute; Deals uses 12 a second of that, so the
    // watchlist and item pages keep theirs). Together every limited is checked again about every minute and a half,
    // the most valuable first; new listings come in every second from the live feed on top of that.
    private readonly Queue<long> _scanQueue = new();
    private DateTime _scanPassStarted;
    private TimeSpan _scanPassTook;
    private bool _batchScanBusy, _itemScanBusy;
    private const int ItemChecksPerSecond = 12;

    private void MaybeScan()
    {
        if (!_dealTimer.IsEnabled) return;
        if (!_batchScanBusy) _ = BatchScanAsync();
        if (!_itemScanBusy) _ = ItemScanAsync();
    }

    /// <summary>The next limiteds to check; a new pass starts (most valuable first) when the last one is done.</summary>
    private List<long> TakeScanIds(int n)
    {
        if (_scanQueue.Count == 0)
        {
            if (_scanPassStarted != default) { _scanFinishedAt = DateTime.Now; _scanPassTook = DateTime.Now - _scanPassStarted; }
            var ids = LimitedValues.All.Where(v => v.Worth > 0).OrderByDescending(v => v.Worth).Select(v => v.AssetId).ToList();
            foreach (var id in ids) _scanQueue.Enqueue(id);
            _scanTotal = ids.Count;
            _scanDone = 0;
            _scanPassStarted = DateTime.Now;
        }
        var take = new List<long>();
        while (take.Count < n && _scanQueue.Count > 0) take.Add(_scanQueue.Dequeue());
        return take;
    }

    /// <param name="fresh">From the per-item service, which is up to date. The batch service lags behind new listings,
    /// so it never changes a deal that has a listing time.</param>
    private void ScanResult(long id, long price, DateTime now, List<DealTile> added, bool fresh)
    {
        _scanDone++;
        if (!_allDeals.TryGetValue(id, out var known))
        {
            // a deal that was there before RoDeck was watching: shown too, after the ones with a listing time
            if (price > 0 && AddDeal(id, price, now, scanned: true) is { } d) added.Add(d);
            return;
        }
        // Roblox's price lists lag a little behind new listings: a listing gets two minutes before a check may change it
        if (known.Listed && (!fresh || now - known.At < TimeSpan.FromMinutes(2))) return;
        // that copy was bought: nobody sells it anymore (Roblox said so, not a missing answer)
        if (price == 0) { _allDeals.Remove(id); _dealsChanged = true; return; }
        if (price == known.Price) return;
        // another price now: a listing whose copy was bought goes back to "time unknown" at the new price;
        // a cheaper copy than the one we know was listed while we weren't looking (time unknown too)
        if (AddDeal(id, price, price < known.Price ? now : known.At, scanned: true) is { } changed) added.Add(changed);
        if (known.Listed) _dealsChanged = true;
    }

    /// <summary>100 at a time through the batch service, as often as Roblox allows.</summary>
    private async Task BatchScanAsync()
    {
        _batchScanBusy = true;
        try
        {
            await LimitedValues.EnsureAsync(CancellationToken.None);
            while (_dealTimer.IsEnabled)
            {
                var chunk = TakeScanIds(100);
                if (chunk.Count == 0) { await Task.Delay(1000); continue; }
                Dictionary<long, long> prices;
                try { prices = await LimitedValues.GetBestPricesAsync(chunk, CurrentAccount?.Cookie, CancellationToken.None); }
                catch (Exception ex) when (ex is RobloxException or System.Text.Json.JsonException)
                {
                    // not checked: back in line for the per-item checks; wait as long as Roblox asked
                    foreach (var id in chunk) _scanQueue.Enqueue(id);
                    await Task.Delay(TimeSpan.FromSeconds(ex is RobloxException { TooMany: true } re ? Math.Clamp(re.WaitSeconds, 5, 60) : 10));
                    continue;
                }
                var now = DateTime.Now;
                var added = new List<DealTile>();
                foreach (var id in chunk)
                {
                    if (prices.TryGetValue(id, out var p)) ScanResult(id, p, now, added, fresh: false);
                    else _scanQueue.Enqueue(id);   // Roblox left it out of its answer: unknown, check it again
                }
                if (prices.Count == 0) await Task.Delay(5000);   // an empty answer: Roblox is busy, give it a moment
                await FillDealThumbsAsync(added);
                FilterDeals();
            }
        }
        finally { _batchScanBusy = false; }
    }

    /// <summary>Every second, the next few limiteds one by one through the per-item service (its own allowance).</summary>
    private async Task ItemScanAsync()
    {
        _itemScanBusy = true;
        try
        {
            await LimitedValues.EnsureAsync(CancellationToken.None);
            while (_dealTimer.IsEnabled)
            {
                var started = DateTime.Now;
                var ids = TakeScanIds(ItemChecksPerSecond);
                var results = await Task.WhenAll(ids.Select(async id =>
                {
                    try { return (id, await LimitedValues.GetBestPriceAsync(id, CancellationToken.None), true); }
                    catch (Exception ex) when (ex is RobloxException or System.Text.Json.JsonException) { return (id, 0L, false); }
                }));
                var now = DateTime.Now;
                var added = new List<DealTile>();
                foreach (var (id, price, ok) in results)
                {
                    if (ok && price >= 0) ScanResult(id, price, now, added, fresh: true);
                    else if (!ok) _scanQueue.Enqueue(id);   // try it again later in this pass
                    else _scanDone++;                       // Roblox didn't say: leave the deal as it is
                }
                if (added.Count > 0) { await FillDealThumbsAsync(added); FilterDeals(); }
                var rest = TimeSpan.FromSeconds(1) - (DateTime.Now - started);
                if (results.Any(r => !r.Item3)) rest = TimeSpan.FromSeconds(10);   // Roblox said slow down: give it a moment
                if (rest > TimeSpan.Zero) await Task.Delay(rest);
            }
        }
        finally { _itemScanBusy = false; }
    }

    /// <summary>Records a best price; returns the new tile when it changed (null when it's the same as before).</summary>
    private DealTile? AddDeal(long id, long price, DateTime at, bool scanned)
    {
        var v = LimitedValues.Get(id);
        if (v == null || v.Worth <= 0) return null;
        // the same price again: nothing new, unless the feed now tells when a deal the price check found was listed
        if (_allDeals.TryGetValue(id, out var old) && old.Price == price && (scanned || old.Listed)) return null;
        var d = new DealTile
        {
            AssetId = id, Name = v.Name, Price = price, Rap = v.Rap, Value = v.Value, Demand = v.Demand, Projected = v.Projected,
            At = at,
            // from the live feed: its listing time; from the price check: a deal that's there, listing time unknown
            Listed = !scanned,
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
            foreach (var old in _allDeals.Where(kv => DateTime.Now - kv.Value.At > KeepListings).Select(kv => kv.Key).ToList())
                _allDeals.Remove(old);   // older than half a day: off the list
            _dealErrors = 0;
            _dealsCheckedAt = DateTime.Now;
            if (added.Count > 0) SaveDealsSoon();
            else if (_dealsChanged && DateTime.Now - _dealsSavedAt > TimeSpan.FromSeconds(30)) SaveDealsNow();
            // pictures and the list only matter while Deals is on screen (they're fetched when it opens otherwise)
            if (_dealTimer.IsEnabled) { await FillDealThumbsAsync(added); FilterDeals(); }
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
            // newest first: when it was listed, or (listing time unknown) when RoDeck first found it, so new deals are on top
            1 => list.OrderByDescending(d => d.At).ThenByDescending(d => d.Discount),
            2 => list.OrderByDescending(d => d.Saving),
            _ => list.OrderByDescending(d => d.Discount)
        };
        var shown = list.Take(150).ToList();
        if (!shown.SequenceEqual(Deals))
        {
            Deals.Clear();
            foreach (var d in shown) Deals.Add(d);
        }
        var scan = _scanTotal == 0 ? "starting the price check"
                 : $"every limited's price: {Math.Min(_scanDone, _scanTotal):N0} of {_scanTotal:N0} this round"
                   + (_scanPassTook > TimeSpan.Zero ? $" (a full round takes about {Math.Max(1, (int)Math.Round(_scanPassTook.TotalSeconds)):N0} s)" : "");
        DealsStatus = $"Live · new listings checked {(_dealsCheckedAt == default ? "…" : _dealsCheckedAt.ToString("HH:mm:ss"))} · {scan} · {Deals.Count} shown";
    }


    // ================================================================== limiteds catalog (a table, like Rolimons' item list)

    private List<LimitedRow> _limitedRowsAll = new();
    private List<LimitedRow> _limitedRowsFiltered = new();
    public ObservableCollection<LimitedRow> LimitedRows { get; } = new();
    [ObservableProperty] private string limitedsSearch = "";
    [ObservableProperty] private bool limitedsValuedOnly, limitedsHideProjected, limitedsRareOnly;
    [ObservableProperty] private bool limitedsHasMore;
    [ObservableProperty] private string limitedsStatus = "", limitedsCount = "";
    /// <summary>What Min / Max apply to.</summary>
    public IReadOnlyList<MarketOption> LimitedFilterFields { get; } = new List<MarketOption>
    {
        new("Value", 0), new("RAP", 1), new("Best price", 2), new("Copies", 3), new("Favorites", 4)
    };
    [ObservableProperty] private MarketOption? limitedsFilterField;
    [ObservableProperty] private string limitedsMin = "", limitedsMax = "";
    /// <summary>The column the table is sorted by ("name", "acronym", "best", "rap", "value", "demand", "trend", "copies", "favorites").</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LimitedsSortArrow))] private string limitedsSortKey = "value";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LimitedsSortArrow))] private bool limitedsSortAsc;
    public string LimitedsSortArrow => LimitedsSortAsc ? "\uE70E" : "\uE70D";
    private bool _limitedStatsBusy;
    private const int LimitedsPage = 100;

    partial void OnLimitedsSearchChanged(string value) => FilterLimiteds();
    partial void OnLimitedsValuedOnlyChanged(bool value) => FilterLimiteds();
    partial void OnLimitedsHideProjectedChanged(bool value) => FilterLimiteds();
    partial void OnLimitedsRareOnlyChanged(bool value) => FilterLimiteds();
    partial void OnLimitedsFilterFieldChanged(MarketOption? value) => FilterLimiteds();
    partial void OnLimitedsMinChanged(string value) => FilterLimiteds();
    partial void OnLimitedsMaxChanged(string value) => FilterLimiteds();

    private DateTime _limitedRowsFrom;

    private void BuildLimiteds()
    {
        LimitedsFilterField ??= LimitedFilterFields[0];
        _limitedRowsFrom = LimitedValues.LoadedAt;
        var thumbs = _limitedRowsAll.Where(r => r.ThumbUrl.Length > 0).ToDictionary(r => r.AssetId, r => r.ThumbUrl);
        _limitedRowsAll = LimitedValues.All.Select(LimitedRow.From).ToList();
        foreach (var r in _limitedRowsAll) if (thumbs.TryGetValue(r.AssetId, out var u)) r.ThumbUrl = u;   // keep the pictures
        ApplyLimitedStats();
        FilterLimiteds();
        _ = LoadLimitedStatsAsync();
    }

    /// <summary>Roblox's best price, copies and favorites for every limited: 100 per request, the table fills in as they come.</summary>
    private async Task LoadLimitedStatsAsync()
    {
        if (_limitedStatsBusy || _limitedRowsAll.Count == 0) return;
        _limitedStatsBusy = true;
        int total = _limitedRowsAll.Count;
        try
        {
            await LimitedValues.LoadStatsAsync(_limitedRowsAll.Select(r => r.AssetId).ToList(), CancellationToken.None, known =>
            {
                LimitedsStatus = $"Loading best prices, copies and favorites from Roblox… {Math.Min(known, total):N0} of {total:N0}";
                ApplyLimitedStats();
            });
            LimitedsStatus = $"Best prices, copies and favorites from Roblox, {DateTime.Now:HH:mm}.";
            if (LimitedsSortKey is "best" or "copies" or "favorites" || LimitedsFilterField?.Value >= 2) FilterLimiteds();
        }
        catch (RobloxException ex) { LimitedsStatus = ex.TooMany ? "Roblox is busy; best prices will fill in later." : "Couldn't load best prices: " + ex.Message; }
        catch (System.Text.Json.JsonException) { }
        finally { _limitedStatsBusy = false; }
    }

    private void ApplyLimitedStats()
    {
        foreach (var r in _limitedRowsAll)
            if (LimitedValues.GetStats(r.AssetId) is { } st) { r.BestPrice = st.BestPrice; r.Copies = st.Copies; r.Favorites = st.Favorites; }
    }

    /// <summary>A column header: sorts by it; again flips the direction.</summary>
    [RelayCommand]
    private void SortLimiteds(string? key)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (LimitedsSortKey == key) LimitedsSortAsc = !LimitedsSortAsc;
        else { LimitedsSortKey = key; LimitedsSortAsc = key is "name" or "acronym"; }
        FilterLimiteds();
    }

    [RelayCommand]
    private void SearchLimiteds() => FilterLimiteds();

    private void FilterLimiteds()
    {
        var q = (LimitedsSearch ?? "").Trim();
        long? min = Limited.ParseAmount(LimitedsMin), max = Limited.ParseAmount(LimitedsMax);
        int field = LimitedsFilterField?.Value ?? 0;
        long FieldOf(LimitedRow r) => field switch { 1 => r.Rap, 2 => r.BestPrice, 3 => r.Copies, 4 => r.Favorites, _ => r.Worth };

        IEnumerable<LimitedRow> list = _limitedRowsAll;
        if (q.Length > 0)
            list = list.Where(r => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                || r.Acronym.Equals(q, StringComparison.OrdinalIgnoreCase) || r.AssetId.ToString() == q);
        if (LimitedsValuedOnly) list = list.Where(r => r.Value > 0);
        if (LimitedsHideProjected) list = list.Where(r => !r.Projected);
        if (LimitedsRareOnly) list = list.Where(r => r.Rare);
        if (min != null) list = list.Where(r => FieldOf(r) >= min);
        if (max != null) list = list.Where(r => FieldOf(r) >= 0 && FieldOf(r) <= max);

        Func<LimitedRow, IComparable> key = LimitedsSortKey switch
        {
            "name" => r => r.Name.ToLowerInvariant(),
            "acronym" => r => r.Acronym.Length == 0 ? "\uffff" : r.Acronym.ToLowerInvariant(),
            "best" => r => r.BestPrice,
            "rap" => r => r.Rap,
            "demand" => r => r.Demand,
            "trend" => r => r.Trend,
            "copies" => r => r.Copies,
            "favorites" => r => r.Favorites,
            _ => r => r.Worth
        };
        list = LimitedsSortAsc ? list.OrderBy(key).ThenByDescending(r => r.Worth) : list.OrderByDescending(key).ThenByDescending(r => r.Worth);
        _limitedRowsFiltered = list.ToList();
        LimitedsCount = $"{_limitedRowsFiltered.Count:N0} of {_limitedRowsAll.Count:N0} limiteds";
        LimitedRows.Clear();
        ShowMoreLimiteds();
    }

    [RelayCommand]
    private void ShowMoreLimiteds()
    {
        var next = _limitedRowsFiltered.Skip(LimitedRows.Count).Take(LimitedsPage).ToList();
        foreach (var r in next) { r.Odd = LimitedRows.Count % 2 == 1; LimitedRows.Add(r); }
        LimitedsHasMore = LimitedRows.Count < _limitedRowsFiltered.Count;
        _ = FillRowThumbsAsync(next.Where(r => r.ThumbUrl.Length == 0).ToList());
    }

    private static async Task FillRowThumbsAsync(List<LimitedRow> rows)
    {
        if (rows.Count == 0) return;
        var pics = await Thumbnails.GetAsync("Asset", rows.Select(r => r.AssetId), "150x150");
        foreach (var r in rows) if (pics.TryGetValue(r.AssetId, out var u)) r.ThumbUrl = u;
    }

    /// <summary>A line in the table: its item page.</summary>
    [RelayCommand]
    private Task OpenLimitedRow(LimitedRow? r)
    {
        if (r == null) return Task.CompletedTask;
        var t = LimitedTile.From(r.AssetId, r.Name, r.Rap);
        t.ThumbUrl = r.ThumbUrl;
        return OpenLimitedTile(t);
    }

    private static async Task FillLimitedThumbsAsync(IList<LimitedTile> tiles)
    {
        // assets and bundles (dynamic heads) have their own picture lists
        var assets = Thumbnails.GetAsync("Asset", tiles.Where(t => !t.IsBundle).Select(t => t.AssetId), "150x150");
        var bundles = Thumbnails.GetAsync("BundleThumbnail", tiles.Where(t => t.IsBundle).Select(t => t.AssetId), "150x150");
        var a = await assets; var b = await bundles;
        foreach (var t in tiles) if ((t.IsBundle ? b : a).TryGetValue(t.AssetId, out var u)) t.ThumbUrl = u;
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
    /// <summary>The days the line is drawn from (oldest first), for the hover on the graph.</summary>
    public IReadOnlyList<(DateTime Day, long Price)> RapChartData { get; private set; } = Array.Empty<(DateTime, long)>();

    [RelayCommand]
    private async Task OpenLimitedTile(LimitedTile? t)
    {
        if (t == null) return;
        _limitedFromMarket = Page == "Marketplace";
        OpenLimited = t;
        OpenStats = null;
        LimitedSellers.Clear();
        LimitedSellersNote = "";
        LimitedSellersLoading = true;
        NotifyLimitedSellers();
        RapChart = new PointCollection();
        RapChartRange = "";
        RapChartData = Array.Empty<(DateTime, long)>();
        OpenLimitedPicture = t.ThumbUrl;
        await LimitedLoad.RunAsync(async ct =>
        {
            var pic = Thumbnails.GetAsync(t.IsBundle ? "BundleThumbnail" : "Asset", new[] { t.AssetId }, "420x420", ct);
            var stats = await RobloxTrades.GetItemStatsAsync(t.AssetId, t.IsBundle, ct);
            if (OpenLimited != t) return true;
            OpenStats = stats;
            DrawRapChart(stats.History);
            _ = LoadLimitedSellersAsync(t, null);   // the stats brought the marketplace ID
            if ((await pic).TryGetValue(t.AssetId, out var u)) OpenLimitedPicture = u;
            return true;
        }, "Loading sales…");
    }

    /// <summary>Back from an item's page: to the trade / inventory it was opened from, or to the list of limiteds.</summary>
    [RelayCommand]
    private void CloseLimited()
    {
        if (Page == "Marketplace") { CloseMarketItem(); return; }
        OpenLimited = null;
        if (_limitedFrom.Length > 0) TradesTab = _limitedFrom;
        _limitedFrom = "";
        OnPropertyChanged(nameof(LimitedBackText));
    }

    /// <summary>Which tab the open item page goes back to ("" = the Limiteds list).</summary>
    private string _limitedFrom = "";
    public string LimitedBackText => Page == "Marketplace" ? $"Back to {MarketTitle}" : _limitedFrom switch
    {
        "Profile" => $"Back to {TradeProfileName}'s profile",
        "Calculator" => "Back to the calculator",
        "Trades" => "Back to the trade",
        "Inventory" => "Back to inventory value",
        "Deals" => "Back to deals",
        "Watchlist" => "Back to the watchlist",
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
        RapChartData = pts.Count < 2 ? Array.Empty<(DateTime, long)>() : pts;
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
    private Task BuyOpenLimited() => OpenLimited is { } t ? OpenLimitedInMarketAsync(t.AssetId, t.Name, buy: false, t.IsBundle) : Task.CompletedTask;

    [RelayCommand]
    private void OpenLimitedOnRolimons()
    {
        if (OpenLimited is { } t) Web($"https://www.rolimons.com/{(t.IsBundle ? "bundle" : "item")}/{t.AssetId}");
    }

    private static void Web(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    /// <summary>Shows a limited on the Marketplace page (sellers list). With <paramref name="buy"/>, the cheapest copy's confirm card opens too.</summary>
    private async Task OpenLimitedInMarketAsync(long assetId, string name, bool buy, bool bundle = false)
    {
        _limitedAutoBuy = buy ? assetId : 0;
        if (MarketCats.Count == 0 && MarketLoad.State != LoadState.Loading) _ = LoadMarketAsync();
        _marketDirect = true;   // straight to this item, not back to All items
        try { Page = "Marketplace"; } finally { _marketDirect = false; }
        await OpenMarketItemAsync(new MarketItem { Id = assetId, ItemType = bundle ? "Bundle" : "Asset", Name = name, IsLimited = true });
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
                    _ = RobloxTrades.MarkHoldsAsync(w.Account.Cookie, w.Account.UserId, items, CancellationToken.None);
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
