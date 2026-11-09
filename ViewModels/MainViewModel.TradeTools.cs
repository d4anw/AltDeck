using System.Collections.ObjectModel;
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
/// Trades → Calculator (add limiteds to both sides and see who wins), Watchlist (get a notification when a limited
/// sells at or below your price), trade profiles (anyone's limiteds and what they were worth over time, your own too)
/// and the check on open trades that every item is still owned.
/// </summary>
public partial class MainViewModel
{
    // ================================================================== finding a limited by name

    /// <summary>Limiteds whose name, acronym or ID matches; the most valuable first. Empty search: the most valuable.</summary>
    private static List<LimitedTile> FindLimiteds(string? query, int take = 24)
    {
        var q = (query ?? "").Trim();
        var all = LimitedValues.All;
        IEnumerable<LimitedInfo> hits = q.Length == 0
            ? all.OrderByDescending(v => v.Worth)
            : all.Where(v => v.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                             || v.Acronym.Equals(q, StringComparison.OrdinalIgnoreCase)
                             || v.AssetId.ToString() == q)
                 .OrderByDescending(v => v.Acronym.Equals(q, StringComparison.OrdinalIgnoreCase))
                 .ThenByDescending(v => v.Worth);
        return hits.Take(take).Select(v => LimitedTile.From(v.AssetId, v.Name, v.Rap)).ToList();
    }

    // ================================================================== calculator

    public ObservableCollection<LimitedTile> CalcGive { get; } = new();
    public ObservableCollection<LimitedTile> CalcGet { get; } = new();
    public ObservableCollection<LimitedTile> CalcHits { get; } = new();
    [ObservableProperty] private string calcSearch = "";
    /// <summary>Which side a picked item goes to: "Give" or "Get".</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CalcSideLabel))] private string calcSide = "Give";
    public string CalcSideLabel => CalcSide == "Get" ? "You get" : "You give";
    [ObservableProperty] private long calcGiveRobux, calcGetRobux;
    [ObservableProperty] private string calcGiveValue = "0", calcGiveRap = "0", calcGetValue = "0", calcGetRap = "0";
    [ObservableProperty] private string calcValueDiff = "", calcRapDiff = "", calcVerdict = "Add items to both sides.";
    [ObservableProperty] private int calcValueDiffKind, calcRapDiffKind, calcVerdictKind;
    public const int CalcMaxItems = 4;

    partial void OnCalcSearchChanged(string value) => RefreshCalcHits();
    partial void OnCalcGiveRobuxChanged(long value) => UpdateCalc();
    partial void OnCalcGetRobuxChanged(long value) => UpdateCalc();

    private async Task OpenCalculatorAsync()
    {
        try { await LimitedValues.EnsureAsync(CancellationToken.None); } catch { }
        if (CalcHits.Count == 0) RefreshCalcHits();
    }

    private void RefreshCalcHits()
    {
        CalcHits.Clear();
        foreach (var t in FindLimiteds(CalcSearch)) CalcHits.Add(t);
        _ = FillLimitedThumbsAsync(CalcHits.ToList());
    }

    [RelayCommand]
    private void CalcAdd(LimitedTile? t)
    {
        if (t == null) return;
        var side = CalcSide == "Get" ? CalcGet : CalcGive;
        if (side.Count >= CalcMaxItems) { StatusMessage = $"Up to {CalcMaxItems} items per side, like on Roblox."; return; }
        // a copy of its own, so the same item can be on a side twice
        var copy = LimitedTile.From(t.AssetId, t.Name, t.Rap);
        copy.ThumbUrl = t.ThumbUrl;
        side.Add(copy);
        if (copy.ThumbUrl.Length == 0) _ = FillLimitedThumbsAsync(new List<LimitedTile> { copy });
        UpdateCalc();
    }

    [RelayCommand]
    private void CalcRemove(LimitedTile? t)
    {
        if (t == null) return;
        if (!CalcGive.Remove(t)) CalcGet.Remove(t);
        UpdateCalc();
    }

