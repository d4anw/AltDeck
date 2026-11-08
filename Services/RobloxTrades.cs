using System.Text.Json;
using System.Text.Json.Nodes;
using AltDeck.Models;

namespace AltDeck.Services;

/// <summary>Resale numbers for one limited from Roblox: RAP, how often it sold, copies, and the RAP history.</summary>
public sealed record ResaleData(long Rap, long Sales, long Stock, long Remaining, List<(DateTime Day, long Price)> History);

/// <summary>
/// Trades and limiteds on Roblox: the trade lists, one trade, accept / decline / counter / send, what someone can trade,
/// an account's limiteds and an item's resale data. Every change is made as the given account.
/// </summary>
public static class RobloxTrades
{
    const string Trades = "https://trades.roblox.com";

    // ------------------------------------------------------------------ the account itself

    /// <summary>Whether the account may trade at all (Roblox needs Premium for it), and Roblox's reason when not.</summary>
    public static async Task<(bool Can, string Why)> CanTradeAsync(string cookie, CancellationToken ct)
    {
        using var doc = await RobloxHttp.GetJsonAsync($"{Trades}/v2/users/me/can-trade", cookie, ct);
        var r = doc.RootElement;
        bool can = r.TryGetProperty("canTrade", out var c) && c.ValueKind == JsonValueKind.True;
        var why = Str(r, "tradeEligibility");
        return (can, can ? "" : why switch
        {
            "" => "Roblox doesn't allow this account to trade.",
            _ when why.Contains("Premium", StringComparison.OrdinalIgnoreCase) || why.Contains("Membership", StringComparison.OrdinalIgnoreCase)
                => "This account needs Premium to trade.",
            _ when why.Contains("Age", StringComparison.OrdinalIgnoreCase) || why.Contains("Verif", StringComparison.OrdinalIgnoreCase)
                => "Roblox needs this account to be verified before it can trade.",
            _ => $"Roblox doesn't allow this account to trade ({why})."
        });
    }

    // ------------------------------------------------------------------ trade lists

