using System.Collections.ObjectModel;
using RoDeck.Controls;
using RoDeck.Models;
using RoDeck.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RoDeck.ViewModels;

/// <summary>
/// Trades page, part 2: the current account's trades (inbound, outbound, completed, inactive), one trade with the value
/// of each side and a win / loss verdict, accept / decline / counter, and making a new trade. Every change asks first.
/// </summary>
public partial class MainViewModel
{
    // ================================================================== the list

    /// <summary>"Inbound", "Outbound", "Completed" or "Inactive".</summary>
    [ObservableProperty] private string tradeType = "Inbound";
    public ObservableCollection<TradeRow> TradeRows { get; } = new();
    public Loader TradesLoad { get; } = new();
    [ObservableProperty] private bool tradesHasMore;
    [ObservableProperty] private string canTradeNote = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(BellCount))] private int inboundCount;
    private string? _tradesNext;
    private Account? _tradesFor;
    private string _tradesTypeLoaded = "";
    private DateTime _tradesLoadedAt;
    private readonly Dictionary<long, TradeDetail> _tradeDetails = new();
    private int _tradeGen;

    partial void OnTradeTypeChanged(string value)
    {
        _ = LoadTradeListAsync();
        if (Page == "Trades") RecordNav();
    }

    private void MaybeLoadTradeList()
    {
        if (Page != "Trades" || TradesTab != "Trades") return;
        if (IsTradeOpen && _tradesFor == CurrentAccount) return;   // back from an item's page: keep the trade open
        if (_tradesFor == CurrentAccount && _tradesTypeLoaded == TradeType && DateTime.Now - _tradesLoadedAt < TimeSpan.FromMinutes(2)) return;
        _ = LoadTradeListAsync();
    }

    [RelayCommand]
    private Task RefreshTrades() => LoadTradeListAsync();

    private async Task LoadTradeListAsync()
    {
        var a = CurrentAccount;
        int gen = ++_tradeGen;
        _tradesFor = a;
        _tradesTypeLoaded = TradeType;
        _tradesLoadedAt = DateTime.Now;
        CloseTrade();
        if (a != _tradeDetailsFor) { _tradeDetails.Clear(); _tradeDetailsFor = a; }
        await TradesLoad.RunAsync(async ct =>
        {
            TradeRows.Clear();
            if (a == null) throw new RobloxException("Add an account first.");
            await LimitedValues.EnsureAsync(ct);
            var can = RobloxTrades.CanTradeAsync(a.Cookie, ct);
            var (rows, next) = await RobloxTrades.GetTradesAsync(a.Cookie, TradeType, null, ct);
            try { var (ok, why) = await can; CanTradeNote = ok ? "" : why; } catch (RobloxException) { CanTradeNote = ""; }
            _tradesNext = next;
            TradesHasMore = next != null;
            foreach (var r in rows) TradeRows.Add(r);
            _ = FillTradeRowsAsync(rows, a, gen);
            _ = RefreshInboundCountAsync();
            return rows.Count > 0;
        }, "Loading trades…");
    }
    private Account? _tradeDetailsFor;

    [RelayCommand]
    private async Task LoadMoreTrades()
    {
        if (_tradesNext == null || CurrentAccount is not { } a) return;
        int gen = _tradeGen;
        try
        {
            var (rows, next) = await RobloxTrades.GetTradesAsync(a.Cookie, TradeType, _tradesNext, CancellationToken.None);
            if (gen != _tradeGen) return;
            _tradesNext = next;
            TradesHasMore = next != null;
            foreach (var r in rows) TradeRows.Add(r);
            _ = FillTradeRowsAsync(rows, a, gen);
        }
        catch (RobloxException ex) { StatusMessage = ex.Message; }
    }

    private async Task RefreshInboundCountAsync()
    {
        if (CurrentAccount is not { } a) return;
        try { var n = await RobloxTrades.GetInboundCountAsync(a.Cookie, CancellationToken.None); if (a == CurrentAccount) InboundCount = n; }
        catch (RobloxException) { }
    }

    /// <summary>Pictures, then each trade's value summary one by one (details are a request each).</summary>
    private async Task FillTradeRowsAsync(List<TradeRow> rows, Account a, int gen)
    {
        var heads = await Thumbnails.GetAsync("AvatarHeadShot", rows.Select(r => r.PartnerId), "150x150");
        foreach (var r in rows) if (heads.TryGetValue(r.PartnerId, out var u)) r.HeadshotUrl = u;
        foreach (var r in rows)
        {
            if (gen != _tradeGen) return;
            try
            {
                var d = await GetTradeDetailAsync(a, r.Id);
                if (gen != _tradeGen) return;
                Summarise(r, d);
            }
            catch (RobloxException ex) when (ex.TooMany) { break; }   // the rest fill in when opened
            catch (RobloxException) { }
        }
        await FillPartnerWorthsAsync(rows, a, gen);
    }

    private async Task<TradeDetail> GetTradeDetailAsync(Account a, long id)
    {
        if (_tradeDetails.TryGetValue(id, out var known)) return known;
        var d = await RobloxTrades.GetTradeAsync(a.Cookie, id, a.UserId, CancellationToken.None);
        _tradeDetails[id] = d;
        return d;
    }

    private static void Summarise(TradeRow r, TradeDetail d)
    {
        long diff = d.Difference;
        r.Result = d.Give == 0 && d.Get == 0 ? 0 : Math.Sign(diff);
        r.GiveText = d.Give.ToString("N0");
        r.GetText = d.Get.ToString("N0");
        r.Summary = $"{(diff >= 0 ? "+" : "−")}{Math.Abs(diff):N0}";
    }

    /// <summary>Partners' inventory values (public inventories only), remembered for 10 minutes.</summary>
    private readonly Dictionary<long, (DateTime At, string Text)> _partnerWorths = new();

    private async Task FillPartnerWorthsAsync(List<TradeRow> rows, Account a, int gen)
    {
        foreach (var id in rows.Select(r => r.PartnerId).Distinct())
        {
            if (gen != _tradeGen) return;
            if (!_partnerWorths.TryGetValue(id, out var known) || DateTime.Now - known.At > TimeSpan.FromMinutes(10))
            {
                string text;
                try
                {
                    var items = await RobloxTrades.GetCollectiblesAsync(a.Cookie, id, CancellationToken.None, extras: false);
                    text = items.Sum(i => i.Worth).ToString("N0");
                }
                catch (RobloxException ex) when (ex.TooMany) { return; }
                catch (RobloxException) { text = "private"; }
                known = (DateTime.Now, text);
                _partnerWorths[id] = known;
            }
            foreach (var r in rows.Where(r => r.PartnerId == id)) r.PartnerWorth = known.Text;
        }
    }

    /// <summary>The Decline / Cancel on a row: one click, nothing is exchanged either way.</summary>
    [RelayCommand]
    private async Task DeclineRow(TradeRow? r)
    {
        if (r == null || !r.IsActive || CurrentAccount is not { } a) return;
        bool mine = TradeType == "Outbound";
        try
        {
            await RobloxTrades.DeclineAsync(a.Cookie, r.Id);
            if (mine) _cancelledByMe.Add(r.Id);
            TradeRows.Remove(r);
            if (OpenTradeRow == r) CloseTrade();
            StatusMessage = mine ? $"Trade to {r.PartnerLabel} cancelled." : $"Trade from {r.PartnerLabel} declined.";
            _ = RefreshInboundCountAsync();
        }
        catch (RobloxException ex) { StatusMessage = ex.TooMany ? "Roblox is busy. Try again in a moment." : ex.Message; }
    }

    [RelayCommand]
    private void OpenPartnerProfile(object? x)
    {
        long id = x switch { TradeRow r => r.PartnerId, long l => l, _ => OpenTradeRow?.PartnerId ?? 0 };
        if (id > 0) Web($"https://www.roblox.com/users/{id}/profile");
    }

    // ================================================================== one trade

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsTradeOpen), nameof(TradeCanRespond), nameof(TradeCanCancel))]
    private TradeDetail? openTrade;
    [ObservableProperty] private TradeRow? openTradeRow;
    public bool IsTradeOpen => OpenTradeRow != null;
    public Loader TradeLoad { get; } = new();
    public ObservableCollection<LimitedTile> TradeGive { get; } = new();
    public ObservableCollection<LimitedTile> TradeGet { get; } = new();
    [ObservableProperty] private string tradeGiveText = "", tradeGetText = "", tradeVerdict = "";
    [ObservableProperty] private int tradeVerdictKind;
    // the opened trade, laid out like a trade window: totals per side, the difference as two pills (value and RAP)
    [ObservableProperty] private string tradeGiveValue = "", tradeGiveRap = "", tradeGetValue = "", tradeGetRap = "";
    [ObservableProperty] private string tradeValueDiff = "", tradeRapDiff = "";
    [ObservableProperty] private int tradeValueDiffKind, tradeRapDiffKind;
    [ObservableProperty] private string tradeGiveRobux = "", tradeGetRobux = "", tradeSubtitle = "";
    public string TradeGiveTitle => TradeType is "Completed" or "Inactive" ? "Items you gave" : "Items you will give";
    public string TradeGetTitle => TradeType is "Completed" or "Inactive" ? "Items you got" : "Items you will receive";
    public bool TradeCanRespond => OpenTradeRow is { IsActive: true } && TradeType == "Inbound" && OpenTrade != null;
    public bool TradeCanCancel => OpenTradeRow is { IsActive: true } && TradeType == "Outbound" && OpenTrade != null;

    partial void OnOpenTradeRowChanged(TradeRow? value)
    {
        OnPropertyChanged(nameof(IsTradeOpen));
        if (Page == "Trades") RecordNav();
    }

    [RelayCommand]
    private async Task OpenTradeRowItem(TradeRow? r)
    {
        if (r == null || CurrentAccount is not { } a) return;
        foreach (var x in TradeRows) x.IsSelected = x == r;
        OpenTradeRow = r;
        OpenTrade = null;
        TradeGoneNote = "";
        TradeGive.Clear(); TradeGet.Clear();
        await TradeLoad.RunAsync(async ct =>
        {
            var d = await GetTradeDetailAsync(a, r.Id);
            if (OpenTradeRow != r) return true;
            ShowTrade(d);
            Summarise(r, d);
            if (r.IsActive) _ = CheckTradeItemsAsync(a, r, d);   // does everyone still have everything?
            return true;
        }, "Loading trade…");
    }

    private void ShowTrade(TradeDetail d)
    {
        OpenTrade = d;
        TradeGive.Clear(); TradeGet.Clear();
        foreach (var i in d.Mine.Items) TradeGive.Add(i);
        foreach (var i in d.Theirs.Items) TradeGet.Add(i);
        _ = FillLimitedThumbsAsync(d.Mine.Items.Concat(d.Theirs.Items).Where(i => i.ThumbUrl.Length == 0).ToList());
        TradeGiveText = SideText(d.Mine, false);
        TradeGetText = SideText(d.Theirs, true);
        (TradeVerdict, TradeVerdictKind) = Verdict(d.Give, d.Get, d.RapDifference);

        long giveRap = d.Mine.Rap + d.Mine.Robux, getRap = d.Theirs.Rap + (long)(d.Theirs.Robux * 0.7);
        TradeGiveValue = d.Give.ToString("N0");
        TradeGiveRap = giveRap.ToString("N0");
        TradeGetValue = d.Get.ToString("N0");
        TradeGetRap = getRap.ToString("N0");
        TradeValueDiff = Limited.Diff(d.Get - d.Give, d.Give);
        TradeRapDiff = Limited.Diff(getRap - giveRap, giveRap);
        TradeValueDiffKind = Math.Sign(d.Get - d.Give);
        TradeRapDiffKind = Math.Sign(getRap - giveRap);
        TradeGiveRobux = d.Mine.Robux > 0 ? d.Mine.Robux.ToString("N0") : "";
        TradeGetRobux = d.Theirs.Robux > 0 ? ((long)(d.Theirs.Robux * 0.7)).ToString("N0") : "";
        var row = OpenTradeRow;
        TradeSubtitle = row == null ? d.Status
            : row.IsActive && row.Expiration != default ? $"Expires on {row.Expiration.ToLocalTime():d MMM yyyy}"
            : d.Status;
        OnPropertyChanged(nameof(TradeGiveTitle));
        OnPropertyChanged(nameof(TradeGetTitle));
    }

    private static string SideText(TradeSide s, bool received) =>
        $"Value R$ {s.Worth:N0} · RAP R$ {s.Rap:N0}"
        + (s.Robux > 0 ? $" · + R$ {s.Robux:N0}{(received ? $" (you get R$ {(long)(s.Robux * 0.7):N0} after Roblox's 30 %)" : "")}" : "");

    /// <summary>"Win: +R$ 1,200 value (+8 %)", kind 1 win / -1 loss / 0 fair.</summary>
    private static (string, int) Verdict(long give, long get, long rapDiff)
    {
        long diff = get - give;
        if (give == 0 && get == 0) return ("Nothing in this trade yet.", 0);
        double pct = give > 0 ? (double)diff / give : 1;
        int kind = Math.Abs(pct) < 0.02 ? 0 : Math.Sign(diff);
        var word = kind switch { 1 => "Win", -1 => "Loss", _ => "Fair" };
        return ($"{word}: {(diff >= 0 ? "+" : "−")}R$ {Math.Abs(diff):N0} value ({(pct >= 0 ? "+" : "−")}{Math.Abs(pct):P0}) · RAP {(rapDiff >= 0 ? "+" : "−")}R$ {Math.Abs(rapDiff):N0}", kind);
    }

    [RelayCommand]
    private void CloseTrade()
    {
        foreach (var x in TradeRows) x.IsSelected = false;
        OpenTradeRow = null;
        OpenTrade = null;
    }

    // ---- actions: always through the confirm card

    [ObservableProperty] private bool showTradeConfirm;
    [ObservableProperty] private string tradeConfirmTitle = "", tradeConfirmBody = "", tradeConfirmButton = "", tradeConfirmError = "";
    [ObservableProperty] private bool tradeConfirmDanger;
    [ObservableProperty] private bool tradeBusy;
    private Func<Task>? _tradeConfirmAction;

    private void AskTrade(string title, string body, string button, bool danger, Func<Task> action)
    {
        TradeConfirmTitle = title;
        TradeConfirmBody = body;
        TradeConfirmButton = button;
        TradeConfirmDanger = danger;
        TradeConfirmError = "";
        _tradeConfirmAction = action;
        ShowTradeConfirm = true;
    }

    [RelayCommand] private void CancelTradeConfirm() { if (!TradeBusy) ShowTradeConfirm = false; }

    [RelayCommand]
    private async Task ConfirmTrade()
    {
        if (_tradeConfirmAction == null || TradeBusy) return;
        TradeBusy = true;
        TradeConfirmError = "";
        try
        {
            await _tradeConfirmAction();
            ShowTradeConfirm = false;
        }
        catch (RobloxException ex)
        {
            TradeConfirmError = ex.TooMany ? "Roblox is busy. Nothing changed. Try again in a moment."
                : ex.RobloxSaid.Contains("unknown error", StringComparison.OrdinalIgnoreCase)
                    ? "Roblox refused it without saying why (\"An unknown error occurred\"). Usually the other player just changed their trade "
                      + "settings, or one of the items was traded away a moment ago. Nothing was sent."
                : ex.Message;
        }
        finally { TradeBusy = false; }
    }

    [RelayCommand]
    private void AcceptTrade()
    {
        if (OpenTrade is not { } d || OpenTradeRow is not { } r || CurrentAccount is not { } a) return;
        AskTrade("Accept this trade?",
            (TradeGoneNote.Length > 0 ? "⚠ " + TradeGoneNote + "\n\n" : "")
            + $"{a.Label} gives {Items(d.Mine)} and gets {Items(d.Theirs)} from {r.PartnerLabel}.\n\n{TradeVerdict}",
            "Accept trade", false, async () =>
            {
                await RobloxTrades.AcceptAsync(a.Cookie, d.Id);
                Log(ActivityLog.Trade, $"{a.Label} accepted a trade with {r.PartnerLabel}: gave {Items(d.Mine)}, got {Items(d.Theirs)}.");
                StatusMessage = $"Trade with {r.PartnerLabel} accepted.";
                TradeRows.Remove(r);
                CloseTrade();
                _ = RefreshInboundCountAsync();
            });
    }

    [RelayCommand]
    private void DeclineTrade()
    {
        if (OpenTrade is not { } d || OpenTradeRow is not { } r || CurrentAccount is not { } a) return;
        bool mine = TradeType == "Outbound";
        AskTrade(mine ? "Cancel this trade?" : "Decline this trade?",
            mine ? $"Your trade to {r.PartnerLabel} is cancelled. Nothing is exchanged." : $"{r.PartnerLabel}'s trade is declined. Nothing is exchanged.",
            mine ? "Cancel trade" : "Decline trade", true, async () =>
            {
                await RobloxTrades.DeclineAsync(a.Cookie, d.Id);
                if (mine) _cancelledByMe.Add(d.Id);
                StatusMessage = mine ? $"Trade to {r.PartnerLabel} cancelled." : $"Trade from {r.PartnerLabel} declined.";
                TradeRows.Remove(r);
                CloseTrade();
                _ = RefreshInboundCountAsync();
            });
    }

    private static string Items(TradeSide s)
    {
        var parts = s.Items.Select(i => i.Name).ToList();
        if (s.Robux > 0) parts.Add($"R$ {s.Robux:N0}");
        return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
    }

    // ================================================================== making a trade (new or counter)

    [ObservableProperty] private bool isBuildingTrade;
    [ObservableProperty] private long buildPartnerId;
    /// <summary>Why this account can't trade with the picked player ("" when it can).</summary>
    [ObservableProperty] private string buildPartnerProblem = "";
    [ObservableProperty] private string buildPartnerName = "";
    [ObservableProperty] private string buildPartnerHead = "";
    [ObservableProperty] private string partnerSearch = "";
    public ObservableCollection<PlayerHit> PartnerHits { get; } = new();
    [ObservableProperty] private bool partnerSearching;
    private long? _counterOf;
    [ObservableProperty] private string buildTitle = "New trade";

    public ObservableCollection<LimitedTile> MyTradable { get; } = new();
    public ObservableCollection<LimitedTile> TheirTradable { get; } = new();
    private List<LimitedTile> _myTradableAll = new(), _theirTradableAll = new();
    /// <summary>The account <see cref="_myTradableAll"/> was loaded for ("" = none yet).</summary>
    private string _myTradableFor = "";
    public ObservableCollection<LimitedTile> BuildGive { get; } = new();
    public ObservableCollection<LimitedTile> BuildGet { get; } = new();
    [ObservableProperty] private string myTradableFilter = "", theirTradableFilter = "";
    [ObservableProperty] private long buildGiveRobux, buildGetRobux;
    [ObservableProperty] private string buildGiveText = "", buildGetText = "", buildVerdict = "";
    [ObservableProperty] private int buildVerdictKind;
    public Loader MyTradableLoad { get; } = new();
    public Loader TheirTradableLoad { get; } = new();
    private const int MaxTradeItems = 4;

    partial void OnMyTradableFilterChanged(string value) => FilterTradable(MyTradable, _myTradableAll, value);
    partial void OnTheirTradableFilterChanged(string value) => FilterTradable(TheirTradable, _theirTradableAll, value);
    partial void OnBuildGiveRobuxChanged(long value) => UpdateBuild();
    partial void OnBuildGetRobuxChanged(long value) => UpdateBuild();

    [RelayCommand]
    private void NewTrade()
    {
        _counterOf = null;
        BuildTitle = "New trade";
        StartBuild(0, "", "");
    }

    /// <summary>Counter: the same partner, starting from their offer.</summary>
    [RelayCommand]
    private void CounterTrade()
    {
        if (OpenTrade is not { } d || OpenTradeRow is not { } r) return;
        _counterOf = d.Id;
        BuildTitle = $"Counter {r.PartnerLabel}'s trade";
        StartBuild(r.PartnerId, r.PartnerLabel, r.HeadshotUrl, d);
    }

    private void StartBuild(long partnerId, string name, string head, TradeDetail? from = null)
    {
        if (CurrentAccount is not { } a) { StatusMessage = "Add an account first."; return; }
        IsBuildingTrade = true;
        BuildGive.Clear(); BuildGet.Clear();
        BuildGiveRobux = 0; BuildGetRobux = 0;
        PartnerHits.Clear();
        PartnerSearch = "";
        MyTradableFilter = ""; TheirTradableFilter = "";
        _theirTradableAll = new(); TheirTradable.Clear();
        _ = LoadMyTradableAsync(a, from);
        if (partnerId > 0) _ = PickPartnerAsync(partnerId, name, head, from);
        else { BuildPartnerId = 0; BuildPartnerName = ""; BuildPartnerHead = ""; }
        UpdateBuild();
    }

    [RelayCommand]
    private void CloseBuild() => IsBuildingTrade = false;

    private async Task LoadMyTradableAsync(Account a, TradeDetail? from)
    {
        await MyTradableLoad.RunAsync(async ct =>
        {
            var list = await RobloxTrades.GetTradableAsync(a.Cookie, a.UserId, ct);
            ct.ThrowIfCancellationRequested();
            if (CurrentAccount?.Id != a.Id) return _myTradableAll.Count > 0;   // switched away meanwhile: not this account's list
            _myTradableAll = list.OrderByDescending(i => i.Worth).ToList();
            _myTradableFor = a.Id;
            if (IsAdPickerOpen) FillAdPicker();
            if (from != null)
                foreach (var i in from.Mine.Items)
                    if (_myTradableAll.FirstOrDefault(x => x.InstanceId == i.InstanceId) is { } same && BuildGive.Count < MaxTradeItems)
                    { same.IsSelected = true; BuildGive.Add(same); }
            if (from != null) BuildGiveRobux = from.Mine.Robux;
            FilterTradable(MyTradable, _myTradableAll, MyTradableFilter);
            UpdateBuild();
            return list.Count > 0;
        }, "Loading your limiteds…");
    }

    [RelayCommand]
    private async Task SearchPartner()
    {
        var q = (PartnerSearch ?? "").Trim();
        if (q.Length < 3) { StatusMessage = "Type at least 3 letters of their username."; return; }
        PartnerSearching = true;
        try
        {
            var hits = await RobloxFriends.SearchPlayersAsync(q, CurrentAccount?.Cookie, CancellationToken.None);
            PartnerHits.Clear();
            foreach (var h in hits.Take(8)) PartnerHits.Add(h);
            if (hits.Count == 0) StatusMessage = $"No one called “{q}”.";
        }
        catch (RobloxException ex) { StatusMessage = ex.Message; }
        finally { PartnerSearching = false; }
    }

    [RelayCommand]
    private Task PickPartner(PlayerHit? h) => h == null ? Task.CompletedTask : PickPartnerAsync(h.UserId, h.Label, h.HeadshotUrl, null);

    private async Task PickPartnerAsync(long id, string name, string head, TradeDetail? from)
    {
        if (CurrentAccount is not { } a) return;
        if (id == a.UserId) { StatusMessage = "That's this account. Pick someone else."; return; }
        BuildPartnerId = id;
        BuildPartnerName = name;
        BuildPartnerHead = head;
        BuildPartnerProblem = "";
        PartnerHits.Clear();
        BuildGet.Clear();
        _ = CheckPartnerAsync(a, id, name);
        if (head.Length == 0)
        {
            var pics = await Thumbnails.GetAsync("AvatarHeadShot", new[] { id }, "150x150");
            if (pics.TryGetValue(id, out var u) && BuildPartnerId == id) BuildPartnerHead = u;
        }
        await TheirTradableLoad.RunAsync(async ct =>
        {
            var list = await RobloxTrades.GetTradableAsync(a.Cookie, id, ct);
            if (BuildPartnerId != id) return true;
            _theirTradableAll = list.OrderByDescending(i => i.Worth).ToList();
            if (from != null)
            {
                foreach (var i in from.Theirs.Items)
                    if (_theirTradableAll.FirstOrDefault(x => x.InstanceId == i.InstanceId) is { } same && BuildGet.Count < MaxTradeItems)
                    { same.IsSelected = true; BuildGet.Add(same); }
                BuildGetRobux = from.Theirs.Robux;
            }
            FilterTradable(TheirTradable, _theirTradableAll, TheirTradableFilter);
            UpdateBuild();
            return list.Count > 0;
        }, "Loading their limiteds…");
    }

    /// <summary>Roblox Plus on both sides, and their trade settings: shown (and Send blocked) before anything is picked.</summary>
    private async Task CheckPartnerAsync(Account a, long id, string name)
    {
        var why = await RobloxTrades.CannotTradeReasonAsync(a.Cookie, id, name, a.Label, CancellationToken.None);
        if (BuildPartnerId != id) return;
        BuildPartnerProblem = why ?? "";
        UpdateBuild();
    }

    private static void FilterTradable(ObservableCollection<LimitedTile> shown, List<LimitedTile> all, string filter)
    {
        var q = (filter ?? "").Trim();
        var list = all.Where(i => q.Length == 0 || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(200).ToList();
        shown.Clear();
        foreach (var i in list) shown.Add(i);
        _ = FillLimitedThumbsAsync(list.Where(i => i.ThumbUrl.Length == 0).ToList());
    }

    [RelayCommand] private void ToggleGive(LimitedTile? t) => ToggleSide(t, BuildGive, "your");
    [RelayCommand] private void ToggleGet(LimitedTile? t) => ToggleSide(t, BuildGet, "their");

    private void ToggleSide(LimitedTile? t, ObservableCollection<LimitedTile> side, string whose)
    {
        if (t == null) return;
        if (side.Contains(t)) { side.Remove(t); t.IsSelected = false; }
        else
        {
            if (t.IsOnHold) { StatusMessage = $"{t.Name} is on trade hold and can't be traded yet."; return; }
            if (side.Count >= MaxTradeItems) { StatusMessage = $"Roblox allows up to {MaxTradeItems} items on {whose} side."; return; }
            side.Add(t);
            t.IsSelected = true;
        }
        UpdateBuild();
    }

    public bool CanSendTrade => BuildPartnerId > 0 && BuildProblem.Length == 0;

    /// <summary>
    /// Why Roblox would refuse this trade, before sending it (Roblox itself only says "An unknown error occurred"):
    /// every side needs at least one item, and Robux on a side may be at most half the RAP of that side's items.
    /// "" when it's fine.
    /// </summary>
    public string BuildProblem
    {
        get
        {
            if (BuildPartnerProblem.Length > 0) return BuildPartnerProblem;
            if (BuildGive.Count == 0 && BuildGet.Count == 0) return "";   // nothing picked yet: no message
            if (BuildGive.Count == 0) return "Add at least one of your items: Roblox doesn't allow a trade with only Robux on a side.";
            if (BuildGet.Count == 0) return "Add at least one of their items: Roblox doesn't allow a trade with only Robux on a side.";
            long giveCap = BuildGive.Sum(i => i.Rap) / 2, getCap = BuildGet.Sum(i => i.Rap) / 2;
            if (BuildGiveRobux > giveCap) return $"You can add at most R$ {giveCap:N0}: half the RAP of the items you give.";
            if (BuildGetRobux > getCap) return $"You can ask at most R$ {getCap:N0}: half the RAP of the items you get.";
            return "";
        }
    }

    private void UpdateBuild()
    {
        var mine = new TradeSide { Items = BuildGive.ToList(), Robux = Math.Max(0, BuildGiveRobux) };
        var theirs = new TradeSide { Items = BuildGet.ToList(), Robux = Math.Max(0, BuildGetRobux) };
        var d = new TradeDetail { Mine = mine, Theirs = theirs };
        BuildGiveText = SideText(mine, false);
        BuildGetText = SideText(theirs, true);
        (BuildVerdict, BuildVerdictKind) = Verdict(d.Give, d.Get, d.RapDifference);
        OnPropertyChanged(nameof(BuildProblem));
        OnPropertyChanged(nameof(CanSendTrade));
    }

    [RelayCommand]
    private void SendTrade()
    {
        if (!CanSendTrade || CurrentAccount is not { } a) return;
        var give = BuildGive.ToList(); var get = BuildGet.ToList();
        long giveR = Math.Max(0, BuildGiveRobux), getR = Math.Max(0, BuildGetRobux);
        var partner = BuildPartnerName; var partnerId = BuildPartnerId; var counter = _counterOf;
        var mine = new TradeSide { Items = give, Robux = giveR };
        var theirs = new TradeSide { Items = get, Robux = getR };
        AskTrade(counter != null ? "Send this counter?" : "Send this trade?",
            $"{a.Label} offers {Items(mine)} to {partner} for {Items(theirs)}.\n\n{BuildVerdict}",
            counter != null ? "Send counter" : "Send trade", false, async () =>
            {
                await RobloxTrades.SendAsync(a.Cookie, a.UserId, give.Select(i => i.InstanceId), giveR, partnerId,
                                             get.Select(i => i.InstanceId), getR, counter);
                Log(ActivityLog.Trade, $"{a.Label} sent a trade to {partner}: {Items(mine)} for {Items(theirs)}.");
                StatusMessage = $"Trade sent to {partner}.";
                IsBuildingTrade = false;
                // show it in Outbound (switching there loads the list; already there: load it again)
                if (TradeType == "Outbound") _ = LoadTradeListAsync(); else TradeType = "Outbound";
            });
    }

    /// <summary>The account switched: trades are per account.</summary>
    private void TradesAccountChanged()
    {
        IsBuildingTrade = false;
        ShowTradeConfirm = false;
        _myTradableAll = new(); _myTradableFor = ""; MyTradable.Clear();
        TradeAdsAccountChanged();
        InboundCount = 0;
        _tradesLoadedAt = DateTime.MinValue;
        MaybeLoadTradeList();
    }
}
