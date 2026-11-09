using System.Text.Json;
using RoDeck.Models;

namespace RoDeck.Services;

/// <summary>Data for the Home page: online friends, and Roblox's own "Continue" and recommended game rows.</summary>
public static class RobloxHome
{
    // ------------------------------------------------------------------ friends

    /// <summary>The account's friends who are online, those in a game first. Needs the account's sign-in.</summary>
    public static async Task<List<FriendTile>> GetOnlineFriendsAsync(string cookie, long userId, CancellationToken ct)
    {
        // 1. friend IDs (Roblox no longer sends names here reliably, so names come from step 3)
        using var friendsDoc = await RobloxHttp.GetJsonAsync($"https://friends.roblox.com/v1/users/{userId}/friends", cookie, ct);
        var ids = new List<long>();
        if (friendsDoc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var f in data.EnumerateArray())
                if (f.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number) ids.Add(id.GetInt64());
        if (ids.Count == 0) return new();

        // 2. who's online and where (same presence service RoDeck already uses for your own accounts)
        var online = new System.Collections.Concurrent.ConcurrentBag<(long Id, int Type, string Loc, long Place, string? Job)>();
        await Task.WhenAll(ids.Chunk(50).Select(async chunk =>
        {
            using var doc = await RobloxHttp.PostJsonAsync("https://presence.roblox.com/v1/presence/users", cookie, new { userIds = chunk }, ct);
            foreach (var p in doc.RootElement.GetProperty("userPresences").EnumerateArray())
            {
                int type = p.TryGetProperty("userPresenceType", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;
                if (type is < 1 or > 3) continue;   // 0 = offline (4 = invisible)
                online.Add((p.GetProperty("userId").GetInt64(), type, Str(p, "lastLocation"),
                            Long(p, "rootPlaceId") is > 0 and var root ? root : Long(p, "placeId"),
                            Str(p, "gameId") is { Length: > 0 } g ? g : null));
            }
        }));
        if (online.IsEmpty) return new();

        // 3. names
        var names = new Dictionary<long, (string Name, string Display)>();
        foreach (var chunk in online.Select(o => o.Id).Chunk(100))
        {
            using var doc = await RobloxHttp.PostJsonAsync("https://users.roblox.com/v1/users", null,
                new { userIds = chunk, excludeBannedUsers = false }, ct);
            foreach (var u in doc.RootElement.GetProperty("data").EnumerateArray())
                names[u.GetProperty("id").GetInt64()] = (Str(u, "name"), Str(u, "displayName"));
        }

        var tiles = online
            .Select(o => new FriendTile
            {
                UserId = o.Id,
                Name = names.TryGetValue(o.Id, out var n) ? n.Name : "",
                DisplayName = names.TryGetValue(o.Id, out var n2) ? n2.Display : "",
                PresenceType = o.Type,
                Location = o.Loc == "Website" ? "" : o.Loc,
                PlaceId = o.Place,
                JobId = o.Job
            })
            .OrderBy(f => f.PresenceType == 2 ? 0 : f.PresenceType == 3 ? 1 : 2)
            .ThenBy(f => f.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 4. pictures (in the background of the caller; the tiles show initials until then)
        var heads = await Thumbnails.GetAsync("AvatarHeadShot", tiles.Select(t => t.UserId), "150x150", ct);
        foreach (var t in tiles) if (heads.TryGetValue(t.UserId, out var url)) t.HeadshotUrl = url;
        return tiles;
    }

    // ------------------------------------------------------------------ games

    /// <summary>Roblox's home rows for this account: the "Continue" row and a recommended row.</summary>
    public static async Task<(List<GameTile> Continue, List<GameTile> Recommended)> GetHomeGamesAsync(string cookie, CancellationToken ct)
    {
        using var doc = await RobloxHttp.PostJsonAsync("https://apis.roblox.com/discovery-api/omni-recommendation", cookie,
            new { pageType = "Home", sessionId = Guid.NewGuid().ToString() }, ct);
        var root = doc.RootElement;

        // metadata: contentMetadata.Game["<universeId>"] = { name, rootPlaceId, playerCount, totalUpVotes, totalDownVotes }
        JsonElement meta = default;
        bool hasMeta = root.TryGetProperty("contentMetadata", out var cm) && cm.TryGetProperty("Game", out meta);

        var rows = new List<(string Topic, List<long> Ids)>();
        if (root.TryGetProperty("sorts", out var sorts) && sorts.ValueKind == JsonValueKind.Array)
            foreach (var s in sorts.EnumerateArray())
            {
                if (!s.TryGetProperty("recommendationList", out var list) || list.ValueKind != JsonValueKind.Array) continue;
                var ids = list.EnumerateArray()
                    .Where(r => Str(r, "contentType") == "Game" && r.TryGetProperty("contentId", out var c) && c.ValueKind == JsonValueKind.Number)
                    .Select(r => r.GetProperty("contentId").GetInt64())
                    .ToList();
                if (ids.Count > 0) rows.Add((Str(s, "topic"), ids));
            }

        var cont = rows.FirstOrDefault(r => r.Topic.Contains("Continue", StringComparison.OrdinalIgnoreCase));
        var rec = rows.FirstOrDefault(r => r.Topic.Contains("Recommended", StringComparison.OrdinalIgnoreCase));
        if (rec.Ids == null) rec = rows.FirstOrDefault(r => r.Ids != cont.Ids);   // else: the first other row

        List<GameTile> Build(List<long>? ids) => (ids ?? new()).Take(18).Select(id =>
        {
            JsonElement g = default;
            bool known = hasMeta && meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty(id.ToString(), out g);
            return new GameTile
            {
                UniverseId = id,
                PlaceId = known ? Long(g, "rootPlaceId") : 0,
                Name = known ? Str(g, "name") : "",
                Playing = known ? Long(g, "playerCount") : 0,
                UpVotes = known ? Long(g, "totalUpVotes") : 0,
                DownVotes = known ? Long(g, "totalDownVotes") : 0
            };
        }).ToList();

        var c = Build(cont.Ids);
        var r = Build(rec.Ids);
        await FillMissingAsync(c.Concat(r).ToList(), ct);
        return (c.Where(t => t.PlaceId > 0).ToList(), r.Where(t => t.PlaceId > 0).ToList());
    }

    /// <summary>The games an account favourited on Roblox, newest first (public, up to 50).</summary>
    public static async Task<List<GameTile>> GetFavoriteGamesAsync(long userId, CancellationToken ct)
    {
        using var doc = await RobloxHttp.GetJsonAsync($"https://games.roblox.com/v2/users/{userId}/favorite/games?limit=50&sortOrder=Desc", null, ct);
        var list = new List<GameTile>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var g in data.EnumerateArray())
                if (Long(g, "id") is > 0 and var id) list.Add(new GameTile { UniverseId = id });
        return list;
    }

    /// <summary>Fills name / place / players / likes from the public games service where Roblox didn't include them, then icons.</summary>
    public static async Task FillMissingAsync(List<GameTile> tiles, CancellationToken ct)
    {
        var missing = tiles.Where(t => t.PlaceId == 0 || string.IsNullOrEmpty(t.Name)).Select(t => t.UniverseId).Distinct().ToList();
        if (missing.Count > 0)
        {
            var info = new Dictionary<long, (string Name, long Place, long Playing)>();
            foreach (var chunk in missing.Chunk(50))
            {
                using var doc = await RobloxHttp.GetJsonAsync("https://games.roblox.com/v1/games?universeIds=" + string.Join(",", chunk), null, ct);
                foreach (var g in doc.RootElement.GetProperty("data").EnumerateArray())
                    info[g.GetProperty("id").GetInt64()] = (Str(g, "name"), Long(g, "rootPlaceId"), Long(g, "playing"));
            }
            var votes = new Dictionary<long, (long Up, long Down)>();
            foreach (var chunk in missing.Chunk(50))
            {
                using var doc = await RobloxHttp.GetJsonAsync("https://games.roblox.com/v1/games/votes?universeIds=" + string.Join(",", chunk), null, ct);
                foreach (var v in doc.RootElement.GetProperty("data").EnumerateArray())
                    votes[v.GetProperty("id").GetInt64()] = (Long(v, "upVotes"), Long(v, "downVotes"));
            }
            foreach (var t in tiles)
            {
                if (!info.TryGetValue(t.UniverseId, out var g)) continue;
                votes.TryGetValue(t.UniverseId, out var v);
                t.PlaceId = g.Place; t.Name = g.Name; t.Playing = g.Playing;
                t.UpVotes = v.Up; t.DownVotes = v.Down;
            }
        }

        var icons = await Thumbnails.GetAsync("GameIcon", tiles.Select(t => t.UniverseId), "256x256", ct);
        foreach (var t in tiles) if (icons.TryGetValue(t.UniverseId, out var url)) t.IconUrl = url;
    }

    static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;
}