    [RelayCommand]
    private void CalcClear()
    {
        CalcGive.Clear(); CalcGet.Clear();
        CalcGiveRobux = 0; CalcGetRobux = 0;
        UpdateCalc();
    }

    /// <summary>Swaps the sides: see the trade from the other person's view.</summary>
    [RelayCommand]
    private void CalcSwap()
    {
        var give = CalcGive.ToList(); var get = CalcGet.ToList();
        CalcGive.Clear(); CalcGet.Clear();
        foreach (var t in get) CalcGive.Add(t);
        foreach (var t in give) CalcGet.Add(t);
        (CalcGiveRobux, CalcGetRobux) = (CalcGetRobux, CalcGiveRobux);
        UpdateCalc();
    }

    /// <summary>Totals per side and the difference; Robux you get count after Roblox's 30 % fee, as in a real trade.</summary>
    private void UpdateCalc()
    {
        long giveRobux = Math.Max(0, CalcGiveRobux), getRobux = (long)(Math.Max(0, CalcGetRobux) * 0.7);
        long give = CalcGive.Sum(t => t.Worth) + giveRobux, get = CalcGet.Sum(t => t.Worth) + getRobux;
        long giveRap = CalcGive.Sum(t => t.Rap) + giveRobux, getRap = CalcGet.Sum(t => t.Rap) + getRobux;
        CalcGiveValue = give.ToString("N0"); CalcGetValue = get.ToString("N0");
        CalcGiveRap = giveRap.ToString("N0"); CalcGetRap = getRap.ToString("N0");
        CalcValueDiff = Limited.Diff(get - give, give);
        CalcRapDiff = Limited.Diff(getRap - giveRap, giveRap);
        CalcValueDiffKind = Math.Sign(get - give);
        CalcRapDiffKind = Math.Sign(getRap - giveRap);
        (CalcVerdict, CalcVerdictKind) = give == 0 || get == 0 && CalcGet.Count == 0
            ? ("Add items to both sides.", 0)
            : Verdict(give, get, getRap - giveRap);
    }

    // ================================================================== watchlist

    public ObservableCollection<WatchRow> WatchRows { get; } = new();
    public ObservableCollection<LimitedTile> WatchHits { get; } = new();
    [ObservableProperty] private string watchSearch = "";
    [ObservableProperty] private string watchStatus = "";
    private readonly DispatcherTimer _watchTimer = new() { Interval = TimeSpan.FromSeconds(45) };
    private bool _watchBusy;
    public bool HasWatchRows => WatchRows.Count > 0;

    partial void OnWatchSearchChanged(string value)
    {
        WatchHits.Clear();
        if ((value ?? "").Trim().Length == 0) return;
        foreach (var t in FindLimiteds(value, 8)) WatchHits.Add(t);
        _ = FillLimitedThumbsAsync(WatchHits.ToList());
    }

