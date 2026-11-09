using System.Collections.Concurrent;
using System.Text.Json;
using RoDeck.Models;

namespace RoDeck.Services;

/// <summary>
/// Limited values from Rolimons (the public values list most traders use): RAP, value, demand, trend and flags for
/// every limited. Fetched at most every 5 minutes, remembered on disk so the app has numbers right away next time.
/// Also the live deal feed: new lowest prices as they get listed.
/// </summary>
public static class LimitedValues
{
    static readonly ConcurrentDictionary<long, LimitedInfo> Items = new();
    static DateTime _loadedAt = DateTime.MinValue;
    static readonly SemaphoreSlim Gate = new(1);
    static string CacheFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoDeck", "cache", "values.json");

    public static int Count => Items.Count;
    public static DateTime LoadedAt => _loadedAt;

    /// <summary>The info for one limited, or null when it isn't a limited Rolimons knows.</summary>
    public static LimitedInfo? Get(long assetId) => Items.TryGetValue(assetId, out var i) ? i : null;

    public static List<LimitedInfo> All => Items.Values.ToList();

    /// <summary>Makes sure the values are at most 5 minutes old (or uses the last good copy when Rolimons doesn't answer).</summary>
    public static async Task EnsureAsync(CancellationToken ct, bool force = false, TimeSpan? maxAge = null)
    {
        var age = maxAge ?? TimeSpan.FromMinutes(5);
        if (!force && Items.Count > 0 && DateTime.UtcNow - _loadedAt < age) return;
        await Gate.WaitAsync(ct);
        try
        {
            if (!force && Items.Count > 0 && DateTime.UtcNow - _loadedAt < age) return;
            if (Items.Count == 0) LoadDisk();
            try
            {
                var text = await RobloxHttp.SendAsync(HttpMethod.Get, "https://api.rolimons.com/items/v2/itemdetails", null, null, ct);
                if (Parse(text) > 0)
                {
                    _loadedAt = DateTime.UtcNow;
                    try { Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!); File.WriteAllText(CacheFile, text); } catch { }
                }
            }
            catch (RobloxException) when (Items.Count > 0) { }   // keep the last values; try again next time
            catch (JsonException) when (Items.Count > 0) { }     // a broken answer: the same
        }
        finally { Gate.Release(); }
    }

    static void LoadDisk()
    {
        try
        {
            if (!File.Exists(CacheFile)) return;
            if (Parse(File.ReadAllText(CacheFile)) > 0) _loadedAt = File.GetLastWriteTimeUtc(CacheFile);
        }
        catch { }
    }

    /// <summary>{"items":{"1029025":["Name","CF",rap,value,defaultValue,demand,trend,projected,hyped,rare,...]}}</summary>
    static int Parse(string text)
    {
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object) return 0;
        int n = 0;
        foreach (var p in items.EnumerateObject())
        {
            if (!long.TryParse(p.Name, out var id) || p.Value.ValueKind != JsonValueKind.Array) continue;
            var a = p.Value.EnumerateArray().ToList();
            long L(int i) => i < a.Count && a[i].ValueKind == JsonValueKind.Number && a[i].TryGetInt64(out var v) ? v : -1;
            string S(int i) => i < a.Count && a[i].ValueKind == JsonValueKind.String ? a[i].GetString() ?? "" : "";
            Items[id] = new LimitedInfo
            {
                AssetId = id, Name = S(0), Acronym = S(1), Rap = Math.Max(0, L(2)), Value = L(3),
                Demand = (int)L(5), Trend = (int)L(6), Projected = L(7) == 1, Hyped = L(8) == 1, Rare = L(9) == 1
            };
            n++;
        }
        return n;
    }

    /// <summary>Updates the RAP in memory (from the deal feed) so deals are measured against the newest number.</summary>
    static void SetRap(long id, long rap) { if (Items.TryGetValue(id, out var i)) Items[id] = i with { Rap = rap }; }

    // ------------------------------------------------------------------ best prices of every limited

    /// <summary>
    /// The cheapest price right now for these limiteds (Roblox's batch item details, up to 100 per request).
    /// Items nobody is selling are left out.
    /// </summary>
    public static async Task<Dictionary<long, long>> GetBestPricesAsync(IReadOnlyList<long> assetIds, string? cookie, CancellationToken ct)
    {
        var body = new { items = assetIds.Select(id => new { itemType = "Asset", id }).ToArray() };
        using var doc = await RobloxHttp.PostJsonAsync("https://catalog.roblox.com/v1/catalog/items/details", cookie, body, ct);
        var result = new Dictionary<long, long>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var x in data.EnumerateArray())
            {
                long id = x.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt64() : 0;
                long p = x.TryGetProperty("lowestResalePrice", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt64() : 0;
                if (id > 0 && p > 0) result[id] = p;
            }
        return result;
    }

    /// <summary>
    /// One limited's cheapest copy for sale right now (0 = nobody sells), from the economy service: quick, and not the
    /// catalog's busy batch that the Limiteds table and the deals scan share.
    /// </summary>
    public static async Task<long> GetBestPriceAsync(long assetId, CancellationToken ct)
    {
        using var doc = await RobloxHttp.GetJsonAsync($"https://economy.roblox.com/v2/assets/{assetId}/details", null, ct);
        return doc.RootElement.TryGetProperty("CollectiblesItemDetails", out var c) && c.ValueKind == JsonValueKind.Object
               && c.TryGetProperty("CollectibleLowestResalePrice", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;
    }

    /// <summary>A limited's marketplace ID (needed to see its sellers and buy one), remembered. "" when it has none.</summary>
    public static async Task<string> GetCollectibleIdAsync(long assetId, CancellationToken ct)
    {
        if (CollectibleIds.TryGetValue(assetId, out var known)) return known;
        using var doc = await RobloxHttp.GetJsonAsync($"https://economy.roblox.com/v2/assets/{assetId}/details", null, ct);
        var cid = doc.RootElement.Text("CollectibleItemId");
        if (cid.Length > 0) CollectibleIds[assetId] = cid;
        return cid;
    }
    private static readonly ConcurrentDictionary<long, string> CollectibleIds = new();

    /// <summary>Best price (0 = nobody sells), copies that exist, and favorites, from Roblox's batch item details.</summary>
    public readonly record struct CatalogStats(long BestPrice, long Copies, long Favorites);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, (DateTime At, CatalogStats Stats)> Stats = new();

    public static CatalogStats? GetStats(long id) => Stats.TryGetValue(id, out var s) ? s.Stats : null;

    /// <summary>
    /// Fills <see cref="GetStats"/> for these limiteds, 100 per request, skipping ones fetched in the last 10 minutes.
    /// <paramref name="done"/> is told after each request (how many are known so far).
    /// </summary>
    public static async Task LoadStatsAsync(IReadOnlyList<long> ids, CancellationToken ct, Action<int>? done = null)
    {
        var need = ids.Where(id => !Stats.TryGetValue(id, out var s) || DateTime.UtcNow - s.At > TimeSpan.FromMinutes(10)).Distinct().ToList();
        int known = ids.Count - need.Count;
        foreach (var chunk in need.Chunk(100))
        {
            ct.ThrowIfCancellationRequested();
            var body = new { items = chunk.Select(id => new { itemType = "Asset", id }).ToArray() };
            using var doc = await RobloxHttp.PostJsonAsync("https://catalog.roblox.com/v1/catalog/items/details", null, body, ct);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var x in data.EnumerateArray())
                {
                    long N(string name) => x.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
                    long id = N("id");
                    if (id > 0) Stats[id] = (DateTime.UtcNow, new CatalogStats(N("lowestResalePrice"), N("totalQuantity"), N("favoriteCount")));
                }
            known += chunk.Length;
            done?.Invoke(known);
            await Task.Delay(250, ct);   // gentle on Roblox
        }
    }

    // ------------------------------------------------------------------ deal feed

    /// <summary>
    /// The latest market moves (about the last two minutes): new lowest prices, and RAP updates which are applied to the
    /// values right away. Only new lowest prices come back.
    /// </summary>
    private static Task<List<(long AssetId, long Price, DateTime At)>>? _feedTask;
    private static DateTime _feedAt;

    public static Task<List<(long AssetId, long Price, DateTime At)>> GetNewPricesAsync(CancellationToken ct)
    {
        // asked again within the same second (Deals, notifications and the watchlist all poll it): share one request
        if (_feedTask != null && DateTime.UtcNow - _feedAt < TimeSpan.FromMilliseconds(900)) return _feedTask;
        _feedAt = DateTime.UtcNow;
        return _feedTask = FetchNewPricesAsync(ct);
    }

    private static async Task<List<(long AssetId, long Price, DateTime At)>> FetchNewPricesAsync(CancellationToken ct)
    {
        var text = await RobloxHttp.SendAsync(HttpMethod.Get, "https://api.rolimons.com/market/v1/dealactivity", null, null, ct);
        using var doc = JsonDocument.Parse(text);
        var list = new List<(long, long, DateTime)>();
        if (!doc.RootElement.TryGetProperty("activities", out var acts) || acts.ValueKind != JsonValueKind.Array) return list;
        foreach (var a in acts.EnumerateArray())
        {
            if (a.ValueKind != JsonValueKind.Array || a.GetArrayLength() < 4) continue;
            static long N(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var v) ? v
                                          : e.ValueKind == JsonValueKind.String && long.TryParse(e.GetString(), out var s) ? s : -1;
            long at = N(a[0]), kind = N(a[1]), id = N(a[2]), price = N(a[3]);
            if (at <= 0 || id <= 0) continue;
            if (kind == 1) SetRap(id, price);
            else if (kind == 0 && price > 0) list.Add((id, price, DateTimeOffset.FromUnixTimeSeconds(at).LocalDateTime));
        }
        return list;
    }
}
