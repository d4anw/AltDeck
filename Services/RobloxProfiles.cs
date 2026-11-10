using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using RoDeck.Models;

namespace RoDeck.Services;

/// <summary>An emote in one of the 8 slots of the emote wheel (position 1–8), or an empty slot.</summary>
public sealed partial class EmoteSlot : ObservableObject
{
    public int Position { get; init; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsEmpty))] private long assetId;
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string thumbUrl = "";
    [ObservableProperty] private bool isSelected;
    public bool IsEmpty => AssetId == 0;
}

/// <summary>
/// The fuller player profile (what they wear, their friends, communities, badges, favourite games, inventory)
/// and the account's emote wheel.
/// </summary>
public static class RobloxProfiles
{
    /// <summary>What the player is wearing right now, with names (Roblox's avatar service).</summary>
    public static async Task<List<WardrobeItem>> GetWearingAsync(long userId, CancellationToken ct)
    {
        using var doc = await RobloxHttp.GetJsonAsync($"https://avatar.roblox.com/v2/avatar/users/{userId}/avatar", null, ct);
        var list = new List<WardrobeItem>();
        foreach (var a in doc.RootElement.Arr("assets"))
        {
            var type = a.TryGetProperty("assetType", out var t) ? t : default;
            list.Add(new WardrobeItem { AssetId = a.Num("id"), Name = a.Text("name"), TypeId = (int)type.Num("id") });
        }
        foreach (var e in doc.RootElement.Arr("emotes"))
            if (e.Num("assetId") is > 0 and var id && list.All(x => x.AssetId != id))
                list.Add(new WardrobeItem { AssetId = id, Name = e.Text("assetName"), TypeId = 61 });
        return list.Where(i => i.AssetId > 0).ToList();
    }

    /// <summary>The player's favourite games.</summary>
    public static async Task<List<GameTile>> GetFavouriteGamesAsync(long userId, CancellationToken ct)
    {
        using var doc = await RobloxHttp.GetJsonAsync($"https://games.roblox.com/v2/users/{userId}/favorite/games?accessFilter=2&limit=50", null, ct);
        return doc.RootElement.Arr("data").Select(g => new GameTile
        {
            UniverseId = g.Num("id"),
            PlaceId = g.TryGetProperty("rootPlace", out var rp) ? rp.Num("id") : 0,
            Name = g.Text("name"),
        }).Where(t => t.UniverseId > 0 && t.PlaceId > 0).ToList();
    }

    /// <summary>A page of the player's badges, newest first (hidden when their inventory is private).</summary>
    public static async Task<(List<BadgeInfo> Badges, string? Next)> GetBadgesAsync(string? cookie, long userId, string? cursor, CancellationToken ct)
    {
        using var doc = await RobloxHttp.GetJsonAsync($"https://badges.roblox.com/v1/users/{userId}/badges?limit=100&sortOrder=Desc"
                                                      + (cursor != null ? "&cursor=" + Uri.EscapeDataString(cursor) : ""), cookie, ct);
        var list = new List<BadgeInfo>();
        foreach (var b in doc.RootElement.Arr("data"))
        {
            var st = b.TryGetProperty("statistics", out var s) ? s : default;
            double rate = st.ValueKind == JsonValueKind.Object && st.TryGetProperty("winRatePercentage", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetDouble() : 0;
            list.Add(new BadgeInfo
            {
                Id = b.Num("id"),
                Name = b.Text("displayName") is { Length: > 0 } dn ? dn : b.Text("name"),
                Description = b.Text("displayDescription") is { Length: > 0 } dd ? dd : b.Text("description"),
                Awarded = st.Num("awardedCount"), AwardedYesterday = st.Num("pastDayAwardedCount"), WinRate = rate * (rate <= 1 ? 100 : 1),
            });
        }
        var next = doc.RootElement.Text("nextPageCursor");
        return (list, next.Length > 0 ? next : null);
    }

    /// <summary>Whether anyone may look at this player's inventory.</summary>
    public static async Task<bool> CanViewInventoryAsync(long userId, CancellationToken ct)
    {
        using var doc = await RobloxHttp.GetJsonAsync($"https://inventory.roblox.com/v1/users/{userId}/can-view-inventory", null, ct);
        return RobloxCommunities.Bool(doc.RootElement, "canView");
    }

    // ------------------------------------------------------------------ the emote wheel

    /// <summary>The account's 8 emote slots (empty ones too).</summary>
    public static async Task<List<EmoteSlot>> GetEmoteSlotsAsync(string cookie, CancellationToken ct)
    {
        using var doc = await RobloxHttp.GetJsonAsync("https://avatar.roblox.com/v1/emotes", cookie, ct);
        var slots = Enumerable.Range(1, 8).Select(p => new EmoteSlot { Position = p }).ToList();
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray() : doc.RootElement.Arr("data");
        foreach (var e in items)
        {
            int pos = (int)e.Num("position");
            if (pos is < 1 or > 8) continue;
            slots[pos - 1].AssetId = e.Num("assetId");
            slots[pos - 1].Name = e.Text("assetName");
        }
        return slots;
    }

    public static async Task SetEmoteAsync(string cookie, long assetId, int position, CancellationToken ct)
    {
        using var _ = await RobloxHttp.PostJsonAsync($"https://avatar.roblox.com/v1/emotes/{assetId}/{position}", cookie, NoBody.Instance, ct);
    }

    public static async Task RemoveEmoteAsync(string cookie, int position, CancellationToken ct)
    {
        using var _ = await RobloxHttp.DeleteJsonAsync($"https://avatar.roblox.com/v1/emotes/{position}", cookie, ct);
    }
}
