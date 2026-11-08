using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using RoDeck.Models;

namespace RoDeck.Services;

/// <summary>The outcome of a purchase. <see cref="Message"/> is Roblox's reason when it didn't go through.</summary>
public sealed record PurchaseResult(bool Ok, string Message);

/// <summary>
/// Marketplace: search avatar items, item details, ownership, trying on and buying.
/// Roblox allows only about 12 searches a minute, so search pages are remembered for a few minutes.
/// </summary>
public static class RobloxMarket
{
    const string Catalog = "https://catalog.roblox.com";

    // ------------------------------------------------------------------ categories

    static List<MarketChip>? _chips;

    /// <summary>Roblox's own category list (with the IDs its search filters on). Falls back to the main ones.</summary>
    public static async Task<List<MarketChip>> GetCategoriesAsync(CancellationToken ct)
    {
        if (_chips != null) return _chips;
        try
        {
            using var doc = await RobloxHttp.GetJsonAsync($"{Catalog}/v1/search/navigation-menu-items", null, ct);
            var list = new List<MarketChip>();
            foreach (var c in doc.RootElement.GetProperty("categories").EnumerateArray())
            {
                var tax = Str(c, "taxonomy");
                if (tax.Length == 0) continue;
                var subs = new List<MarketChip>();
                if (c.TryGetProperty("subcategories", out var ss) && ss.ValueKind == JsonValueKind.Array)
                    foreach (var s in ss.EnumerateArray())
                        if (Str(s, "taxonomy") is { Length: > 0 } st) subs.Add(new MarketChip { Taxonomy = st, Name = Nice(Str(s, "name")) });
                list.Add(new MarketChip { Taxonomy = tax, Name = Str(c, "name") is "All" ? "All items" : Str(c, "name"), Subs = subs });
            }
            if (list.Count > 0) return _chips = list;
        }
        catch (RobloxException) { }
        return new List<MarketChip>
        {
            new() { Taxonomy = "tZsUsd2BqGViQrJ9Vs3Wah", Name = "All items" },
            new() { Taxonomy = "mwkR3QvDBVzYk4o9RAVVa2", Name = "Body" },
            new() { Taxonomy = "5G3bZScC9Hxp2D2EUtGSNm", Name = "Clothing" },
            new() { Taxonomy = "wNYJso48d1XnhMyFWT3oX3", Name = "Accessories" },
            new() { Taxonomy = "9fPpW3SJ6UkieQU5jJ4dgP", Name = "Animations" },
        };
    }

    /// <summary>"Neck Accessory" → "Neck" (the chip row already says Accessories).</summary>
    static string Nice(string name) => name.EndsWith(" Accessory") ? name[..^10] : name;

    // ------------------------------------------------------------------ search

    public sealed record SearchQuery(string Taxonomy, string Keyword, int Sort, int Aggregation, long? MinPrice, long? MaxPrice, bool LimitedsOnly);

    static readonly ConcurrentDictionary<string, (DateTime At, List<MarketItem> Items, string? Next)> Pages = new();

