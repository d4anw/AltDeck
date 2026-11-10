using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoDeck.Controls;
using RoDeck.Models;
using RoDeck.Services;

namespace RoDeck.ViewModels;

// The Robux pill's menu: My Transactions (a page) and Redeem Roblox Codes (a card), both for the account top right.
// Buying Robux stays on Roblox's own payment page (see RobloxWebWindow).
public partial class MainViewModel
{
    // ================================================================== My Transactions

    [ObservableProperty] private string txType = "Purchase";
    [ObservableProperty] private string txTimeFrame = "Month";
    [ObservableProperty] private string txIncoming = "";
    [ObservableProperty] private string txOutgoing = "";
    [ObservableProperty] private string txPending = "";
    [ObservableProperty] private bool hasTxMore;
    [ObservableProperty] private bool txMoreBusy;

    public ObservableCollection<TxRow> TxRows { get; } = new();
    public ObservableCollection<TxTotal> TxTotals { get; } = new();
    public Loader TxLoad { get; } = new();
    public Loader TxTotalsLoad { get; } = new();

    private string? _txNext;
    private Account? _txFor;

    // Roblox adds up the totals on request and only allows a few a minute: each account + time frame is kept a few
    // minutes, so switching back and forth is instant, and a quick run of clicks only asks for the last one
    private readonly Dictionary<string, (DateTime At, long In, long Out, long Pending, List<TxTotal> Lines)> _txTotalsCache = new();
    private int _txTotalsAsk;

    partial void OnTxTypeChanged(string value) { if (Page == "Transactions") _ = LoadTxAsync(); }
    partial void OnTxTimeFrameChanged(string value) { if (Page == "Transactions") _ = LoadTxTotalsAsync(); }

    [RelayCommand]
    private void OpenTransactions() => Page = "Transactions";

    /// <summary>Opening the page (or switching account on it): the totals and the list for that account.</summary>
    private void MaybeLoadTransactions()
    {
        if (Page != "Transactions") return;
        if (_txFor == CurrentAccount && TxLoad.State is LoadState.Ready or LoadState.Loading && TxRows.Count > 0) return;
        _ = LoadTxTotalsAsync();
        _ = LoadTxAsync();
    }

    [RelayCommand]
    private Task RefreshTransactions()
    {
        _txTotalsCache.Clear();
        return Task.WhenAll(LoadTxTotalsAsync(), LoadTxAsync());
    }