    /// <summary>Started once: the saved list, checked every 45 seconds (even with RoDeck in the tray).</summary>
    private void SetupWatchlist()
    {
        foreach (var e in Settings.Watchlist) AddWatchRow(e.AssetId, e.MaxPrice, e.Name);
        WatchRows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasWatchRows));
        _watchTimer.Tick += async (_, _) => await WatchTickAsync();
        _watchTimer.Start();
        if (WatchRows.Count > 0) _ = WatchTickAsync();
    }

    private WatchRow AddWatchRow(long id, long maxPrice, string name)
    {
        var v = LimitedValues.Get(id);
        var row = new WatchRow { AssetId = id, Name = v?.Name is { Length: > 0 } n ? n : name, Rap = v?.Rap ?? 0, Value = v?.Value ?? -1, MaxPrice = maxPrice };
        row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(WatchRow.MaxPrice)) { row.NotifiedPrice = 0; SaveWatchlist(); } };
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
        // a starting price you can change: 10 % under what it's worth
        long start = Math.Max(1, (long)Math.Round(t.Worth * 0.9 / 10.0) * 10);
        AddWatchRow(t.AssetId, start, t.Name);
        WatchSearch = "";
        SaveWatchlist();
        await WatchTickAsync();
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

    /// <summary>The cheapest copy of each watched limited right now; a notification once per new price at or below yours.</summary>
    private async Task WatchTickAsync()
    {
        if (_watchBusy || WatchRows.Count == 0) return;
        _watchBusy = true;
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
                if (r.IsHit && r.NotifiedPrice != r.BestPrice)
                {
                    r.NotifiedPrice = r.BestPrice;
                    Notification?.Invoke($"Watchlist: {r.Name} for R$ {r.BestPrice:N0}",
                                         $"At or below your price of R$ {r.MaxPrice:N0} (RAP {r.Rap:N0}).", "Watchlist");
                }
            }
            WatchStatus = $"Checked at {DateTime.Now:HH:mm:ss} · every 45 seconds, also while RoDeck is in the tray.";
        }
        catch (RobloxException ex) { WatchStatus = ex.TooMany ? "Roblox is busy; trying again soon." : "Couldn't check prices: " + ex.Message; }
        catch (System.Text.Json.JsonException) { }
        finally { _watchBusy = false; }
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
            if (theirGone.Count > 0)
                notes.Add($"{r.PartnerLabel} doesn't have {Names(theirGone)} anymore.");
            if (myGone.Count > 0)
                notes.Add($"You don't have {Names(myGone)} anymore.");
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
    [ObservableProperty] private string tradeProfileEmpty = "No limiteds.";
    [ObservableProperty] private PointCollection tradeProfileChart = new();
    [ObservableProperty] private PointCollection tradeProfileChartFill = new();
    [ObservableProperty] private string tradeProfileChartNote = "", tradeProfileChartChange = "";
    [ObservableProperty] private int tradeProfileChartKind;
    public ObservableCollection<LimitedTile> TradeProfileItems { get; } = new();
    public Loader TradeProfileLoad { get; } = new();
    public Loader TradeProfileChartLoad { get; } = new();
    public bool TradeProfileIsMe => CurrentAccount != null && CurrentAccount.UserId == TradeProfileId;
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

    /// <summary>Someone's trade profile: from a trade (row or open trade), an account, or a user ID.</summary>
    [RelayCommand]
    private async Task OpenTradeProfile(object? x)
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
        await ShowTradeProfileAsync(a, id, name, user, head);
        TradesTab = "Profile";
    }

    [RelayCommand]
    private Task OpenMyTradeProfile() => CurrentAccount is { } a ? OpenTradeProfile(a) : Task.CompletedTask;

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
        bool same = TradeProfileId == id && TradeProfileItems.Count > 0;
        TradeProfileId = id;
        TradeProfileName = name.Length > 0 ? name : user.Length > 0 ? user : $"User {id}";
        TradeProfileUser = user;
        TradeProfileHead = head;
        if (same) return;   // already showing them (back / forward)
        TradeProfileItems.Clear();
        TradeProfileValue = TradeProfileRap = TradeProfileCount = "–";
        TradeProfileChart = new PointCollection(); TradeProfileChartFill = new PointCollection();
        TradeProfileChartNote = TradeProfileChartChange = "";
        if (head.Length == 0)
            _ = Task.Run(async () =>
            {
                var pics = await Thumbnails.GetAsync("AvatarHeadShot", new[] { id }, "150x150");
                if (pics.TryGetValue(id, out var u)) Application.Current.Dispatcher.Invoke(() => { if (TradeProfileId == id) TradeProfileHead = u; });
            });
        if (name.Length == 0 && user.Length == 0) _ = FillProfileNameAsync(id);
        await TradeProfileLoad.RunAsync(async ct =>
        {
            await LimitedValues.EnsureAsync(ct);
            List<LimitedTile> items;
            try { items = await RobloxTrades.GetCollectiblesAsync(a.Cookie, id, ct); }
            catch (RobloxException ex) when (ex.Status is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized && !ex.SignedOut)
            {
                TradeProfileEmpty = "Their inventory is private, so their limiteds can't be shown.";
                return false;
            }
            if (TradeProfileId != id) return true;
            var sorted = items.OrderByDescending(i => i.Worth).ToList();
            foreach (var t in sorted) TradeProfileItems.Add(t);
            TradeProfileValue = sorted.Sum(i => i.Worth).ToString("N0");
            TradeProfileRap = sorted.Sum(i => i.Rap).ToString("N0");
            TradeProfileCount = sorted.Count.ToString("N0");
            TradeProfileEmpty = "No limiteds.";
            _ = FillLimitedThumbsAsync(sorted);
            _ = LoadProfileChartAsync(id, sorted);
            return sorted.Count > 0;
        }, "Loading limiteds…");
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

    /// <summary>
    /// What their limiteds were worth over the last 90 days: for their most valuable items (up to 25, most of the
    /// total), the average sale price per day from Roblox, added up. Drawn as a line in a 600 × 140 box.
    /// </summary>
    private async Task LoadProfileChartAsync(long id, List<LimitedTile> items)
    {
        if (items.Count == 0) return;
        await TradeProfileChartLoad.RunAsync(async ct =>
        {
            const int days = 90;
            var start = DateTime.UtcNow.Date.AddDays(-days);
            var groups = items.GroupBy(i => (i.AssetId, i.IsBundle))
                              .Select(g => (g.Key.AssetId, g.Key.IsBundle, Count: g.Count(), Rap: g.First().Rap))
                              .OrderByDescending(g => g.Rap * g.Count).Take(25).ToList();
            long totalRap = items.Sum(i => i.Rap), covered = groups.Sum(g => g.Rap * g.Count);
            var sum = new double[days + 1];
            foreach (var g in groups)
            {
                var history = await RobloxTrades.GetPriceHistoryAsync(g.AssetId, ct, g.IsBundle);
                if (TradeProfileId != id) return true;
                var pts = history.Where(p => p.Price > 0).OrderBy(p => p.Day).ToList();
                // per day: the last known average sale price (a week's average, so one odd sale doesn't spike it)
                for (int d = 0; d <= days; d++)
                {
                    var day = start.AddDays(d);
                    var recent = pts.Where(p => p.Day <= day && p.Day > day.AddDays(-7)).Select(p => (double)p.Price).ToList();
                    double price = recent.Count > 0 ? recent.Average()
                                 : pts.LastOrDefault(p => p.Day <= day) is { Price: > 0 } before ? before.Price
                                 : pts.Count > 0 ? pts[0].Price : g.Rap;
                    sum[d] += price * g.Count;
                }
            }
            if (TradeProfileId != id) return true;
            double lo = sum.Min(), hi = sum.Max();
            if (hi - lo < 1) hi = lo + 1;
            var line = new PointCollection();
            for (int d = 0; d <= days; d++) line.Add(new Point(d * 600.0 / days, 132 - (sum[d] - lo) * 120.0 / (hi - lo)));
            var fill = new PointCollection(line) { new Point(600, 140), new Point(0, 140) };
            line.Freeze(); fill.Freeze();
            TradeProfileChart = line;
            TradeProfileChartFill = fill;
            double change = sum[0] > 0 ? (sum[^1] - sum[0]) / sum[0] : 0;
            TradeProfileChartKind = Math.Abs(change) < 0.01 ? 0 : Math.Sign(change);
            TradeProfileChartChange = $"{(change >= 0 ? "+" : "−")}{Math.Abs(change):0.#%} in 90 days";
            TradeProfileChartNote = $"Low R$ {lo:N0} · high R$ {hi:N0} · from the sale prices of {(groups.Count == items.Select(i => i.AssetId).Distinct().Count() ? "all their items" : $"their {groups.Count} most valuable items ({(totalRap > 0 ? (double)covered / totalRap : 1):0%} of their RAP)")}.";
            return true;
        }, "Working out their value over time…");
    }
}
