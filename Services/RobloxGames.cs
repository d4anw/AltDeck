using System.Text.Json;
using AltDeck.Models;

namespace AltDeck.Services;

/// <summary>One chart row on the Games page (Top Trending, Up-and-Coming…).</summary>
public sealed record GameSort(string Id, string Name, string Info, List<GameTile> Games);

/// <summary>Everything the game page shows about one game.</summary>
public sealed class GameDetails
{
    public long UniverseId { get; init; }
    public long PlaceId { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Creator { get; init; } = "";
    public long CreatorId { get; init; }
    public string CreatorType { get; init; } = "";
    public bool CreatorVerified { get; init; }
    public long Playing { get; init; }
    public long Visits { get; init; }
    public long Favorites { get; init; }
    public int MaxPlayers { get; init; }
    public string Genre { get; init; } = "";
    public DateTime? Updated { get; init; }
    public DateTime? Created { get; init; }
    public long UpVotes { get; set; }
    public long DownVotes { get; set; }
    public List<string> Media { get; set; } = new();
    public string IconUrl { get; set; } = "";
}

/// <summary>Public game data for the Games page: charts, search, details, pictures and the server list. No sign-in needed.</summary>
public static class RobloxGames
{
    static readonly string Session = Guid.NewGuid().ToString();

    // ------------------------------------------------------------------ charts

