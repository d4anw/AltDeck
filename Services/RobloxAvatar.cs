using System.Text.Json;
using System.Text.Json.Nodes;
using AltDeck.Models;

namespace AltDeck.Services;

/// <summary>Roblox's avatar rules: what can be worn and how much, scale limits and the body colour palette.</summary>
public sealed class AvatarRules
{
    public Dictionary<int, (string Name, int Max)> Wearable { get; } = new();
    public Dictionary<string, (double Min, double Max, double Step)> Scales { get; } = new();
    public List<PaletteColor> Palette { get; } = new();
}

/// <summary>Avatar editor data and actions. Reading is public; every change is made as the given account (its cookie).</summary>
public static class RobloxAvatar
{
    const string Avatar = "https://avatar.roblox.com";

    // ------------------------------------------------------------------ reading

    static AvatarRules? _rules;

    public static async Task<AvatarRules> GetRulesAsync(CancellationToken ct)
    {
        if (_rules != null) return _rules;
        using var doc = await RobloxHttp.GetJsonAsync($"{Avatar}/v1/avatar-rules", null, ct);
        var r = new AvatarRules();
        var root = doc.RootElement;
        foreach (var w in root.GetProperty("wearableAssetTypes").EnumerateArray())
            r.Wearable[(int)Long(w, "id")] = (Str(w, "name"), (int)Long(w, "maxNumber"));
        foreach (var s in root.GetProperty("scales").EnumerateObject())
            r.Scales[s.Name] = (Dbl(s.Value, "min"), Dbl(s.Value, "max"), Dbl(s.Value, "increment"));
        if (root.TryGetProperty("bodyColorsPalette", out var pal))
            foreach (var c in pal.EnumerateArray())
                r.Palette.Add(new PaletteColor(Str(c, "hexColor").TrimStart('#').ToUpperInvariant(), Str(c, "name")));
        return _rules = r;
    }

    public static async Task<AvatarState> GetAvatarAsync(long userId, CancellationToken ct)
    {
        using var doc = await RobloxHttp.GetJsonAsync($"{Avatar}/v2/avatar/users/{userId}/avatar", null, ct);
        return ReadLook(doc.RootElement, "scales");
    }

    /// <summary>The look stored in an outfit (same shape as an avatar).</summary>
    public static async Task<AvatarState> GetOutfitAsync(long outfitId, CancellationToken ct)
    {
        using var doc = await RobloxHttp.GetJsonAsync($"{Avatar}/v3/outfits/{outfitId}/details", null, ct);
        return ReadLook(doc.RootElement, "scale");
    }

