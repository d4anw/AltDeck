using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RoDeck.Services;

static class Fmt
{
    /// <summary>13540088 → "13.5M", 4500 → "4.5K".</summary>
    public static string Short(long n) => n switch
    {
        >= 1_000_000_000 => (n / 1_000_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "B",
        >= 1_000_000 => (n / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 10_000 => (n / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "K",
        _ => n.ToString("N0", CultureInfo.CurrentCulture)
    };
}

/// <summary>A community (group) in a list: yours, or a search result.</summary>
public sealed partial class CommunityTile : ObservableObject
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public long Members { get; init; }
    public bool Verified { get; init; }
    /// <summary>Your role in it ("" when you're not a member).</summary>
    public string Role { get; init; } = "";
    public int Rank { get; init; }
    [ObservableProperty] private string iconUrl = "";
    public string MembersText => Fmt.Short(Members) + (Members == 1 ? " member" : " members");
    public string SubText => Role.Length > 0 ? Role : MembersText;
}

/// <summary>A community's page.</summary>
public sealed class CommunityDetails
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public long OwnerId { get; init; }
    public string OwnerName { get; init; } = "";
    public string Shout { get; init; } = "";
    public string ShoutBy { get; init; } = "";
    public DateTime? ShoutAt { get; init; }
    public long Members { get; init; }
    public bool Verified { get; init; }
    public bool PublicEntry { get; init; }
    public string MembersText => Fmt.Short(Members) + (Members == 1 ? " member" : " members");
    public bool HasShout => Shout.Length > 0;
    public string ShoutMeta => ShoutBy.Length == 0 ? "" : ShoutBy + (ShoutAt is DateTime d ? " · " + d.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture) : "");
}

public sealed partial class CommunityMember : ObservableObject
{
    public long UserId { get; init; }
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Role { get; init; } = "";
    [ObservableProperty] private string headshotUrl = "";
    public string Initials => (DisplayName.Length > 0 ? DisplayName : Name) is { Length: > 0 } n ? n[..Math.Min(2, n.Length)].ToUpperInvariant() : "?";
    public string Label => DisplayName.Length > 0 ? DisplayName : Name;
}

public sealed record CommunityRole(long Id, string Name, int Rank, long Members)
{
    public override string ToString() => Members > 0 ? $"{Name} ({Fmt.Short(Members)})" : Name;
}

/// <summary>Communities (Roblox's new name for groups): yours, search, a community's page, members, games, joining and leaving.</summary>
public static class RobloxCommunities
{
    const string Groups = "https://groups.roblox.com";

    public static async Task<List<CommunityTile>> GetMineAsync(long userId, CancellationToken ct)
    {
        using var doc = await RobloxHttp.GetJsonAsync($"{Groups}/v2/users/{userId}/groups/roles", null, ct);
        var list = new List<CommunityTile>();
        foreach (var x in doc.RootElement.Arr("data"))
        {
            var g = x.TryGetProperty("group", out var gg) ? gg : default;
            var r = x.TryGetProperty("role", out var rr) ? rr : default;
            list.Add(new CommunityTile
            {
                Id = g.Num("id"), Name = g.Text("name"), Members = g.Num("memberCount"), Verified = Bool(g, "hasVerifiedBadge"),
                Role = r.Text("name"), Rank = (int)r.Num("rank")
            });
        }
        return list.Where(c => c.Id > 0).ToList();
    }

    public static async Task<List<CommunityTile>> SearchAsync(string keyword, CancellationToken ct)
    {
        using var doc = await RobloxHttp.GetJsonAsync($"{Groups}/v1/groups/search?keyword={Uri.EscapeDataString(keyword)}&limit=50&prioritizeExactMatch=true", null, ct);
        return doc.RootElement.Arr("data").Select(g => new CommunityTile
        {
            Id = g.Num("id"), Name = g.Text("name"), Members = g.Num("memberCount"), Verified = Bool(g, "hasVerifiedBadge")
        }).Where(c => c.Id > 0).ToList();
    }