    private Task LoadTxTotalsAsync() => TxTotalsLoad.RunAsync(async ct =>
    {
        int ask = ++_txTotalsAsk;
        TxTotals.Clear();
        TxIncoming = TxOutgoing = TxPending = "";
        if (CurrentAccount is not { UserId: > 0 } a) throw new RobloxException("Pick an account top right first.");
        var frame = TxTimeFrame;
        var key = $"{a.Id}|{frame}";
        if (!_txTotalsCache.TryGetValue(key, out var t) || DateTime.Now - t.At > TimeSpan.FromMinutes(5))
        {
            await Task.Delay(350, ct);   // clicking through the time frames: only the last one is asked
            if (ask != _txTotalsAsk) return true;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromMinutes(2));
            _ = SayStillLoadingAsync(TxTotalsLoad, ct, frame switch
            {
                "Day" => "Roblox is adding up the past day…",
                "Week" => "Roblox is adding up the past week…",
                "Year" => "Roblox is adding up the whole year. That can take a minute…",
                _ => "Roblox is adding up the past month. That can take a while…",
            });
            try
            {
                var (inc, outg, pend, lines) = await RobloxEconomy.GetTotalsAsync(a.Cookie, a.UserId, frame, limit.Token);
                t = (DateTime.Now, inc, outg, pend, lines);
                _txTotalsCache[key] = t;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new RobloxException("Roblox didn't finish adding this up. Press Try again in a moment.");
            }
        }
        if (a != CurrentAccount || frame != TxTimeFrame) return true;
        TxIncoming = "+" + t.In.ToString("N0");
        TxOutgoing = (t.Out > 0 ? "−" : "") + Math.Abs(t.Out).ToString("N0");
        TxPending = t.Pending.ToString("N0");
        foreach (var l in t.Lines) TxTotals.Add(l);
        return true;
    });

    private Task LoadTxAsync() => TxLoad.RunAsync(async ct =>
    {
        TxRows.Clear();
        _txNext = null;
        HasTxMore = false;
        if (CurrentAccount is not { UserId: > 0 } a) throw new RobloxException("Pick an account top right first.");
        _txFor = a;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromMinutes(2));
        _ = SayStillLoadingAsync(TxLoad, ct, "Roblox is still looking these up…");
        List<TxRow> rows;
        string? next;
        try { (rows, next) = await RobloxEconomy.GetTransactionsAsync(a.Cookie, a.UserId, TxType, null, limit.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RobloxException("Roblox didn't answer. Press Try again in a moment.");
        }
        if (a != CurrentAccount) return true;
        foreach (var r in rows) TxRows.Add(r);
        _txNext = next;
        HasTxMore = next != null;
        _ = FillTxThumbsAsync(rows);
        return rows.Count > 0;
    });

    /// <summary>After a few seconds of loading, says why it takes a while (Roblox works these out when asked).</summary>
    private static async Task SayStillLoadingAsync(Loader load, CancellationToken ct, string text)
    {
        try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { return; }
        if (!ct.IsCancellationRequested && load.State == LoadState.Loading && load.Message == "Loading…") load.Message = text;
    }

    [RelayCommand]
    private async Task LoadMoreTx()
    {
        if (TxMoreBusy || _txNext == null || CurrentAccount is not { UserId: > 0 } a) return;
        TxMoreBusy = true;
        var type = TxType;
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var (rows, next) = await RobloxEconomy.GetTransactionsAsync(a.Cookie, a.UserId, type, _txNext, limit.Token);
            if (a != CurrentAccount || type != TxType) return;
            foreach (var r in rows) TxRows.Add(r);
            _txNext = next;
            HasTxMore = next != null;
            _ = FillTxThumbsAsync(rows);
        }
        catch (RobloxException ex) { StatusMessage = ex.Message; }
        catch (OperationCanceledException) { StatusMessage = "Roblox didn't answer. Press Show more again in a moment."; }
        finally { TxMoreBusy = false; }
    }

    private async Task FillTxThumbsAsync(List<TxRow> rows)
    {
        try
        {
            // every kind at once, each from its own picture service
            await Task.WhenAll(rows.Where(r => r.HasPicture).GroupBy(r => r.ItemType).Select(async g =>
            {
                try
                {
                    var urls = await RobloxEconomy.GetIconsAsync(g.Key, g.Select(r => r.ItemId), CancellationToken.None);
                    foreach (var r in g) if (urls.TryGetValue(r.ItemId, out var u)) r.ThumbUrl = u;
                }
                catch (Exception ex) when (ex is RobloxException or System.Text.Json.JsonException) { }
            }));
        }
        catch { }
    }

    /// <summary>A row with an item: its Marketplace page.</summary>
    [RelayCommand]
    private void OpenTxItem(TxRow? r)
    {
        if (r is not { HasItem: true }) return;
        _marketDirect = true;
        try { Page = "Marketplace"; } finally { _marketDirect = false; }
        _ = OpenMarketItemAsync(new MarketItem { Id = r.ItemId, Name = r.ItemName, ItemType = r.ItemType == "Bundle" ? "Bundle" : "Asset", ThumbUrl = r.ThumbUrl });
    }

    // ================================================================== Redeem Roblox Codes

    [ObservableProperty] private bool showRedeem;
    [ObservableProperty] private string redeemCode = "";
    [ObservableProperty] private string redeemResult = "";
    [ObservableProperty] private int redeemResultKind;   // 1 done, -1 failed, 0 nothing yet
    [ObservableProperty] private bool redeemBusy;

    /// <summary>Asks the window to open a roblox.com page for the account (Buy Robux, or a gift card on the redeem page).</summary>
    public event Action<Account, string, string>? OpenRobloxPageRequested;

    [RelayCommand]
    private void OpenRedeem()
    {
        if (CurrentAccount == null) { StatusMessage = "Pick an account top right first."; return; }
        RedeemCode = "";
        RedeemResult = "";
        RedeemResultKind = 0;
        ShowRedeem = true;
    }

    [RelayCommand] private void CloseRedeem() { if (!RedeemBusy) ShowRedeem = false; }

    [RelayCommand]
    private async Task Redeem()
    {
        var code = (RedeemCode ?? "").Trim();
        if (code.Length == 0 || RedeemBusy) return;
        if (CurrentAccount is not { } a) { RedeemResult = "Pick an account top right first."; RedeemResultKind = -1; return; }

        // a gift card PIN: Roblox's payment service with its own security check, so its own page (the code filled in)
        if (RobloxEconomy.LooksLikeGiftCard(code))
        {
            ShowRedeem = false;
            OpenRobloxPageRequested?.Invoke(a, "https://www.roblox.com/redeem?code=" + Uri.EscapeDataString(code), "Redeem a gift card");
            StatusMessage = "Gift cards are redeemed on Roblox's own page: your code is filled in there.";
            return;
        }

        RedeemBusy = true;
        RedeemResult = "";
        RedeemResultKind = 0;
        try
        {
            var (ok, check, message) = await RobloxEconomy.RedeemCodeAsync(a.Cookie, code, CancellationToken.None);
            if (check)
            {
                ShowRedeem = false;
                OpenRobloxPageRequested?.Invoke(a, "https://www.roblox.com/redeem?code=" + Uri.EscapeDataString(code), "Redeem Roblox Codes");
                StatusMessage = "Roblox wants a quick security check: finish redeeming on its page (your code is filled in).";
                return;
            }
            RedeemResult = message;
            RedeemResultKind = ok ? 1 : -1;
            if (ok)
            {
                Log(ActivityLog.Purchase, $"{a.Label} redeemed a code: {message}");
                await RefreshRobuxAsync(a);
            }
        }
        catch (RobloxException ex) { RedeemResult = ex.Message; RedeemResultKind = -1; }
        finally { RedeemBusy = false; }
    }
}
