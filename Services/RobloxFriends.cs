using System.Text.Json;
using AltDeck.Models;

namespace AltDeck.Services;

/// <summary>An incoming friend request.</summary>
public sealed class FriendRequest
{
    public long UserId { get; init; }
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public DateTime? SentAt { get; init; }
    public int MutualFriends { get; init; }
    public string HeadshotUrl { get; set; } = "";
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
    public string Initials => string.IsNullOrWhiteSpace(Label) ? "?" : Label.Trim()[..Math.Min(2, Label.Trim().Length)].ToUpperInvariant();
    public string Info => string.Join("  ·  ", new[]
    {
        Name.Length > 0 ? "@" + Name : "",
        MutualFriends > 0 ? $"{MutualFriends} mutual friend{(MutualFriends == 1 ? "" : "s")}" : "",
        SentAt is DateTime d ? Ago(d) : ""
    }.Where(x => x.Length > 0));

    static string Ago(DateTime d)
    {
        var t = DateTime.Now - d;
        return t.TotalMinutes < 60 ? $"{Math.Max(1, (int)t.TotalMinutes)} min ago"
             : t.TotalHours < 24 ? $"{(int)t.TotalHours} h ago"
             : t.TotalDays < 30 ? $"{(int)t.TotalDays} days ago"
             : d.ToString("d MMM yyyy");
    }
}

/// <summary>A player found by search.</summary>
public sealed class PlayerHit
{
    public long UserId { get; init; }
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public bool Verified { get; init; }
    public string HeadshotUrl { get; set; } = "";
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
    public string Initials => string.IsNullOrWhiteSpace(Label) ? "?" : Label.Trim()[..Math.Min(2, Label.Trim().Length)].ToUpperInvariant();
}

/// <summary>Someone's profile, as seen by the current account.</summary>
public sealed class PlayerProfile
{
    public long UserId { get; init; }
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string About { get; init; } = "";
    public bool Verified { get; init; }
    public bool Banned { get; init; }
    public DateTime? Joined { get; init; }
    public long Friends { get; set; }
    public long Followers { get; set; }
    public long Following { get; set; }
    public string AvatarUrl { get; set; } = "";
    /// <summary>Friends, RequestSent, RequestReceived or NotFriends (relative to the current account). "" if unknown.</summary>
    public string Friendship { get; set; } = "";
    public int PresenceType { get; set; }
    public string Location { get; set; } = "";
    public long PlaceId { get; set; }
    public string? JobId { get; set; }
    public List<GameTile> Games { get; set; } = new();
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
}

/// <summary>Friends page data and the friend actions. Everything runs as the account top right.</summary>
public static class RobloxFriends
{
    // ------------------------------------------------------------------ the friend list

    /// <summary>The account's friends with their names, status not filled in yet (<see cref="FillPresenceAsync"/>).
    /// Fast: one request for the list, names mostly from memory.</summary>
    public static async Task<List<FriendTile>> GetFriendListAsync(string cookie, long userId, CancellationToken ct)
    {
        using var friendsDoc = await RobloxHttp.GetJsonAsync($"https://friends.roblox.com/v1/users/{userId}/friends", cookie, ct);
        var ids = new List<long>();
        if (friendsDoc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var f in data.EnumerateArray())
                if (Long(f, "id") is > 0 and var id) ids.Add(id);
        if (ids.Count == 0) return new();

        var names = await GetNamesAsync(ids, ct);
        return ids.Select(id =>
        {
            names.TryGetValue(id, out var n);
            return new FriendTile { UserId = id, Name = n.Name ?? "", DisplayName = n.Display ?? "" };
        })
        .OrderBy(f => f.Label, StringComparer.OrdinalIgnoreCase)
        .ToList();
    }