    /// <summary>Roblox's chart rows with their games (two pages of rows: about ten charts).</summary>
    public static async Task<List<GameSort>> GetSortsAsync(CancellationToken ct)
    {
        var sorts = new List<GameSort>();
        string? token = null;
        for (int page = 0; page < 2; page++)
        {
            var url = $"https://apis.roblox.com/explore-api/v1/get-sorts?sessionId={Session}&device=computer&country=all"
                    + (token != null ? "&sortsPageToken=" + Uri.EscapeDataString(token) : "");
            using var doc = await RobloxHttp.GetJsonAsync(url, null, ct);
            var root = doc.RootElement;
            if (root.TryGetProperty("sorts", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var s in arr.EnumerateArray())
                {
                    if (Str(s, "contentType") != "Games" || !s.TryGetProperty("games", out var games) || games.ValueKind != JsonValueKind.Array) continue;
                    var tiles = games.EnumerateArray().Where(g => !Bool(g, "isSponsored")).Select(Tile).Where(t => t.PlaceId > 0).ToList();
                    if (tiles.Count == 0) continue;
                    var info = s.TryGetProperty("topicLayoutData", out var tl) ? Str(tl, "infoText") : "";
                    sorts.Add(new GameSort(Str(s, "sortId"), Str(s, "sortDisplayName"), info, tiles));
                }
            token = Str(root, "nextSortsPageToken");
            if (string.IsNullOrEmpty(token)) break;
        }
        return sorts;
    }

    // ------------------------------------------------------------------ search

    public static async Task<List<GameTile>> SearchAsync(string query, CancellationToken ct)
    {
        var url = $"https://apis.roblox.com/search-api/omni-search?searchQuery={Uri.EscapeDataString(query)}&sessionId={Session}&pageType=all";
        using var doc = await RobloxHttp.GetJsonAsync(url, null, ct);
        var list = new List<GameTile>();
        if (doc.RootElement.TryGetProperty("searchResults", out var groups) && groups.ValueKind == JsonValueKind.Array)
            foreach (var g in groups.EnumerateArray())
            {
                if (Str(g, "contentGroupType") != "Game" || !g.TryGetProperty("contents", out var contents)) continue;
                foreach (var c in contents.EnumerateArray())
                    if (!Bool(c, "isSponsored")) list.Add(Tile(c));
            }
        return list.Where(t => t.PlaceId > 0).GroupBy(t => t.UniverseId).Select(x => x.First()).ToList();
    }

    // ------------------------------------------------------------------ one game

    public static async Task<GameDetails> GetDetailsAsync(long universeId, CancellationToken ct)
    {
        using var doc = await RobloxHttp.GetJsonAsync($"https://games.roblox.com/v1/games?universeIds={universeId}", null, ct);
        var data = doc.RootElement.GetProperty("data");
        if (data.GetArrayLength() == 0) throw new RobloxException("Roblox couldn't find that game.");
        var g = data[0];
        var creator = g.TryGetProperty("creator", out var cr) ? cr : default;
        var d = new GameDetails
        {
            UniverseId = universeId,
            PlaceId = Long(g, "rootPlaceId"),
            Name = Str(g, "name"),
            Description = Str(g, "description"),
            Creator = Str(creator, "name"),
            CreatorId = Long(creator, "id"),
            CreatorType = Str(creator, "type"),
            CreatorVerified = Bool(creator, "hasVerifiedBadge"),
            Playing = Long(g, "playing"),
            Visits = Long(g, "visits"),
            Favorites = Long(g, "favoritedCount"),
            MaxPlayers = (int)Long(g, "maxPlayers"),
            Genre = Str(g, "genre_l1") is { Length: > 0 } l1 ? l1 : Str(g, "genre"),
            Updated = Date(g, "updated"),
            Created = Date(g, "created")
        };

        // likes, pictures and icon in parallel; any of them failing just leaves that part empty
        var votes = Safe(async () =>
        {
            using var v = await RobloxHttp.GetJsonAsync($"https://games.roblox.com/v1/games/votes?universeIds={universeId}", null, ct);
            var e = v.RootElement.GetProperty("data")[0];
            d.UpVotes = Long(e, "upVotes"); d.DownVotes = Long(e, "downVotes");
        });
        var media = Safe(async () =>
        {
            using var m = await RobloxHttp.GetJsonAsync(
                $"https://thumbnails.roblox.com/v1/games/multiget/thumbnails?universeIds={universeId}&countPerUniverse=10&size=768x432&format=Png", null, ct);
            foreach (var u in m.RootElement.GetProperty("data").EnumerateArray())
                foreach (var t in u.GetProperty("thumbnails").EnumerateArray())
                    if (Str(t, "state") == "Completed" && Str(t, "imageUrl") is { Length: > 0 } url) d.Media.Add(url);
        });
        var icon = Safe(async () =>
        {
            var icons = await Thumbnails.GetAsync("GameIcon", new[] { universeId }, "256x256", ct);
            if (icons.TryGetValue(universeId, out var url)) d.IconUrl = url;
        });
        await Task.WhenAll(votes, media, icon);
        return d;
    }

    /// <summary>The universe (game) a place belongs to, for links that only have a place ID.</summary>
    public static async Task<long> GetUniverseIdAsync(long placeId, CancellationToken ct)
    {
        using var doc = await RobloxHttp.GetJsonAsync($"https://apis.roblox.com/universes/v1/places/{placeId}/universe", null, ct);
        return Long(doc.RootElement, "universeId");
    }

    // ------------------------------------------------------------------ servers

    /// <summary>Roblox refused because of its rate limit; <see cref="WaitSeconds"/> until it allows the next list.</summary>
    public sealed class ServersLimitedException : Exception
    {
        public int WaitSeconds { get; }
        public ServersLimitedException(int wait) : base("Roblox is limiting server lists.") => WaitSeconds = wait;
    }

    // Roblox allows only a few server lists per minute (3 without a sign-in). Each signed-in account has its own
    // allowance, so requests are spread over the user's accounts, and an account that hit the limit rests until reset.
    static readonly Dictionary<string, DateTime> LimitedUntil = new();
    static int _rotate;

    // Lists are kept for 2 minutes, so changing the sort or reopening a game costs nothing.
    static readonly Dictionary<(long, bool), (DateTime At, List<ServerEntry> List)> ServerCache = new();

    /// <summary>
    /// Public servers with room, up to <paramref name="maxPages"/> pages of 100. <paramref name="pageLoaded"/> gets the
    /// list after each page, so the first 100 can show right away. <paramref name="cookies"/>: the user's signed-in accounts
    /// (empty = anonymous only).
    /// </summary>
    public static async Task<List<ServerEntry>> GetServersAsync(long placeId, bool fewestFirst, int maxPages,
        IReadOnlyList<string> cookies, Action<List<ServerEntry>>? pageLoaded, CancellationToken ct, bool fresh = false)
    {
        lock (ServerCache)
            if (!fresh && ServerCache.TryGetValue((placeId, fewestFirst), out var hit) && DateTime.Now - hit.At < TimeSpan.FromMinutes(2))
            {
                pageLoaded?.Invoke(hit.List);
                return hit.List;
            }

        var list = new List<ServerEntry>();
        string cursor = "";
        for (int page = 0; page < maxPages; page++)
        {
            var url = $"https://games.roblox.com/v1/games/{placeId}/servers/Public?sortOrder={(fewestFirst ? "Asc" : "Desc")}"
                    + "&excludeFullGames=true&limit=100" + (cursor.Length > 0 ? "&cursor=" + Uri.EscapeDataString(cursor) : "");
            string? json;
            try { json = await FetchServerPageAsync(url, cookies, ct); }
            catch (ServersLimitedException) when (list.Count > 0) { break; }   // keep what we have

            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    foreach (var e in data.EnumerateArray())
                    {
                        var id = Str(e, "id");
                        if (id.Length == 0) continue;
                        list.Add(new ServerEntry
                        {
                            JobId = id,
                            Playing = (int)Long(e, "playing"),
                            MaxPlayers = (int)Long(e, "maxPlayers"),
                            Ping = e.TryGetProperty("ping", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null,
                            Fps = e.TryGetProperty("fps", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetDouble() : null
                        });
                    }
                cursor = Str(root, "nextPageCursor");
            }
            pageLoaded?.Invoke(list.ToList());
            if (cursor.Length == 0) break;
        }
        lock (ServerCache) ServerCache[(placeId, fewestFirst)] = (DateTime.Now, list);
        return list;
    }

    /// <summary>One page, trying each account that isn't resting, then without a sign-in. No waiting: if all are limited
    /// it throws <see cref="ServersLimitedException"/> with the shortest wait.</summary>
    static async Task<string> FetchServerPageAsync(string url, IReadOnlyList<string> cookies, CancellationToken ct)
    {
        var candidates = new List<string?>();
        for (int i = 0; i < cookies.Count; i++) candidates.Add(cookies[(i + _rotate) % cookies.Count]);
        candidates.Add(null);   // anonymous last
        _rotate++;

        int shortest = int.MaxValue;
        foreach (var cookie in candidates)
        {
            var key = cookie == null ? "anon" : cookie.Length > 40 ? cookie[^40..] : cookie;
            lock (LimitedUntil)
                if (LimitedUntil.TryGetValue(key, out var until) && until > DateTime.Now)
                {
                    shortest = Math.Min(shortest, (int)Math.Ceiling((until - DateTime.Now).TotalSeconds));
                    continue;
                }

            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
            if (cookie != null) req.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
            using var resp = await RobloxApi.Http.SendAsync(req, ct);
            if ((int)resp.StatusCode == 429)
            {
                int wait = 60;
                if (resp.Headers.TryGetValues("x-ratelimit-reset", out var r) && int.TryParse(r.First().Split(',')[0].Trim(), out var reset)) wait = reset;
                else if (resp.Headers.RetryAfter?.Delta is TimeSpan d) wait = (int)Math.Ceiling(d.TotalSeconds);
                wait = Math.Clamp(wait, 1, 60);
                lock (LimitedUntil) LimitedUntil[key] = DateTime.Now.AddSeconds(wait);
                shortest = Math.Min(shortest, wait);
                continue;
            }
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized && cookie != null) continue;   // signed-out account: skip it
            if (!resp.IsSuccessStatusCode)
                throw new RobloxException($"Roblox couldn't list servers for this game (error {(int)resp.StatusCode}).", resp.StatusCode);
            return await resp.Content.ReadAsStringAsync(ct);
        }
        throw new ServersLimitedException(shortest == int.MaxValue ? 30 : shortest);
    }

    // ------------------------------------------------------------------ helpers

    static GameTile Tile(JsonElement g) => new()
    {
        UniverseId = Long(g, "universeId") is > 0 and var u ? u : Long(g, "contentId"),
        PlaceId = Long(g, "rootPlaceId"),
        Name = Str(g, "name"),
        Playing = Long(g, "playerCount"),
        UpVotes = Long(g, "totalUpVotes"),
        DownVotes = Long(g, "totalDownVotes")
    };

    static async Task Safe(Func<Task> work) { try { await work(); } catch { } }

    static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;
    static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    static DateTime? Date(JsonElement e, string name) =>
        DateTime.TryParse(Str(e, name), null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d.ToLocalTime() : null;
}