    /// <summary>"Inbound", "Outbound", "Completed" or "Inactive": one page of 25, newest first.</summary>
    public static async Task<(List<TradeRow> Rows, string? Next)> GetTradesAsync(string cookie, string type, string? cursor, CancellationToken ct)
    {
        var url = $"{Trades}/v1/trades/{type}?limit=25&sortOrder=Desc" + (string.IsNullOrEmpty(cursor) ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
        using var doc = await RobloxHttp.GetJsonAsync(url, cookie, ct);
        var rows = new List<TradeRow>();
        foreach (var t in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var u = t.TryGetProperty("user", out var uu) ? uu : default;
            rows.Add(new TradeRow
            {
                Id = Long(t, "id"),
                PartnerId = Long(u, "id"),
                PartnerName = Str(u, "name"),
                PartnerDisplay = Str(u, "displayName"),
                Created = Date(t, "created"),
                Expiration = Date(t, "expiration"),
                Status = Str(t, "status"),
                IsActive = t.TryGetProperty("isActive", out var a) && a.ValueKind == JsonValueKind.True
            });
        }
        var next = Str(doc.RootElement, "nextPageCursor");
        return (rows, string.IsNullOrEmpty(next) ? null : next);
    }

    public static async Task<int> GetInboundCountAsync(string cookie, CancellationToken ct)
    {
        using var doc = await RobloxHttp.GetJsonAsync($"{Trades}/v1/trades/Inbound/count", cookie, ct);
        return (int)Long(doc.RootElement, "count");
    }

    /// <summary>One trade with both sides, as seen by <paramref name="myUserId"/>.</summary>
    public static async Task<TradeDetail> GetTradeAsync(string cookie, long tradeId, long myUserId, CancellationToken ct)
    {
        using var doc = await RobloxHttp.GetJsonAsync($"{Trades}/v2/trades/{tradeId}", cookie, ct);
        var r = doc.RootElement;
        var a = ReadSide(r.TryGetProperty("participantAOffer", out var pa) ? pa : default);
        var b = ReadSide(r.TryGetProperty("participantBOffer", out var pb) ? pb : default);
        return new TradeDetail
        {
            Id = tradeId,
            Status = Str(r, "status"),
            Mine = b.UserId == myUserId ? b : a,
            Theirs = b.UserId == myUserId ? a : b
        };
    }

    static TradeSide ReadSide(JsonElement offer)
    {
        if (offer.ValueKind != JsonValueKind.Object) return new TradeSide();
        var u = offer.TryGetProperty("user", out var uu) ? uu : default;
        var items = new List<LimitedTile>();
        if (offer.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var i in arr.EnumerateArray()) items.Add(ReadInstance(i));
        return new TradeSide { UserId = Long(u, "id"), UserName = Str(u, "name"), Robux = Long(offer, "robux"), Items = items };
    }

    /// <summary>A TradableItemInstance: {collectibleItemInstanceId, itemTarget{targetId}, itemName, serialNumber, recentAveragePrice, isOnHold}</summary>
    static LimitedTile ReadInstance(JsonElement i)
    {
        var target = i.TryGetProperty("itemTarget", out var tt) ? tt : default;
        long assetId = long.TryParse(Str(target, "targetId"), out var id) ? id : Long(target, "targetId");
        return LimitedTile.From(assetId, Str(i, "itemName"), Long(i, "recentAveragePrice"), Long(i, "serialNumber"),
                                Str(i, "collectibleItemInstanceId"), i.TryGetProperty("isOnHold", out var h) && h.ValueKind == JsonValueKind.True);
    }

    // ------------------------------------------------------------------ acting on trades

    public static async Task AcceptAsync(string cookie, long tradeId, CancellationToken ct = default)
    {
        using var _ = await RobloxHttp.PostJsonAsync($"{Trades}/v1/trades/{tradeId}/accept", cookie, new { }, ct);
    }

    /// <summary>Declines an inbound trade, or cancels one you sent.</summary>
    public static async Task DeclineAsync(string cookie, long tradeId, CancellationToken ct = default)
    {
        using var _ = await RobloxHttp.PostJsonAsync($"{Trades}/v1/trades/{tradeId}/decline", cookie, new { }, ct);
    }

    /// <summary>Sends a new trade (or, with <paramref name="counterOf"/>, a counter to that trade). Returns the trade's ID.</summary>
    public static async Task<long> SendAsync(string cookie, long myUserId, IEnumerable<string> give, long giveRobux,
                                             long partnerId, IEnumerable<string> get, long getRobux, long? counterOf = null,
                                             CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["senderOffer"] = new JsonObject
            {
                ["userId"] = myUserId, ["robux"] = giveRobux,
                ["collectibleItemInstanceIds"] = new JsonArray(give.Select(x => (JsonNode)x).ToArray())
            },
            ["recipientOffer"] = new JsonObject
            {
                ["userId"] = partnerId, ["robux"] = getRobux,
                ["collectibleItemInstanceIds"] = new JsonArray(get.Select(x => (JsonNode)x).ToArray())
            }
        }.ToJsonString();
        var url = counterOf is long id ? $"{Trades}/v2/trades/{id}/counter" : $"{Trades}/v2/trades/send";
        using var doc = await RobloxHttp.PostJsonAsync(url, cookie, body, ct);
        return Long(doc.RootElement, "tradeId") is > 0 and var t ? t : Long(doc.RootElement, "id");
    }

    // ------------------------------------------------------------------ items

