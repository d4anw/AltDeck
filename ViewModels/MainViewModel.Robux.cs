using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoDeck.Controls;
using RoDeck.Models;
using RoDeck.Services;

namespace RoDeck.ViewModels;

// The Robux pill's menu: My Transactions (a page here) and Redeem Roblox Codes (Roblox's page), for the account top right.
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

    // Roblox adds up the totals when asked, which can take most of a minute, and allows about 10 a minute. So the last
    // totals of every account + time frame are kept (also on disk): opening the page shows them at once ("updated 18:42")
    // and fresh ones replace them quietly. This month's are fetched in the background after start-up.
    private sealed record SavedTotals(DateTime At, long In, long Out, long Pending, List<TxTotal> Lines);
    private Dictionary<string, SavedTotals>? _txTotalsCache;
    private readonly HashSet<string> _txTotalsBusy = new();
    private int _txTotalsAsk;
    [ObservableProperty] private string txTotalsUpdated = "";

    private static string TotalsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoDeck", "cache", "txtotals.json");

    private Dictionary<string, SavedTotals> TotalsCache
    {
        get
        {
            if (_txTotalsCache != null) return _txTotalsCache;
            try
            {
                _txTotalsCache = File.Exists(TotalsFile)
                    ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, SavedTotals>>(File.ReadAllText(TotalsFile))
                    : null;
            }
            catch { _txTotalsCache = null; }
            return _txTotalsCache ??= new();
        }
    }

    private void SaveTotals()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TotalsFile)!);
            // only accounts that are still in RoDeck
            var ids = Accounts.Select(x => x.Id).ToHashSet();
            var keep = TotalsCache.Where(kv => ids.Contains(kv.Key.Split('|')[0])).ToDictionary(kv => kv.Key, kv => kv.Value);
            File.WriteAllText(TotalsFile, System.Text.Json.JsonSerializer.Serialize(keep));
        }
        catch { }
    }

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

    /// <summary>Refresh: the list again, and the totals quietly (the ones on screen stay until the new ones are in).</summary>
    [RelayCommand]
    private Task RefreshTransactions()
    {
        if (CurrentAccount is { UserId: > 0 } a && TotalsCache.ContainsKey($"{a.Id}|{TxTimeFrame}"))
            _ = RefreshTotalsQuietlyAsync(a, TxTimeFrame);
        else _ = LoadTxTotalsAsync();
        return LoadTxAsync();
    }

    private void ShowTotals(SavedTotals t)
    {
        TxTotals.Clear();
        TxIncoming = "+" + t.In.ToString("N0");
        TxOutgoing = (t.Out > 0 ? "−" : "") + Math.Abs(t.Out).ToString("N0");
        TxPending = t.Pending.ToString("N0");
        foreach (var l in t.Lines) TxTotals.Add(l);
        TxTotalsUpdated = UpdatedText(t.At);
    }

    private static string UpdatedText(DateTime at) =>
        "Updated " + (at.Date == DateTime.Today ? at.ToString("HH:mm") : at.ToString("d MMM, HH:mm"));

    private Task LoadTxTotalsAsync()
    {
        int ask = ++_txTotalsAsk;
        var frame = TxTimeFrame;
        // known already: on screen at once, and fresh ones in the background when these are older than 5 minutes
        if (CurrentAccount is { UserId: > 0 } known && TotalsCache.TryGetValue($"{known.Id}|{frame}", out var saved))
        {
            TxTotalsLoad.Cancel();
            TxTotalsLoad.State = LoadState.Ready;
            ShowTotals(saved);
            if (DateTime.Now - saved.At > TimeSpan.FromMinutes(5)) _ = RefreshTotalsQuietlyAsync(known, frame);
            return Task.CompletedTask;
        }
        return TxTotalsLoad.RunAsync(async ct =>
        {
            TxTotals.Clear();
            TxIncoming = TxOutgoing = TxPending = TxTotalsUpdated = "";
            if (CurrentAccount is not { UserId: > 0 } a) throw new RobloxException("Pick an account top right first.");
            await Task.Delay(350, ct);   // clicking through the time frames: only the last one is asked
            if (ask != _txTotalsAsk) return true;
            _ = SayStillLoadingAsync(TxTotalsLoad, ct, frame switch
            {
                "Day" => "Roblox is adding up the past day…",
                "Week" => "Roblox is adding up the past week…",
                "Year" => "Roblox is adding up the whole year. That can take a minute…",
                _ => "Roblox is adding up the past month. That can take a while…",
            });
            var t = await FetchTotalsAsync(a, frame, ct);
            if (a != CurrentAccount || frame != TxTimeFrame) return true;
            ShowTotals(t);
            return true;
        });
    }

    /// <summary>Asks Roblox for the totals (up to 2 minutes) and keeps them.</summary>
    private async Task<SavedTotals> FetchTotalsAsync(Account a, string frame, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var (inc, outg, pend, lines) = await RobloxEconomy.GetTotalsAsync(a.Cookie, a.UserId, frame, limit.Token);
            var t = new SavedTotals(DateTime.Now, inc, outg, pend, lines);
            TotalsCache[$"{a.Id}|{frame}"] = t;
            SaveTotals();
            return t;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RobloxException("Roblox didn't finish adding this up. Press Try again in a moment.");
        }
    }

    /// <summary>Fresh totals while the old ones stay on screen ("Updated 18:42 · updating…").</summary>
    private async Task RefreshTotalsQuietlyAsync(Account a, string frame)
    {
        var key = $"{a.Id}|{frame}";
        if (!_txTotalsBusy.Add(key)) return;
        bool Showing() => CurrentAccount == a && TxTimeFrame == frame && Page == "Transactions";
        try
        {
            if (Showing() && TotalsCache.TryGetValue(key, out var old)) TxTotalsUpdated = UpdatedText(old.At) + " · updating…";
            var t = await FetchTotalsAsync(a, frame, CancellationToken.None);
            if (Showing()) ShowTotals(t);
        }
        catch (RobloxException)
        {
            if (Showing() && TotalsCache.TryGetValue(key, out var old)) TxTotalsUpdated = UpdatedText(old.At) + " · couldn't update just now";
        }
        finally { _txTotalsBusy.Remove(key); }
    }

    /// <summary>After start-up and after switching account: this month's totals in the background, if they're old.</summary>
    private async Task PrefetchTotalsAsync(Account? a)
    {
        if (a is not { UserId: > 0 }) return;
        await Task.Delay(TimeSpan.FromSeconds(20));
        if (CurrentAccount != a) return;
        if (TotalsCache.TryGetValue($"{a.Id}|Month", out var t) && DateTime.Now - t.At < TimeSpan.FromMinutes(30)) return;
        await RefreshTotalsQuietlyAsync(a, "Month");
    }

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

    /// <summary>Asks the window to open a roblox.com page for the account (Buy Robux, Redeem Roblox Codes).</summary>
    public event Action<Account, string, string>? OpenRobloxPageRequested;

    /// <summary>
    /// Roblox's own redeem page, signed in as the account top right. Gift cards add money to the account there, which you
    /// then turn into Robux or a subscription with Roblox's own steps (and security check), so RoDeck doesn't redeem itself.
    /// </summary>
    [RelayCommand]
    private void OpenRedeem()
    {
        if (CurrentAccount is not { } a) { StatusMessage = "Pick an account top right first."; return; }
        OpenRobloxPageRequested?.Invoke(a, "https://www.roblox.com/redeem", "Redeem Roblox Codes");
    }
}
