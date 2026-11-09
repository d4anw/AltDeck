using System.Text.Json;
using System.Text.Json.Nodes;
using RoDeck.Models;

namespace RoDeck.Services;

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
        bool bundle = string.Equals(Str(target, "itemType"), "Bundle", StringComparison.OrdinalIgnoreCase);
        return LimitedTile.From(assetId, Str(i, "itemName"), Long(i, "recentAveragePrice"), Long(i, "serialNumber"),
                                Str(i, "collectibleItemInstanceId"), i.TryGetProperty("isOnHold", out var h) && h.ValueKind == JsonValueKind.True, bundle);
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
        // that list only has single items: limited bundles (dynamic heads) come from the bundles the account owns
        try { list.AddRange(await GetLimitedBundlesAsync(cookie, userId, ct)); }
        catch (RobloxException ex) when (!ex.TooMany) { }
        catch (JsonException) { }
        return list;
    }

    /// <summary>
    /// The limited bundles someone owns (dynamic heads and the like), one tile per copy. Owned bundles come from the
    /// catalog; the limited ones are picked out with the batch item details; copies, serials and RAP come from what they
    /// can trade, and a bundle that isn't in there (on hold) gets its RAP from its sales.
    /// </summary>
    public static async Task<List<LimitedTile>> GetLimitedBundlesAsync(string cookie, long userId, CancellationToken ct)
    {
        var owned = new List<long>();
        string? cursor = null;
        for (int page = 0; page < 10; page++)
        {
            var url = $"https://catalog.roblox.com/v1/users/{userId}/bundles?limit=100&sortOrder=Desc"
                    + (cursor != null ? "&cursor=" + Uri.EscapeDataString(cursor) : "");
            using var doc = await RobloxHttp.GetJsonAsync(url, cookie, ct);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var x in data.EnumerateArray()) if (Long(x, "id") is > 0 and var id) owned.Add(id);
            cursor = Str(doc.RootElement, "nextPageCursor");
            if (string.IsNullOrEmpty(cursor)) break;
        }
        if (owned.Count == 0) return new();

        // which of them are limited
        var limited = new Dictionary<long, (string Name, string Cid)>();
        foreach (var chunk in owned.Distinct().Chunk(100))
        {
            var body = new { items = chunk.Select(id => new { itemType = "Bundle", id }).ToArray() };
            using var doc = await RobloxHttp.PostJsonAsync("https://catalog.roblox.com/v1/catalog/items/details", null, body, ct);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) continue;
            foreach (var x in data.EnumerateArray())
            {
                var cid = Str(x, "collectibleItemId");
                bool isLimited = x.TryGetProperty("itemRestrictions", out var rs) && rs.ValueKind == JsonValueKind.Array
                                 && rs.EnumerateArray().Any(r => r.ValueKind == JsonValueKind.String && r.GetString() is "Limited" or "LimitedUnique" or "Collectible");
                if (cid.Length > 0 && isLimited) { limited[Long(x, "id")] = (Str(x, "name"), cid); BundleCollectibleIds[Long(x, "id")] = cid; }
            }
        }
        if (limited.Count == 0) return new();

        // copies with serials and RAP, from what they can trade
        var result = new List<LimitedTile>();
        List<LimitedTile> tradable;
        bool tradableKnown = true;
        try { tradable = (await GetTradableAsync(cookie, userId, ct)).Where(t => t.IsBundle).ToList(); }
        catch (RobloxException ex) when (!ex.TooMany) { tradable = new(); tradableKnown = false; }
        int lookups = 0;
        foreach (var (id, (name, cid)) in limited)
        {
            var copies = tradable.Where(t => t.AssetId == id).ToList();
            if (copies.Count > 0) { result.AddRange(copies); continue; }
            long rap = 0;
            if (lookups++ < 40)   // keep a huge collection from flooding Roblox
                try { rap = (await ReadSalesAsync(cid, ct)).Rap; } catch (RobloxException ex) when (!ex.TooMany) { }
            result.Add(LimitedTile.From(id, name, rap, onHold: tradableKnown, bundle: true));   // owned but not tradable: on hold
        }
        return result;
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

    /// <summary>
    /// The item page's numbers: type, copies, best price, RAP and sales from Roblox; acronym, value, demand and trend from
    /// the values list.
    /// </summary>
    public static async Task<ItemStats> GetItemStatsAsync(long assetId, bool bundle, CancellationToken ct)
    {
        if (bundle) return await GetBundleStatsAsync(assetId, ct);
        JsonDocument det;
        try { det = await RobloxHttp.GetJsonAsync($"https://economy.roblox.com/v2/assets/{assetId}/details", null, ct); }
        catch (RobloxException ex) when (!ex.TooMany && !ex.SignedOut) { return await GetBundleStatsAsync(assetId, ct); }   // not an asset: a bundle
        using var _det = det;
        var d = det.RootElement;
        var cid = Str(d, "CollectibleItemId");
        if (cid.Length > 0) CollectibleIds[assetId] = cid;
        var col = d.TryGetProperty("CollectiblesItemDetails", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;

        long made = 0, rap = 0, sold30 = 0;
        var points = new List<(DateTime, long)>();
        var old = RobloxHttp.GetJsonAsync($"https://economy.roblox.com/v1/assets/{assetId}/resale-data", null, ct);
        if (cid.Length > 0)
        {
            using var rd = await RobloxHttp.GetJsonAsync($"https://apis.roblox.com/marketplace-sales/v1/item/{cid}/resale-data", null, ct);
            var r = rd.RootElement;
            rap = r.TryGetProperty("recentAveragePrice", out var ra) && ra.ValueKind == JsonValueKind.Number ? (long)Math.Round(ra.GetDouble()) : 0;
            if (r.TryGetProperty("priceDataPoints", out var pd) && pd.ValueKind == JsonValueKind.Array)
                foreach (var p in pd.EnumerateArray())
                    if (Date(p, "date") is { } day && day != default && Long(p, "value") > 0) points.Add((day, Long(p, "value")));
            var since = DateTime.UtcNow.AddDays(-30);
            if (r.TryGetProperty("volumeDataPoints", out var vd) && vd.ValueKind == JsonValueKind.Array)
                foreach (var p in vd.EnumerateArray())
                    if (Date(p, "date") > since) sold30 += Long(p, "value");
            points.Sort((x, y) => x.Item1.CompareTo(y.Item1));
            Histories[assetId] = (DateTime.UtcNow, points);
        }
        try { using var o = await old; made = Long(o.RootElement, "assetStock"); if (rap == 0) rap = Long(o.RootElement, "recentAveragePrice"); }
        catch (RobloxException) { }

        var v = LimitedValues.Get(assetId);
        long copies = Long(col, "TotalQuantity");
        return new ItemStats
        {
            Type = Models.MarketItem.AssetKind((int)Long(d, "AssetTypeId")),
            Acronym = v?.Acronym ?? "",
            LimitedU = d.TryGetProperty("IsLimitedUnique", out var lu) && lu.ValueKind == JsonValueKind.True || made > 0,
            Copies = copies > 0 ? copies : made,
            Made = made,
            BestPrice = Long(col, "CollectibleLowestResalePrice"),
            Rap = rap > 0 ? rap : v?.Rap ?? 0,
            Value = v?.Value ?? -1,
            Demand = v?.Demand ?? -1,
            Trend = v?.Trend ?? -1,
            Sold30 = sold30,
            History = points
        };
    }

    /// <summary>A limited bundle (a dynamic head, for one): the catalog has its numbers, the marketplace its sales.</summary>
    static async Task<ItemStats> GetBundleStatsAsync(long bundleId, CancellationToken ct)
    {
        using var det = await RobloxHttp.GetJsonAsync($"https://catalog.roblox.com/v1/catalog/items/{bundleId}/details?itemType=Bundle", null, ct);
        var d = det.RootElement;
        var cid = Str(d, "collectibleItemId");
        if (cid.Length > 0) BundleCollectibleIds[bundleId] = cid;
        var (rap, sold30, points) = cid.Length > 0 ? await ReadSalesAsync(cid, ct) : (0, 0, new List<(DateTime, long)>());
        if (cid.Length > 0) BundleHistories[bundleId] = (DateTime.UtcNow, points);
        bool lu = d.TryGetProperty("itemRestrictions", out var rs) && rs.ValueKind == JsonValueKind.Array
                  && rs.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "LimitedUnique");
        long copies = Long(d, "totalQuantity");
        long best = Long(d, "lowestResalePrice");
        return new ItemStats
        {
            Type = Long(d, "bundleType") == 4 ? "Dynamic head" : "Bundle",
            LimitedU = lu,
            Copies = copies,
            Made = copies,
            BestPrice = best > 0 ? best : Long(d, "lowestPrice"),
            Rap = rap,
            Sold30 = sold30,
            History = points
        };
    }

    /// <summary>The marketplace's sales for a collectible: RAP, copies sold in the last 30 days, and the price per day.</summary>
    static async Task<(long Rap, long Sold30, List<(DateTime, long)> Points)> ReadSalesAsync(string cid, CancellationToken ct)
    {
        using var rd = await RobloxHttp.GetJsonAsync($"https://apis.roblox.com/marketplace-sales/v1/item/{cid}/resale-data", null, ct);
        var r = rd.RootElement;
        long rap = r.TryGetProperty("recentAveragePrice", out var ra) && ra.ValueKind == JsonValueKind.Number ? (long)Math.Round(ra.GetDouble()) : 0;
        var points = new List<(DateTime, long)>();
        if (r.TryGetProperty("priceDataPoints", out var pd) && pd.ValueKind == JsonValueKind.Array)
            foreach (var p in pd.EnumerateArray())
                if (Date(p, "date") is { } day && day != default && Long(p, "value") > 0) points.Add((day, Long(p, "value")));
        long sold30 = 0;
        var since = DateTime.UtcNow.AddDays(-30);
        if (r.TryGetProperty("volumeDataPoints", out var vd) && vd.ValueKind == JsonValueKind.Array)
            foreach (var p in vd.EnumerateArray())
                if (Date(p, "date") > since) sold30 += Long(p, "value");
        points.Sort((x, y) => x.Item1.CompareTo(y.Item1));
        if (rap == 0 && points.Count > 0) rap = points[^1].Item2;
        return (rap, sold30, points);
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<long, string> BundleCollectibleIds = new();
    static readonly System.Collections.Concurrent.ConcurrentDictionary<long, (DateTime At, List<(DateTime, long)> Points)> BundleHistories = new();
    static readonly System.Collections.Concurrent.ConcurrentDictionary<long, string> CollectibleIds = new();
    static readonly System.Collections.Concurrent.ConcurrentDictionary<long, (DateTime At, List<(DateTime, long)> Points)> Histories = new();

    /// <summary>
    /// The average sale price per day (what the RAP follows), oldest first, from Roblox's marketplace. Remembered for 15
    /// minutes, so hovering the same item again is instant.
    /// </summary>
    public static async Task<List<(DateTime Day, long Price)>> GetPriceHistoryAsync(long assetId, CancellationToken ct, bool bundle = false)
    {
        if (bundle)
        {
            if (BundleHistories.TryGetValue(assetId, out var kb) && DateTime.UtcNow - kb.At < TimeSpan.FromMinutes(15)) return kb.Points;
            if (!BundleCollectibleIds.TryGetValue(assetId, out var bcid))
            {
                using var bd = await RobloxHttp.GetJsonAsync($"https://catalog.roblox.com/v1/catalog/items/{assetId}/details?itemType=Bundle", null, ct);
                bcid = Str(bd.RootElement, "collectibleItemId");
                BundleCollectibleIds[assetId] = bcid;
            }
            var pts = bcid.Length > 0 ? (await ReadSalesAsync(bcid, ct)).Points : new List<(DateTime, long)>();
            if (BundleHistories.Count > 300) BundleHistories.Clear();
            BundleHistories[assetId] = (DateTime.UtcNow, pts);
            return pts;
        }
        if (Histories.TryGetValue(assetId, out var known) && DateTime.UtcNow - known.At < TimeSpan.FromMinutes(15)) return known.Points;
        if (!CollectibleIds.TryGetValue(assetId, out var cid))
        {
            JsonDocument det;
            try { det = await RobloxHttp.GetJsonAsync($"https://economy.roblox.com/v2/assets/{assetId}/details", null, ct); }
            catch (RobloxException ex) when (!ex.TooMany && !ex.SignedOut) { return await GetPriceHistoryAsync(assetId, ct, bundle: true); }
            using (det) cid = Str(det.RootElement, "CollectibleItemId");
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
