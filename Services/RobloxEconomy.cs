using System.Globalization;
using System.Text.Json;

namespace RoDeck.Services;

/// <summary>One line of the account's Robux history (economy.roblox.com transactions).</summary>
public sealed partial class TxRow(long id, DateTime created, bool pending, string agentName, long agentId, string agentType,
                                  string itemName, long itemId, string itemType, long amount) : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public long Id { get; } = id;
    public DateTime Created { get; } = created;
    public bool Pending { get; } = pending;
    public string AgentName { get; } = agentName;
    public long AgentId { get; } = agentId;
    public string AgentType { get; } = agentType;
    public string ItemName { get; } = itemName;
    public long ItemId { get; } = itemId;
    /// <summary>"Asset", "Bundle", "GamePass", "DeveloperProduct" or "" (Robux, a payout…), whatever spelling Roblox used.</summary>
    public string ItemType { get; } = Normalize(itemType);
    public long Amount { get; } = amount;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(ShowGlyph))]
    private string thumbUrl = "";
    /// <summary>No picture (yet).</summary>
    public bool ShowGlyph => ThumbUrl.Length == 0;
    /// <summary>Plain Robux (bought, Premium, a group payout, a trade): the Robux symbol instead of a picture.</summary>
    public bool IsRobux => !HasPicture;
    /// <summary>Something with a picture: an item, a bundle, a game pass or an in-game product.</summary>
    public bool HasPicture => ItemId > 0 && ItemType.Length > 0;

    static string Normalize(string t) => t.ToLowerInvariant() switch
    {
        "asset" => "Asset",
        "bundle" => "Bundle",
        "gamepass" or "game pass" or "game-pass" => "GamePass",
        "developerproduct" or "developer product" or "product" => "DeveloperProduct",
        _ => "",
    };
    public string WhenText => Created == default ? "" : Created.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture);
    public string AmountText => (Amount < 0 ? "−" : Amount > 0 ? "+" : "") + Math.Abs(Amount).ToString("N0", CultureInfo.CurrentCulture);
    public int Kind => Amount > 0 ? 1 : Amount < 0 ? -1 : 0;
    /// <summary>Opens on the Marketplace (items and bundles; game passes and in-game products aren't sold there).</summary>
    public bool HasItem => ItemId > 0 && ItemType is "Asset" or "Bundle";
    public string ItemText => ItemName.Length > 0 ? ItemName : "Robux";
    public string AgentText => AgentName.Length == 0 ? "" : AgentType == "Group" ? AgentName + " (group)" : AgentName;
    public string PendingText => Pending ? "Pending" : "";
}

/// <summary>A total for the chosen time frame, e.g. "Sales  +1,200".</summary>
public sealed record TxTotal(string Label, long Amount)
{
    public string AmountText => (Amount < 0 ? "−" : Amount > 0 ? "+" : "") + Math.Abs(Amount).ToString("N0", CultureInfo.CurrentCulture);
    public int Kind => Amount > 0 ? 1 : Amount < 0 ? -1 : 0;
}

/// <summary>
/// The Robux side of an account: its transactions and totals (what roblox.com/transactions shows) and redeeming
/// promo / item codes. Gift card PINs go through Roblox's payment service (with its own security check), so those are
/// redeemed on Roblox's own page instead.
/// </summary>
public static class RobloxEconomy
{
    /// <summary>The kinds of history the page shows, as Roblox names them, with the tab text.</summary>
    public static readonly (string Type, string Label)[] Types =
    {
        ("Purchase", "Purchases"),
        ("Sale", "Sales"),
        ("CurrencyPurchase", "Robux bought"),
        ("PremiumStipend", "Premium"),
        ("GroupPayout", "Group payouts"),
        ("TradeRobux", "Trades"),
    };

    // Roblox works the history and the totals out when they're asked for, which can take well over half a minute for a
    // busy account or a whole year. The usual client gives up after 30 s and tries again (starting over each time), so
    // these get their own: one request, as long as the caller lets it run, and Roblox's own wait when it says "too many".
    static readonly HttpClient Slow = CreateSlow();