    /// <summary>Fills in who's online and where (Roblox's presence service: 60 requests a minute, 50 friends per request).</summary>
    public static async Task FillPresenceAsync(string cookie, IReadOnlyList<FriendTile> tiles, CancellationToken ct)
    {
        var byId = tiles.GroupBy(t => t.UserId).ToDictionary(g => g.Key, g => g.ToList());
        await Task.WhenAll(byId.Keys.Chunk(50).Select(async chunk =>
        {
            using var doc = await RobloxHttp.PostJsonAsync("https://presence.roblox.com/v1/presence/users", cookie, new { userIds = chunk }, ct);
            var results = new List<(long Id, int Type, string Loc, long Place, string? Job)>();
            foreach (var p in doc.RootElement.GetProperty("userPresences").EnumerateArray())
            {
                int type = p.TryGetProperty("userPresenceType", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;
                if (type > 3) type = 0;   // 4 = invisible: shows as offline
                var loc = Str(p, "lastLocation");
                results.Add((Long(p, "userId"), type, loc == "Website" ? "" : loc,
                             Long(p, "rootPlaceId") is > 0 and var root ? root : Long(p, "placeId"),
                             Str(p, "gameId") is { Length: > 0 } g ? g : null));
            }
            // tiles are on screen: change them on the UI thread
            await OnUi(() =>
            {
                foreach (var r in results)
                    if (byId.TryGetValue(r.Id, out var list))
                        foreach (var tile in list)
                        {
                            tile.Location = r.Loc; tile.PlaceId = r.Place; tile.JobId = r.Job;
                            tile.PresenceType = r.Type;
                        }
            });
        }));
        // anyone Roblox didn't mention counts as offline
        await OnUi(() =>
        {
            foreach (var t in tiles) if (t.PresenceType < 0) t.PresenceType = 0;
        });
    }

    // tiles may be on screen: change them on the UI thread (directly when there is no window, e.g. in tests)
    static Task OnUi(Action a)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) { a(); return Task.CompletedTask; }
        return d.InvokeAsync(a).Task;
    }

    /// <summary>Order for lists: in a game, Studio, online, offline, then by name.</summary>
    public static List<FriendTile> ByPresence(IEnumerable<FriendTile> list) =>
        list.OrderBy(f => f.PresenceType switch { 2 => 0, 3 => 1, 1 => 2, _ => 3 })
            .ThenBy(f => f.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Pictures for the friends on screen (the rest stay initials until shown).</summary>
    public static async Task FillHeadshotsAsync(IEnumerable<FriendTile> tiles, CancellationToken ct)
    {
        var need = tiles.Where(t => t.HeadshotUrl.Length == 0).ToList();
        if (need.Count == 0) return;
        var heads = await Thumbnails.GetAsync("AvatarHeadShot", need.Select(t => t.UserId), "150x150", ct);
        foreach (var t in need) if (heads.TryGetValue(t.UserId, out var url)) t.HeadshotUrl = url;
    }

    // ------------------------------------------------------------------ requests

    public static async Task<List<FriendRequest>> GetRequestsAsync(string cookie, CancellationToken ct)
    {
        using var doc = await RobloxHttp.GetJsonAsync("https://friends.roblox.com/v1/my/friends/requests?limit=100", cookie, ct);
        var list = new List<FriendRequest>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var r in data.EnumerateArray())
            {
                var fr = r.TryGetProperty("friendRequest", out var x) ? x : default;
                int mutual = r.TryGetProperty("mutualFriendsList", out var m) && m.ValueKind == JsonValueKind.Array ? m.GetArrayLength() : 0;
                list.Add(new FriendRequest
                {
                    UserId = Long(r, "id") is > 0 and var id ? id : Long(fr, "senderId"),
                    Name = Str(r, "name"), DisplayName = Str(r, "displayName"),
                    SentAt = DateTime.TryParse(Str(fr, "sentAt"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d.ToLocalTime() : null,
                    MutualFriends = mutual
                });
            }

        // names Roblox left out, and pictures
        var missing = list.Where(r => string.IsNullOrEmpty(r.Name)).Select(r => r.UserId).ToList();
        if (missing.Count > 0)
        {
            var names = await GetNamesAsync(missing, ct);
            foreach (var r in list)
                if (names.TryGetValue(r.UserId, out var n)) { r.Name = n.Name ?? ""; r.DisplayName = n.Display ?? ""; }
        }
        var heads = await Thumbnails.GetAsync("AvatarHeadShot", list.Select(r => r.UserId), "150x150", ct);
        foreach (var r in list) if (heads.TryGetValue(r.UserId, out var url)) r.HeadshotUrl = url;
        return list;
    }

    public static async Task<int> GetRequestCountAsync(string cookie, CancellationToken ct)
    {
        using var doc = await RobloxHttp.GetJsonAsync("https://friends.roblox.com/v1/user/friend-requests/count", cookie, ct);
        return (int)Long(doc.RootElement, "count");
    }

    // ------------------------------------------------------------------ actions (always as the account top right)

    public static Task AcceptAsync(string cookie, long userId, CancellationToken ct = default) =>
        ActAsync($"https://friends.roblox.com/v1/users/{userId}/accept-friend-request", cookie, null, ct);

    public static Task DeclineAsync(string cookie, long userId, CancellationToken ct = default) =>
        ActAsync($"https://friends.roblox.com/v1/users/{userId}/decline-friend-request", cookie, null, ct);

    public static Task UnfriendAsync(string cookie, long userId, CancellationToken ct = default) =>
        ActAsync($"https://friends.roblox.com/v1/users/{userId}/unfriend", cookie, null, ct);

    public static Task SendRequestAsync(string cookie, long userId, CancellationToken ct = default) =>
        ActAsync($"https://friends.roblox.com/v1/users/{userId}/request-friendship", cookie,
                 new { friendshipOriginSourceType = "UserProfile" }, ct);

    static async Task ActAsync(string url, string cookie, object? body, CancellationToken ct)
    {
        try { using var _ = await RobloxHttp.PostJsonAsync(url, cookie, body ?? new { }, ct); }
        catch (RobloxException ex) when (ex.Status == System.Net.HttpStatusCode.Forbidden)
        {
            // Roblox asks for a captcha ("challenge") on some friend actions; that can only be solved on its website
            throw new RobloxException("Roblox wants a captcha for this. Do it on roblox.com instead (Open on roblox.com).", ex.Status, ex);
        }
    }

    // ------------------------------------------------------------------ search and profiles

    /// <summary>Exact username first (works without sign-in), then Roblox's player search as the current account.</summary>
    public static async Task<List<PlayerHit>> SearchPlayersAsync(string query, string? cookie, CancellationToken ct)
    {
        query = query.Trim().TrimStart('@');
        var hits = new List<PlayerHit>();
        if (query.Length >= 3)
        {
            try
            {
                using var exact = await RobloxHttp.PostJsonAsync("https://users.roblox.com/v1/usernames/users", null,
                    new { usernames = new[] { query }, excludeBannedUsers = false }, ct);
                foreach (var u in exact.RootElement.GetProperty("data").EnumerateArray())
                    hits.Add(new PlayerHit { UserId = Long(u, "id"), Name = Str(u, "name"), DisplayName = Str(u, "displayName"), Verified = Bool(u, "hasVerifiedBadge") });
            }
            catch (RobloxException) { }

            try
            {
                using var doc = await RobloxHttp.GetJsonAsync(
                    $"https://users.roblox.com/v1/users/search?keyword={Uri.EscapeDataString(query)}&limit=25", cookie, ct);
                foreach (var u in doc.RootElement.GetProperty("data").EnumerateArray())
                {
                    long id = Long(u, "id");
                    if (hits.Any(h => h.UserId == id)) continue;
                    hits.Add(new PlayerHit { UserId = id, Name = Str(u, "name"), DisplayName = Str(u, "displayName"), Verified = Bool(u, "hasVerifiedBadge") });
                }
            }
            catch (RobloxException) when (hits.Count > 0) { }   // the exact match is enough
        }
        else if (long.TryParse(query, out var id) && id > 0)
        {
            var p = await GetProfileAsync(id, null, 0, ct);
            hits.Add(new PlayerHit { UserId = id, Name = p.Name, DisplayName = p.DisplayName, Verified = p.Verified });
        }

        var heads = await Thumbnails.GetAsync("AvatarHeadShot", hits.Select(h => h.UserId), "150x150", ct);
        foreach (var h in hits) if (heads.TryGetValue(h.UserId, out var url)) h.HeadshotUrl = url;
        return hits;
    }

    /// <summary>A profile; with <paramref name="cookie"/> also the friendship with that account and where they are.</summary>
    public static async Task<PlayerProfile> GetProfileAsync(long userId, string? cookie, long meId, CancellationToken ct)
    {
        using var doc = await RobloxHttp.GetJsonAsync($"https://users.roblox.com/v1/users/{userId}", null, ct);
        var u = doc.RootElement;
        var p = new PlayerProfile
        {
            UserId = userId,
            Name = Str(u, "name"),
            DisplayName = Str(u, "displayName"),
            About = Str(u, "description"),
            Verified = Bool(u, "hasVerifiedBadge"),
            Banned = Bool(u, "isBanned"),
            Joined = DateTime.TryParse(Str(u, "created"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d.ToLocalTime() : null
        };

        var tasks = new List<Task>
        {
            Safe(async () => { using var x = await RobloxHttp.GetJsonAsync($"https://friends.roblox.com/v1/users/{userId}/friends/count", null, ct); p.Friends = Long(x.RootElement, "count"); }),
            Safe(async () => { using var x = await RobloxHttp.GetJsonAsync($"https://friends.roblox.com/v1/users/{userId}/followers/count", null, ct); p.Followers = Long(x.RootElement, "count"); }),
            Safe(async () => { using var x = await RobloxHttp.GetJsonAsync($"https://friends.roblox.com/v1/users/{userId}/followings/count", null, ct); p.Following = Long(x.RootElement, "count"); }),
            Safe(async () =>
            {
                var pics = await Thumbnails.GetAsync("Avatar", new[] { userId }, "420x420", ct);
                if (pics.TryGetValue(userId, out var url)) p.AvatarUrl = url;
            }),
            Safe(async () =>
            {
                using var x = await RobloxHttp.GetJsonAsync($"https://games.roblox.com/v2/users/{userId}/games?accessFilter=Public&limit=10&sortOrder=Desc", null, ct);
                var games = x.RootElement.GetProperty("data").EnumerateArray()
                    .Select(g => new GameTile { UniverseId = Long(g, "id"), PlaceId = g.TryGetProperty("rootPlace", out var rp) ? Long(rp, "id") : 0, Name = Str(g, "name") })
                    .Where(g => g.UniverseId > 0).ToList();
                await RobloxHome.FillMissingAsync(games, ct);
                p.Games = games.Where(g => g.PlaceId > 0).ToList();
            })
        };
        if (cookie != null && meId > 0)
        {
            tasks.Add(Safe(async () =>
            {
                using var x = await RobloxHttp.GetJsonAsync($"https://friends.roblox.com/v1/users/{meId}/friends/statuses?userIds={userId}", cookie, ct);
                var e = x.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault();
                p.Friendship = e.ValueKind == JsonValueKind.Object ? Str(e, "status") : "";
            }));
            tasks.Add(Safe(async () =>
            {
                using var x = await RobloxHttp.PostJsonAsync("https://presence.roblox.com/v1/presence/users", cookie, new { userIds = new[] { userId } }, ct);
                var e = x.RootElement.GetProperty("userPresences")[0];
                int type = e.TryGetProperty("userPresenceType", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;
                p.PresenceType = type > 3 ? 0 : type;
                p.Location = Str(e, "lastLocation") is var loc && loc != "Website" ? loc : "";
                p.PlaceId = Long(e, "rootPlaceId") is > 0 and var root ? root : Long(e, "placeId");
                p.JobId = Str(e, "gameId") is { Length: > 0 } g ? g : null;
            }));
        }
        await Task.WhenAll(tasks);
        return p;
    }

    // ------------------------------------------------------------------ helpers

    // names barely ever change: remember them for the session, so friend lists need far fewer requests
    static readonly System.Collections.Concurrent.ConcurrentDictionary<long, (string?, string?)> NameCache = new();

    static async Task<Dictionary<long, (string? Name, string? Display)>> GetNamesAsync(IEnumerable<long> ids, CancellationToken ct)
    {
        var names = new System.Collections.Concurrent.ConcurrentDictionary<long, (string?, string?)>();
        var missing = new List<long>();
        foreach (var id in ids.Distinct())
            if (NameCache.TryGetValue(id, out var known)) names[id] = known; else missing.Add(id);
        await Task.WhenAll(missing.Chunk(100).Select(async chunk =>
        {
            using var doc = await RobloxHttp.PostJsonAsync("https://users.roblox.com/v1/users", null, new { userIds = chunk, excludeBannedUsers = false }, ct);
            foreach (var u in doc.RootElement.GetProperty("data").EnumerateArray())
                names[Long(u, "id")] = NameCache[Long(u, "id")] = (Str(u, "name"), Str(u, "displayName"));
        }));
        return new Dictionary<long, (string?, string?)>(names);
    }

    static async Task Safe(Func<Task> work) { try { await work(); } catch { } }

    static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;
    static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