    static AvatarState ReadLook(JsonElement root, string scalesName)
    {
        var a = new AvatarState { Type = Str(root, "playerAvatarType") is { Length: > 0 } t ? t : "R15" };
        if (root.TryGetProperty(scalesName, out var sc) && sc.ValueKind == JsonValueKind.Object)
            foreach (var s in sc.EnumerateObject())
                if (s.Value.ValueKind == JsonValueKind.Number) a.Scales[s.Name] = s.Value.GetDouble();
        if (root.TryGetProperty("bodyColor3s", out var bc) && bc.ValueKind == JsonValueKind.Object)
            foreach (var c in bc.EnumerateObject())
                if (c.Value.ValueKind == JsonValueKind.String) a.Colors[c.Name] = c.Value.GetString()!.TrimStart('#').ToUpperInvariant();
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var x in assets.EnumerateArray())
            {
                var type = x.TryGetProperty("assetType", out var at) ? at : default;
                a.Assets.Add(new WornAsset
                {
                    Id = Long(x, "id"),
                    Name = Str(x, "name"),
                    TypeId = (int)Long(type, "id"),
                    TypeName = Str(type, "name"),
                    Meta = x.TryGetProperty("meta", out var m) && m.ValueKind == JsonValueKind.Object ? JsonNode.Parse(m.GetRawText()) : null
                });
            }
        return a;
    }

    /// <summary>One page of the account's items of these types (newest first). Own inventory: works even when it's private.</summary>
    public static async Task<(List<WardrobeItem> Items, string? Next)> GetInventoryAsync(string cookie, long userId, int typeId,
                                                                                       string? cursor, CancellationToken ct)
    {
        var url = $"https://inventory.roblox.com/v2/users/{userId}/inventory/{typeId}?limit=100&sortOrder=Desc"
                + (string.IsNullOrEmpty(cursor) ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
        using var doc = await RobloxHttp.GetJsonAsync(url, cookie, ct);
        var items = new List<WardrobeItem>();
        foreach (var x in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            long id = Long(x, "assetId");
            if (id > 0 && items.All(i => i.AssetId != id))   // limiteds can be owned more than once
                items.Add(new WardrobeItem { AssetId = id, Name = Str(x, "assetName"), TypeId = typeId });
        }
        var next = Str(doc.RootElement, "nextPageCursor");
        return (items, string.IsNullOrEmpty(next) ? null : next);
    }

    public static async Task<List<OutfitInfo>> GetOutfitsAsync(long userId, CancellationToken ct)
    {
        var list = new List<OutfitInfo>();
        string? token = null;
        for (int page = 0; page < 4; page++)
        {
            var url = $"{Avatar}/v2/avatar/users/{userId}/outfits?outfitType=Avatar&page=1&itemsPerPage=50&isEditable=true"
                    + (token != null ? "&paginationToken=" + Uri.EscapeDataString(token) : "");
            using var doc = await RobloxHttp.GetJsonAsync(url, null, ct);
            foreach (var o in doc.RootElement.GetProperty("data").EnumerateArray())
                list.Add(new OutfitInfo { Id = Long(o, "id"), Name = Str(o, "name") });
            token = Str(doc.RootElement, "paginationToken");
            if (string.IsNullOrEmpty(token)) break;
        }
        var pics = await Thumbnails.GetAsync("Outfit", list.Select(o => o.Id), "150x150", ct);
        foreach (var o in list) if (pics.TryGetValue(o.Id, out var url)) o.ThumbUrl = url;
        return list;
    }

    /// <summary>The avatar's full-body picture. After a change Roblox needs a moment to draw it, so this waits for the new one.</summary>
    public static async Task<string> GetPictureAsync(long userId, bool fresh, CancellationToken ct)
    {
        if (fresh) Thumbnails.Forget("Avatar", userId);
        for (int tries = 0; tries < 12; tries++)
        {
            var pics = await Thumbnails.GetAsync("Avatar", new[] { userId }, "420x420", ct);
            if (pics.TryGetValue(userId, out var url)) return url;
            await Task.Delay(800, ct);   // still being drawn ("Pending")
        }
        return "";
    }

    // ------------------------------------------------------------------ changes (as the account)

    /// <summary>Puts exactly these items on. Returns the IDs Roblox refused (not owned, not allowed, too many).</summary>
    public static async Task<List<long>> SetWearingAsync(string cookie, IEnumerable<WornAsset> assets, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["assets"] = new JsonArray(assets.Select(a =>
            {
                var o = new JsonObject { ["id"] = a.Id };
                if (a.Meta != null) o["meta"] = a.Meta.DeepClone();
                return (JsonNode)o;
            }).ToArray())
        };
        using var doc = await RobloxHttp.PostJsonAsync($"{Avatar}/v2/avatar/set-wearing-assets", cookie, body.ToJsonString(), ct);
        var refused = new List<long>();
        if (doc.RootElement.TryGetProperty("invalidAssetIds", out var bad) && bad.ValueKind == JsonValueKind.Array)
            foreach (var b in bad.EnumerateArray()) if (b.ValueKind == JsonValueKind.Number) refused.Add(b.GetInt64());
        return refused;
    }

    public static async Task SetColorsAsync(string cookie, Dictionary<string, string> colors, CancellationToken ct = default)
    {
        var body = new JsonObject();
        foreach (var (k, v) in colors) body[k] = v.TrimStart('#').ToUpperInvariant();
        using var _ = await RobloxHttp.PostJsonAsync($"{Avatar}/v2/avatar/set-body-colors", cookie, body.ToJsonString(), ct);
    }

    public static async Task SetScalesAsync(string cookie, Dictionary<string, double> scales, CancellationToken ct = default)
    {
        var body = new JsonObject();
        foreach (var (k, v) in scales) body[k] = Math.Round(v, 2);
        using var _ = await RobloxHttp.PostJsonAsync($"{Avatar}/v1/avatar/set-scales", cookie, body.ToJsonString(), ct);
    }

    public static async Task SetTypeAsync(string cookie, string type, CancellationToken ct = default)
    {
        using var _ = await RobloxHttp.PostJsonAsync($"{Avatar}/v1/avatar/set-player-avatar-type", cookie, new { playerAvatarType = type }, ct);
    }

    /// <summary>Asks Roblox to draw the avatar picture again (it usually does by itself after a change).</summary>
    public static async Task RedrawAsync(string cookie, CancellationToken ct = default)
    {
        try { using var _ = await RobloxHttp.PostJsonAsync($"{Avatar}/v1/avatar/redraw-thumbnail", cookie, new { }, ct); }
        catch (RobloxException) { }   // Roblox limits how often this may be asked; the picture still updates on its own
    }

    /// <summary>
    /// Puts a whole look on an account: body type, scales, colours and items. Items the account doesn't own are skipped
    /// by Roblox; their IDs come back.
    /// </summary>
    public static async Task<List<long>> ApplyLookAsync(string cookie, AvatarState look, CancellationToken ct = default)
    {
        await SetTypeAsync(cookie, look.Type, ct);
        if (look.Scales.Count > 0) await SetScalesAsync(cookie, look.Scales, ct);
        if (look.Colors.Count > 0) await SetColorsAsync(cookie, look.Colors, ct);
        var refused = await SetWearingAsync(cookie, look.Assets, ct);
        await RedrawAsync(cookie, ct);
        return refused;
    }

    public static async Task<long> CreateOutfitAsync(string cookie, string name, AvatarState look, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["name"] = name,
            ["outfitType"] = "Avatar",
            ["playerAvatarType"] = look.Type,
            ["bodyColor3s"] = new JsonObject(look.Colors.Select(c => KeyValuePair.Create(c.Key, (JsonNode?)JsonValue.Create(c.Value)))),
            ["scale"] = new JsonObject(look.Scales.Select(s => KeyValuePair.Create(s.Key, (JsonNode?)JsonValue.Create(Math.Round(s.Value, 2))))),
            ["assets"] = new JsonArray(look.Assets.Select(a =>
            {
                var o = new JsonObject { ["id"] = a.Id };
                if (a.Meta != null) o["meta"] = a.Meta.DeepClone();
                return (JsonNode)o;
            }).ToArray())
        };
        using var doc = await RobloxHttp.PostJsonAsync($"{Avatar}/v3/outfits/create", cookie, body.ToJsonString(), ct);
        return Long(doc.RootElement, "id");
    }

    public static async Task RenameOutfitAsync(string cookie, long outfitId, string name, CancellationToken ct = default)
    {
        using var _ = await RobloxHttp.PatchJsonAsync($"{Avatar}/v1/outfits/{outfitId}", cookie, new { name }, ct);
    }

    public static async Task DeleteOutfitAsync(string cookie, long outfitId, CancellationToken ct = default)
    {
        using var _ = await RobloxHttp.PostJsonAsync($"{Avatar}/v1/outfits/{outfitId}/delete", cookie, new { }, ct);
    }

    // ------------------------------------------------------------------ helpers

    static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;
    static double Dbl(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