    static HttpClient CreateSlow()
    {
        var c = new HttpClient(new HttpClientHandler { UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(RobloxApi.Http.DefaultRequestHeaders.UserAgent.ToString());
        return c;
    }

    static async Task<JsonDocument> SlowGetAsync(string url, string cookie, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
            HttpResponseMessage resp;
            try { resp = await Slow.SendAsync(req, ct); }
            catch (HttpRequestException ex) { throw new RobloxException("Roblox isn't answering. Check your internet and try again.", null, ex); }
            using (resp)
            {
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode)
                {
                    try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text); }
                    catch (JsonException ex) { throw new RobloxException("Roblox sent something RoDeck didn't understand.", null, ex); }
                }
                if ((int)resp.StatusCode >= 500 && attempt < 2) { await Task.Delay(2000, ct); continue; }
                int wait = resp.Headers.TryGetValues("x-ratelimit-reset", out var v) && int.TryParse(v.First().Split(',')[0].Trim(), out var n) ? n : 0;
                throw (int)resp.StatusCode switch
                {
                    401 => new RobloxException("This account is signed out. Sign it in again on the Accounts tab.", resp.StatusCode),
                    429 => new RobloxException("Roblox is getting too many requests.", resp.StatusCode) { WaitSeconds = Math.Max(wait, 5) },
                    >= 500 => new RobloxException("Roblox is having trouble right now. Try again in a moment.", resp.StatusCode),
                    _ => new RobloxException($"Roblox answered {(int)resp.StatusCode}.", resp.StatusCode),
                };
            }
        }
    }

    public static async Task<(List<TxRow> Rows, string? Next)> GetTransactionsAsync(string cookie, long userId, string type, string? cursor,
                                                                                    CancellationToken ct)
    {
        var url = $"https://economy.roblox.com/v2/users/{userId}/transactions?transactionType={Uri.EscapeDataString(type)}&limit=100"
                  + (string.IsNullOrEmpty(cursor) ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
        using var doc = await SlowGetAsync(url, cookie, ct);
        var root = doc.RootElement;
        var rows = new List<TxRow>();
        foreach (var t in root.Arr("data"))
        {
            JsonElement agent = t.TryGetProperty("agent", out var ag) ? ag : default;
            JsonElement details = t.TryGetProperty("details", out var de) ? de : default;
            JsonElement currency = t.TryGetProperty("currency", out var cu) ? cu : default;
            DateTime.TryParse(t.Text("created"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var created);
            bool pending = t.ValueKind == JsonValueKind.Object && t.TryGetProperty("isPending", out var p) && p.ValueKind == JsonValueKind.True;
            rows.Add(new TxRow(t.Num("id"), created, pending, agent.Text("name"), agent.Num("id"), agent.Text("type"),
                               details.Text("name"), details.Num("id"), details.Text("type"), currency.Num("amount")));
        }
        var next = root.Text("nextPageCursor");
        return (rows, next.Length > 0 ? next : null);
    }

    // Roblox's names for the totals, in the order the page lists them; anything new it adds is shown too, by its name
    static readonly (string Key, string Label)[] TotalNames =
    {
        ("salesTotal", "Sales of goods"),
        ("currencyPurchasesTotal", "Robux bought"),
        ("premiumStipendsTotal", "Premium stipends"),
        ("premiumPayoutsTotal", "Premium payouts"),
        ("groupPayoutsTotal", "Group payouts"),
        ("affiliateSalesTotal", "Commissions"),
        ("tradeSystemEarningsTotal", "Trades (received)"),
        ("purchasesTotal", "Purchases"),
        ("tradeSystemCostsTotal", "Trades (paid)"),
        ("adSpendTotal", "Ads"),
        ("developerExchangeTotal", "DevEx"),
        ("individualToGroupTotal", "Sent to groups"),
        ("csAdjustmentTotal", "Adjustments by Roblox"),
    };

    /// <summary>
    /// Totals for "Day", "Week", "Month" or "Year": incoming, outgoing, pending and every kind that isn't zero.
    /// </summary>
    public static async Task<(long Incoming, long Outgoing, long Pending, List<TxTotal> Lines)> GetTotalsAsync(
        string cookie, long userId, string timeFrame, CancellationToken ct)
    {
        using var doc = await SlowGetAsync(
            $"https://economy.roblox.com/v2/users/{userId}/transaction-totals?timeFrame={timeFrame}&transactionType=summary", cookie, ct);
        var root = doc.RootElement;
        var all = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind == JsonValueKind.Object)
            foreach (var prop in root.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt64(out var v)) all[prop.Name] = v;

        long incoming = all.GetValueOrDefault("incomingRobuxTotal"), outgoing = all.GetValueOrDefault("outgoingRobuxTotal");
        long pending = all.GetValueOrDefault("pendingRobuxTotal");
        var lines = new List<TxTotal>();
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "incomingRobuxTotal", "outgoingRobuxTotal", "pendingRobuxTotal" };
        foreach (var (key, label) in TotalNames)
        {
            shown.Add(key);
            if (all.TryGetValue(key, out var v) && v != 0) lines.Add(new TxTotal(label, v));
        }
        foreach (var (key, v) in all)
            if (!shown.Contains(key) && v != 0) lines.Add(new TxTotal(Humanize(key), v));
        return (incoming, outgoing, pending, lines);
    }

    /// <summary>"groupAdsRevsharePayoutsTotal" → "Group ads revshare payouts".</summary>
    static string Humanize(string key)
    {
        if (key.EndsWith("Total", StringComparison.Ordinal)) key = key[..^5];
        var sb = new System.Text.StringBuilder();
        foreach (var c in key)
        {
            if (char.IsUpper(c) && sb.Length > 0) sb.Append(' ').Append(char.ToLowerInvariant(c));
            else sb.Append(sb.Length == 0 ? char.ToUpperInvariant(c) : c);
        }
        return sb.ToString();
    }

    static readonly Dictionary<string, (string Path, string Param)> IconServices = new()
    {
        ["Asset"] = ("v1/assets", "assetIds"),
        ["Bundle"] = ("v1/bundles/thumbnails", "bundleIds"),
        ["GamePass"] = ("v1/game-passes", "gamePassIds"),
        ["DeveloperProduct"] = ("v1/developer-products/icons", "developerProductIds"),
    };

    /// <summary>150×150 pictures for these ids of one kind; ones Roblox is still making are asked again a few times.</summary>
    public static async Task<Dictionary<long, string>> GetIconsAsync(string kind, IEnumerable<long> ids, CancellationToken ct)
    {
        var result = new Dictionary<long, string>();
        if (!IconServices.TryGetValue(kind, out var svc)) return result;
        var left = ids.Where(i => i > 0).Distinct().ToList();
        for (int round = 0; round < 4 && left.Count > 0; round++)
        {
            if (round > 0) await Task.Delay(TimeSpan.FromSeconds(2 * round), ct);
            var pending = new List<long>();
            foreach (var chunk in left.Chunk(100))
            {
                using var doc = await RobloxHttp.GetJsonAsync(
                    $"https://thumbnails.roblox.com/{svc.Path}?{svc.Param}={string.Join(',', chunk)}&size=150x150&format=Png&isCircular=false",
                    null, ct);
                foreach (var t in doc.RootElement.Arr("data"))
                {
                    long id = t.Num("targetId");
                    var url = t.Text("imageUrl");
                    if (t.Text("state") == "Completed" && url.Length > 0) result[id] = url;
                    else if (t.Text("state") == "Pending") pending.Add(id);
                }
            }
            left = pending;
        }
        return result;
    }

    /// <summary>A gift card PIN (digits only), which Roblox redeems through its payment service, not here.</summary>
    public static bool LooksLikeGiftCard(string code)
    {
        var c = code.Replace("-", "").Replace(" ", "");
        return c.Length >= 8 && c.All(char.IsDigit);
    }

    /// <summary>
    /// Redeems a promo or item code. Returns Roblox's own message. <c>Check</c> is true when Roblox wants a security
    /// check (a captcha) first: then the code has to be redeemed on Roblox's own page, where you do that check yourself.
    /// </summary>
    public static async Task<(bool Ok, bool Check, string Message)> RedeemCodeAsync(string cookie, string code, CancellationToken ct)
    {
        try
        {
            using var doc = await RobloxHttp.PostJsonAsync("https://billing.roblox.com/v1/promocodes/redeem", cookie, new { code = code.Trim() }, ct);
            var root = doc.RootElement;
            bool ok = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
            var success = root.Text("successMsg");
            var error = root.Text("errorMsg");
            if (error.Contains("captcha", StringComparison.OrdinalIgnoreCase)) return (false, true, "Roblox wants a quick security check first.");
            if (ok || (success.Length > 0 && error.Length == 0))
                return (true, false, success.Length > 0 ? success : "Code redeemed.");
            return (false, false, error.Length > 0 ? error : "Roblox didn't accept that code.");
        }
        catch (RobloxException ex) when (ex.Challenge)
        {
            return (false, true, "Roblox wants a quick security check first.");
        }
    }
}