    public static async Task<CommunityDetails> GetAsync(long id, CancellationToken ct)
    {
        using var doc = await RobloxHttp.GetJsonAsync($"{Groups}/v1/groups/{id}", null, ct);
        var g = doc.RootElement;
        var owner = g.TryGetProperty("owner", out var o) ? o : default;
        var shout = g.TryGetProperty("shout", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
        var poster = shout.ValueKind == JsonValueKind.Object && shout.TryGetProperty("poster", out var p) ? p : default;
        DateTime? at = DateTime.TryParse(shout.Text("updated"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
        return new CommunityDetails
        {
            Id = g.Num("id"), Name = g.Text("name"), Description = g.Text("description"),
            OwnerId = owner.Num("userId"), OwnerName = owner.Text("displayName") is { Length: > 0 } dn ? dn : owner.Text("username"),
            Shout = shout.Text("body"), ShoutBy = poster.Text("displayName") is { Length: > 0 } pd ? pd : poster.Text("username"), ShoutAt = at,
            Members = g.Num("memberCount"), Verified = Bool(g, "hasVerifiedBadge"), PublicEntry = Bool(g, "publicEntryAllowed")
        };
    }

    public static async Task<List<CommunityRole>> GetRolesAsync(long id, CancellationToken ct)
    {
        using var doc = await RobloxHttp.GetJsonAsync($"{Groups}/v1/groups/{id}/roles", null, ct);
        return doc.RootElement.Arr("roles")
            .Select(r => new CommunityRole(r.Num("id"), r.Text("name"), (int)r.Num("rank"), r.Num("memberCount")))
            .Where(r => r.Rank > 0).OrderByDescending(r => r.Rank).ToList();
    }

    /// <summary>A page of members: everyone (newest first), or one role.</summary>
    public static async Task<(List<CommunityMember> Members, string? Next)> GetMembersAsync(long id, long? roleId, string? cursor, CancellationToken ct)
    {
        var url = (roleId is long r ? $"{Groups}/v1/groups/{id}/roles/{r}/users?limit=100&sortOrder=Desc" : $"{Groups}/v1/groups/{id}/users?limit=100&sortOrder=Desc")
                  + (string.IsNullOrEmpty(cursor) ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
        using var doc = await RobloxHttp.GetJsonAsync(url, null, ct);
        var list = new List<CommunityMember>();
        foreach (var x in doc.RootElement.Arr("data"))
        {
            // the all-members list nests the user; the role list is the user itself
            var u = x.TryGetProperty("user", out var uu) ? uu : x;
            var role = x.TryGetProperty("role", out var rr) ? rr.Text("name") : "";
            list.Add(new CommunityMember { UserId = u.Num("userId"), Name = u.Text("username"), DisplayName = u.Text("displayName"), Role = role });
        }
        var next = doc.RootElement.Text("nextPageCursor");
        return (list.Where(m => m.UserId > 0).ToList(), next.Length > 0 ? next : null);
    }

    /// <summary>The community's public games.</summary>
    public static async Task<List<RoDeck.Models.GameTile>> GetGamesAsync(long id, CancellationToken ct)
    {
        using var doc = await RobloxHttp.GetJsonAsync($"https://games.roblox.com/v2/groups/{id}/gamesV2?accessFilter=Public&limit=50&sortOrder=Desc", null, ct);
        return doc.RootElement.Arr("data").Select(g => new RoDeck.Models.GameTile
        {
            UniverseId = g.Num("id"),
            PlaceId = g.TryGetProperty("rootPlace", out var rp) ? rp.Num("id") : 0,
            Name = g.Text("name"),
        }).Where(t => t.UniverseId > 0 && t.PlaceId > 0).ToList();
    }

    /// <summary>
    /// Joins (or asks to join, for communities that approve members; check <see cref="GetMineAsync"/> afterwards).
    /// Roblox sometimes wants a security check first: then <see cref="RobloxException.Challenge"/> is set.
    /// </summary>
    public static async Task JoinAsync(string cookie, long id, CancellationToken ct)
    {
        using var _ = await RobloxHttp.PostJsonAsync($"{Groups}/v1/groups/{id}/users", cookie, new { }, ct);
    }

    public static async Task LeaveAsync(string cookie, long id, long userId, CancellationToken ct)
    {
        using var _ = await RobloxHttp.DeleteJsonAsync($"{Groups}/v1/groups/{id}/users/{userId}", cookie, ct);
    }

    public static async Task<Dictionary<long, string>> GetIconsAsync(IEnumerable<long> ids, CancellationToken ct)
    {
        var result = new Dictionary<long, string>();
        foreach (var chunk in ids.Distinct().Where(i => i > 0).Chunk(100))
        {
            using var doc = await RobloxHttp.GetJsonAsync(
                $"https://thumbnails.roblox.com/v1/groups/icons?groupIds={string.Join(',', chunk)}&size=150x150&format=Png&isCircular=false", null, ct);
            foreach (var t in doc.RootElement.Arr("data"))
                if (t.Text("state") == "Completed" && t.Text("imageUrl") is { Length: > 0 } u) result[t.Num("targetId")] = u;
        }
        return result;
    }

    internal static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