    /// <summary>
    /// Every copy of every limited someone can trade (one tile per copy), as seen by the account. Works for your own
    /// accounts and for the person you trade with.
    /// </summary>
    public static async Task<List<LimitedTile>> GetTradableAsync(string cookie, long userId, CancellationToken ct)
    {
        var list = new List<LimitedTile>();
        string? cursor = null;
        for (int page = 0; page < 20; page++)
        {
            var url = $"{Trades}/v2/users/{userId}/tradableItems?limit=50&sortBy=CreationTime&sortOrder=2"
                    + (cursor != null ? "&cursor=" + Uri.EscapeDataString(cursor) : "");
            using var doc = await RobloxHttp.GetJsonAsync(url, cookie, ct);
            if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var it in items.EnumerateArray())
                    if (it.TryGetProperty("instances", out var inst) && inst.ValueKind == JsonValueKind.Array)
                        foreach (var i in inst.EnumerateArray()) list.Add(ReadInstance(i));
            cursor = Str(doc.RootElement, "nextPageCursor");
            if (string.IsNullOrEmpty(cursor)) break;
        }
        return list;
    }

    /// <summary>All the account's limiteds (one tile per copy), for its inventory value.</summary>
    public static async Task<List<LimitedTile>> GetCollectiblesAsync(string cookie, long userId, CancellationToken ct)
    {
        var list = new List<LimitedTile>();
        string? cursor = null;
        for (int page = 0; page < 30; page++)
        {
            var url = $"https://inventory.roblox.com/v1/users/{userId}/assets/collectibles?limit=100&sortOrder=Desc"
                    + (cursor != null ? "&cursor=" + Uri.EscapeDataString(cursor) : "");
            using var doc = await RobloxHttp.GetJsonAsync(url, cookie, ct);
            foreach (var x in doc.RootElement.GetProperty("data").EnumerateArray())
                list.Add(LimitedTile.From(Long(x, "assetId"), Str(x, "name"), Long(x, "recentAveragePrice"), Long(x, "serialNumber"),
                                          "", x.TryGetProperty("isOnHold", out var h) && h.ValueKind == JsonValueKind.True));
            cursor = Str(doc.RootElement, "nextPageCursor");
            if (string.IsNullOrEmpty(cursor)) break;
        }
        return list;
    }

    /// <summary>Roblox's resale numbers for a limited (public).</summary>
    public static async Task<ResaleData> GetResaleDataAsync(long assetId, CancellationToken ct)
    {
        var historyTask = GetPriceHistoryAsync(assetId, ct);
        using var doc = await RobloxHttp.GetJsonAsync($"https://economy.roblox.com/v1/assets/{assetId}/resale-data", null, ct);
        var r = doc.RootElement;
        List<(DateTime, long)> points;
        try { points = await historyTask; }
        catch (RobloxException) { points = new(); }
        return new ResaleData(Long(r, "recentAveragePrice"), Long(r, "sales"), Long(r, "assetStock"), Long(r, "numberRemaining"), points);
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<long, string> CollectibleIds = new();
    static readonly System.Collections.Concurrent.ConcurrentDictionary<long, (DateTime At, List<(DateTime, long)> Points)> Histories = new();

    /// <summary>
    /// The average sale price per day (what the RAP follows), oldest first, from Roblox's marketplace. Remembered for 15
    /// minutes, so hovering the same item again is instant.
    /// </summary>
    public static async Task<List<(DateTime Day, long Price)>> GetPriceHistoryAsync(long assetId, CancellationToken ct)
    {
        if (Histories.TryGetValue(assetId, out var known) && DateTime.UtcNow - known.At < TimeSpan.FromMinutes(15)) return known.Points;
        if (!CollectibleIds.TryGetValue(assetId, out var cid))
        {
            using var det = await RobloxHttp.GetJsonAsync($"https://economy.roblox.com/v2/assets/{assetId}/details", null, ct);
            cid = Str(det.RootElement, "CollectibleItemId");
            CollectibleIds[assetId] = cid;
        }
        var points = new List<(DateTime, long)>();
        if (cid.Length > 0)
        {
            using var doc = await RobloxHttp.GetJsonAsync($"https://apis.roblox.com/marketplace-sales/v1/item/{cid}/resale-data", null, ct);
            if (doc.RootElement.TryGetProperty("priceDataPoints", out var pd) && pd.ValueKind == JsonValueKind.Array)
                foreach (var p in pd.EnumerateArray())
                    if (Date(p, "date") is { } d && d != default && Long(p, "value") > 0) points.Add((d, Long(p, "value")));
        }
        points.Sort((x, y) => x.Item1.CompareTo(y.Item1));
        if (Histories.Count > 300) Histories.Clear();
        Histories[assetId] = (DateTime.UtcNow, points);
        return points;
    }

    // ------------------------------------------------------------------ helpers

    static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;
    static DateTime Date(JsonElement e, string name) =>
        DateTime.TryParse(Str(e, name), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : default;
}
