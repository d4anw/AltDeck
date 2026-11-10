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
    public string ItemType { get; } = itemType;
    public long Amount { get; } = amount;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string thumbUrl = "";
    public string WhenText => Created == default ? "" : Created.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture);
    public string AmountText => (Amount < 0 ? "−" : Amount > 0 ? "+" : "") + Math.Abs(Amount).ToString("N0", CultureInfo.CurrentCulture);
    public int Kind => Amount > 0 ? 1 : Amount < 0 ? -1 : 0;
    public bool HasItem => ItemId > 0 && ItemType is "Asset" or "Bundle" or "GamePass";
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

    public static async Task<(List<TxRow> Rows, string? Next)> GetTransactionsAsync(string cookie, long userId, string type, string? cursor,
                                                                                    CancellationToken ct)
    {
        var url = $"https://economy.roblox.com/v2/users/{userId}/transactions?transactionType={Uri.EscapeDataString(type)}&limit=100"
                  + (string.IsNullOrEmpty(cursor) ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
        using var doc = await RobloxHttp.GetJsonAsync(url, cookie, ct);
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
        using var doc = await RobloxHttp.GetJsonAsync(
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
