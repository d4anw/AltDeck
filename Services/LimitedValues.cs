using System.Collections.Concurrent;
using System.Text.Json;
using AltDeck.Models;

namespace AltDeck.Services;

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
    static string CacheFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AltDeck", "cache", "values.json");

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
            long L(int i) => i < a.Count && a[i].ValueKind == JsonValueKind.Number ? a[i].GetInt64() : -1;
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

    // ------------------------------------------------------------------ deal feed

    /// <summary>
    /// The latest market moves (about the last two minutes): new lowest prices, and RAP updates which are applied to the
    /// values right away. Only new lowest prices come back.
    /// </summary>
    public static async Task<List<(long AssetId, long Price, DateTime At)>> GetNewPricesAsync(CancellationToken ct)
    {
        var text = await RobloxHttp.SendAsync(HttpMethod.Get, "https://api.rolimons.com/market/v1/dealactivity", null, null, ct);
        using var doc = JsonDocument.Parse(text);
        var list = new List<(long, long, DateTime)>();
        if (!doc.RootElement.TryGetProperty("activities", out var acts) || acts.ValueKind != JsonValueKind.Array) return list;
        foreach (var a in acts.EnumerateArray())
        {
            if (a.ValueKind != JsonValueKind.Array || a.GetArrayLength() < 4) continue;
            long at = a[0].GetInt64(), kind = a[1].GetInt64(), id = a[2].GetInt64(), price = a[3].GetInt64();
            if (kind == 1) SetRap(id, price);
            else if (kind == 0 && price > 0) list.Add((id, price, DateTimeOffset.FromUnixTimeSeconds(at).LocalDateTime));
        }
        return list;
    }
}