    /// <summary>One page (60 items) of search results. <paramref name="cookie"/> is optional: signed in, Roblox is a bit more generous.</summary>
    public static async Task<(List<MarketItem> Items, string? Next)> SearchAsync(SearchQuery q, string? cursor, string? cookie, CancellationToken ct)
    {
        var url = $"{Catalog}/v2/search/items/details?taxonomy={Uri.EscapeDataString(q.Taxonomy)}&limit=60&salesTypeFilter={(q.LimitedsOnly ? 2 : 1)}"
                + $"&sortType={q.Sort}"
                + (q.Sort is 1 or 2 ? $"&sortAggregation={q.Aggregation}" : "")
                + (q.Keyword.Length > 0 ? "&keyword=" + Uri.EscapeDataString(q.Keyword) : "")
                + (q.MinPrice is long min ? $"&minPrice={min}" : "")
                + (q.MaxPrice is long max ? $"&maxPrice={max}" : "")
                + (q.MaxPrice == 0 ? "&includeNotForSale=false" : "")
                + (string.IsNullOrEmpty(cursor) ? "" : "&cursor=" + Uri.EscapeDataString(cursor));

        if (Pages.TryGetValue(url, out var known) && DateTime.UtcNow - known.At < TimeSpan.FromMinutes(5))
            return (known.Items.Select(Copy).ToList(), known.Next);

        using var doc = await RobloxHttp.GetJsonAsync(url, cookie, ct);
        var items = new List<MarketItem>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var x in data.EnumerateArray())
                if (Read(x) is { } it && items.All(i => i.Id != it.Id || i.ItemType != it.ItemType)
                    && (q.MaxPrice == null || it.Buyable))   // a price filter means "things I can get for that": skip off-sale ones
                    items.Add(it);
        var next = Str(doc.RootElement, "nextPageCursor");
        var result = (items, string.IsNullOrEmpty(next) ? null : next);
        if (Pages.Count > 60) Pages.Clear();
        Pages[url] = (DateTime.UtcNow, items.Select(Copy).ToList(), result.Item2);
        return result;
    }

    /// <summary>Fresh objects for each showing (the tiles hold per-account state like Owned).</summary>
    static MarketItem Copy(MarketItem m) => new()
    {
        Id = m.Id, ItemType = m.ItemType, AssetType = m.AssetType, BundleType = m.BundleType, Name = m.Name, Description = m.Description,
        CreatorName = m.CreatorName, CreatorId = m.CreatorId, CreatorType = m.CreatorType, CreatorVerified = m.CreatorVerified,
        Price = m.Price, LowestResale = m.LowestResale, PriceStatus = m.PriceStatus, Favorites = m.Favorites, IsLimited = m.IsLimited,
        CollectibleItemId = m.CollectibleItemId, Parts = m.Parts, ThumbUrl = m.ThumbUrl
    };

    /// <summary>Pictures for a list of items (assets and bundles mixed).</summary>
    public static async Task FillThumbsAsync(IList<MarketItem> items, string size, CancellationToken ct)
    {
        var assets = Thumbnails.GetAsync("Asset", items.Where(i => !i.IsBundle).Select(i => i.Id), size, ct);
        var bundles = Thumbnails.GetAsync("BundleThumbnail", items.Where(i => i.IsBundle).Select(i => i.Id), size, ct);
        var a = await assets; var b = await bundles;
        foreach (var i in items)
            if ((i.IsBundle ? b : a).TryGetValue(i.Id, out var url)) i.ThumbUrl = url;
    }

    // ------------------------------------------------------------------ one item

    /// <summary>
    /// Full details as the account sees them: price right now, whether it owns it, and for bundles what's inside.
    /// Without a cookie <see cref="MarketItem.Owned"/> stays null.
    /// </summary>
    public static async Task<MarketItem> GetItemAsync(long id, bool bundle, string? cookie, CancellationToken ct)
    {
        using var doc = await RobloxHttp.GetJsonAsync($"{Catalog}/v1/catalog/items/{id}/details?itemType={(bundle ? "Bundle" : "Asset")}", cookie, ct);
        var item = Read(doc.RootElement) ?? throw new RobloxException("Roblox couldn't find that item.");
        if (cookie != null && doc.RootElement.TryGetProperty("owned", out var o) && o.ValueKind is JsonValueKind.True or JsonValueKind.False)
            item.Owned = o.GetBoolean();
        return item;
    }

    // ------------------------------------------------------------------ trying on

    /// <summary>
    /// Draws a picture of a look without saving it (Roblox's "Try on"): works with items the account doesn't own.
    /// Returns the picture link, or "" when Roblox didn't finish in time.
    /// </summary>
    public static async Task<string> RenderLookAsync(string cookie, AvatarState look, CancellationToken ct)
    {
        var colors = new JsonObject();
        foreach (var (k, v) in look.Colors)
            colors[k.EndsWith("3") ? k[..^1] : k] = "#" + v.TrimStart('#');   // headColor3 → headColor
        var scales = new JsonObject();
        foreach (var (k, v) in look.Scales) scales[k] = Math.Round(v, 3);
        var body = new JsonObject
        {
            ["thumbnailConfig"] = new JsonObject { ["thumbnailId"] = 1, ["thumbnailType"] = "2d", ["size"] = "420x420" },
            ["avatarDefinition"] = new JsonObject
            {
                ["assets"] = new JsonArray(look.Assets.Select(a =>
                {
                    var x = new JsonObject { ["id"] = a.Id };
                    if (a.Meta != null) x["meta"] = a.Meta.DeepClone();
                    return (JsonNode)x;
                }).ToArray()),
                ["bodyColors"] = colors,
                ["scales"] = scales,
                ["playerAvatarType"] = new JsonObject { ["playerAvatarType"] = look.Type }
            }
        }.ToJsonString();

        for (int i = 0; i < 20; i++)   // Roblox answers "Pending" until it's drawn; asking again is how you wait for it
        {
            using var doc = await RobloxHttp.PostJsonAsync("https://avatar.roblox.com/v1/avatar/render", cookie, body, ct);
            var state = Str(doc.RootElement, "state");
            var url = Str(doc.RootElement, "imageUrl");
            if (state == "Completed" && url.Length > 0) return url;
            if (state is "Error" or "Blocked") throw new RobloxException("Roblox couldn't draw this look.");
            await Task.Delay(i < 4 ? 600 : 1000, ct);
        }
        return "";
    }

    // ------------------------------------------------------------------ buying

    /// <summary>
    /// Buys (or, at price 0, gets) the item as the account, at exactly <paramref name="expectedPrice"/>: if Roblox's price
    /// changed in the meantime, Roblox refuses instead of charging something else.
    /// </summary>
    public static async Task<PurchaseResult> BuyAsync(string cookie, long userId, MarketItem item, long expectedPrice, CancellationToken ct)
    {
        if (item.CollectibleItemId.Length > 0)
        {
            var productId = await GetCollectibleProductIdAsync(item, ct);
            if (productId.Length == 0) return new(false, "Roblox didn't say how to buy this item. Try again in a moment.");
            var body = new JsonObject
            {
                ["collectibleItemId"] = item.CollectibleItemId,
                ["collectibleProductId"] = productId,
                ["expectedCurrency"] = 1,
                ["expectedPrice"] = expectedPrice,
                ["expectedPurchaserId"] = userId.ToString(),
                ["expectedPurchaserType"] = "User",
                ["expectedSellerId"] = item.CreatorId,
                ["expectedSellerType"] = item.CreatorType,
                // the same key on every try: Roblox never charges twice for one key, even if the answer got lost
                ["idempotencyKey"] = Guid.NewGuid().ToString()
            }.ToJsonString();
            using var doc = await RobloxHttp.PostJsonAsync(
                $"https://apis.roblox.com/marketplace-sales/v1/item/{item.CollectibleItemId}/purchase-item", cookie, body, ct);
            var r = doc.RootElement;
            bool ok = r.TryGetProperty("purchased", out var p) && p.ValueKind == JsonValueKind.True;
            return new(ok, ok ? "" : Reason(Str(r, "errorMessage") is { Length: > 0 } e ? e : Str(r, "purchaseResult")));
        }

        // older items without a collectible ID: the classic purchase endpoint, which needs the economy product ID
        using (var det = await RobloxHttp.GetJsonAsync($"https://economy.roblox.com/v2/assets/{item.Id}/details", null, ct))
        {
            long productId = Long(det.RootElement, "ProductId");
            if (productId == 0) return new(false, "Roblox didn't say how to buy this item.");
            using var doc = await RobloxHttp.PostJsonAsync($"https://economy.roblox.com/v1/purchases/products/{productId}", cookie,
                new { expectedCurrency = 1, expectedPrice, expectedSellerId = item.CreatorId }, ct);
            var r = doc.RootElement;
            bool ok = r.TryGetProperty("purchased", out var p) && p.ValueKind == JsonValueKind.True;
            return new(ok, ok ? "" : Reason(Str(r, "errorMsg") is { Length: > 0 } e ? e : Str(r, "reason")));
        }
    }

    /// <summary>Players reselling this limited, cheapest first (Roblox's order), one page of 30.</summary>
    public static async Task<(List<MarketSeller> Sellers, string? Next)> GetResellersAsync(string collectibleItemId, string? cursor, CancellationToken ct)
    {
        var url = $"https://apis.roblox.com/marketplace-sales/v1/item/{collectibleItemId}/resellers?limit=30"
                + (string.IsNullOrEmpty(cursor) ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
        using var doc = await RobloxHttp.GetJsonAsync(url, null, ct);
        var list = new List<MarketSeller>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var x in data.EnumerateArray())
            {
                var s = x.TryGetProperty("seller", out var se) ? se : default;
                list.Add(new MarketSeller
                {
                    Name = Str(s, "name"),
                    SellerId = Long(s, "sellerId"),
                    SellerType = Str(s, "sellerType") is { Length: > 0 } t ? t : "User",
                    Verified = s.ValueKind == JsonValueKind.Object && s.TryGetProperty("hasVerifiedBadge", out var v) && v.ValueKind == JsonValueKind.True,
                    Price = Long(x, "price"),
                    Serial = Long(x, "serialNumber"),
                    ProductId = Str(x, "collectibleProductId"),
                    InstanceId = Str(x, "collectibleItemInstanceId")
                });
            }
        var next = Str(doc.RootElement, "nextPageCursor");
        return (list, string.IsNullOrEmpty(next) ? null : next);
    }

    /// <summary>Buys one reseller's copy at exactly its listed price (Roblox refuses if it changed or was sold meanwhile).</summary>
    public static async Task<PurchaseResult> BuyResaleAsync(string cookie, long userId, MarketItem item, MarketSeller seller, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["collectibleItemId"] = item.CollectibleItemId,
            ["collectibleItemInstanceId"] = seller.InstanceId,
            ["collectibleProductId"] = seller.ProductId,
            ["expectedCurrency"] = 1,
            ["expectedPrice"] = seller.Price,
            ["expectedPurchaserId"] = userId.ToString(),
            ["expectedPurchaserType"] = "User",
            ["expectedSellerId"] = seller.SellerId,
            ["expectedSellerType"] = seller.SellerType,
            ["idempotencyKey"] = Guid.NewGuid().ToString()
        }.ToJsonString();
        using var doc = await RobloxHttp.PostJsonAsync(
            $"https://apis.roblox.com/marketplace-sales/v1/item/{item.CollectibleItemId}/purchase-resale", cookie, body, ct);
        var r = doc.RootElement;
        bool ok = r.TryGetProperty("purchased", out var p) && p.ValueKind == JsonValueKind.True;
        return new(ok, ok ? "" : Reason(Str(r, "errorMessage") is { Length: > 0 } e ? e : Str(r, "purchaseResult")));
    }

    static async Task<string> GetCollectibleProductIdAsync(MarketItem item, CancellationToken ct)
    {
        using var doc = await RobloxHttp.PostJsonAsync("https://apis.roblox.com/marketplace-items/v1/items/details", null,
            new { itemIds = new[] { item.CollectibleItemId } }, ct);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
            foreach (var x in doc.RootElement.EnumerateArray())
                if (Str(x, "collectibleItemId") == item.CollectibleItemId) return Str(x, "collectibleProductId");
        return "";
    }

    /// <summary>Roblox's purchase reasons in plain words.</summary>
    static string Reason(string r) => r switch
    {
        "" => "Roblox didn't complete the purchase.",
        _ when r.Contains("AlreadyOwned", StringComparison.OrdinalIgnoreCase) || r.Contains("already own", StringComparison.OrdinalIgnoreCase)
            => "This account already owns it.",
        _ when r.Contains("InsufficientFunds", StringComparison.OrdinalIgnoreCase) || r.Contains("insufficient", StringComparison.OrdinalIgnoreCase)
            => "Not enough Robux on this account.",
        _ when r.Contains("PriceChanged", StringComparison.OrdinalIgnoreCase) || r.Contains("price", StringComparison.OrdinalIgnoreCase)
            => "The price changed. Nothing was charged. Open the item again to see the new price.",
        _ when r.Contains("NotForSale", StringComparison.OrdinalIgnoreCase) || r.Contains("not for sale", StringComparison.OrdinalIgnoreCase)
            => "This item isn't for sale right now.",
        _ => r
    };

    // ------------------------------------------------------------------ reading

    static MarketItem? Read(JsonElement x)
    {
        long id = Long(x, "id");
        if (id == 0) return null;
        bool bundle = Str(x, "itemType") == "Bundle";
        long? price = x.TryGetProperty("price", out var pr) && pr.ValueKind == JsonValueKind.Number ? pr.GetInt64() : null;
        var status = Str(x, "priceStatus");
        if (status is "Off Sale" or "No Resellers") price = null;
        // limiteds Roblox stopped selling come back as price 0 + isOffSale: they're not free, only resellers have them
        if (x.TryGetProperty("isOffSale", out var off) && off.ValueKind == JsonValueKind.True) price = null;
        if (price == 0 && status != "Free" && x.TryGetProperty("isPurchasable", out var ip) && ip.ValueKind == JsonValueKind.False) price = null;

        var restrictions = new List<string>();
        if (x.TryGetProperty("itemRestrictions", out var rs) && rs.ValueKind == JsonValueKind.Array)
            foreach (var r in rs.EnumerateArray()) if (r.ValueKind == JsonValueKind.String) restrictions.Add(r.GetString()!);

        var parts = new List<MarketPart>();
        if (x.TryGetProperty("bundledItems", out var bi) && bi.ValueKind == JsonValueKind.Array)
            foreach (var p in bi.EnumerateArray())
                if (Str(p, "type") == "Asset") parts.Add(new MarketPart(Long(p, "id"), (int)Long(p, "assetType"), Str(p, "name")));

        return new MarketItem
        {
            Id = id,
            ItemType = bundle ? "Bundle" : "Asset",
            AssetType = (int)Long(x, "assetType"),
            BundleType = (int)Long(x, "bundleType"),
            Name = Str(x, "name").Trim(),
            Description = Str(x, "description").Trim(),
            CreatorName = Str(x, "creatorName"),
            CreatorId = Long(x, "creatorTargetId"),
            CreatorType = Str(x, "creatorType") is { Length: > 0 } ctp ? ctp : "User",
            CreatorVerified = x.TryGetProperty("creatorHasVerifiedBadge", out var v) && v.ValueKind == JsonValueKind.True,
            Price = price,
            LowestResale = Long(x, "lowestResalePrice"),
            PriceStatus = status,
            Favorites = Long(x, "favoriteCount"),
            IsLimited = restrictions.Any(r => r is "Limited" or "LimitedUnique" or "Collectible"),
            CollectibleItemId = Str(x, "collectibleItemId"),
            Parts = parts
        };
    }

    static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;
}
