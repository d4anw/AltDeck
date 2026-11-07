using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AltDeck.Services;

public static class RobloxApi
{
    internal static readonly HttpClient Http = CreateClient();   // shared with RobloxHttp
    public record UserInfo(long Id, string Name, string DisplayName);

    static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { UseCookies = false });
        var v = typeof(RobloxApi).Assembly.GetName().Version;
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Mozilla/5.0 AltDeck/{v?.Major ?? 0}.{v?.Minor ?? 0}.{v?.Build ?? 0}");
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }

    public static string CleanCookie(string raw)
    {
        raw = raw.Trim().Trim('"');
        const string prefix = ".ROBLOSECURITY=";
        if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) raw = raw.Substring(prefix.Length);
        return raw.TrimEnd(';').Trim();
    }

    static HttpRequestMessage Req(HttpMethod method, string url, string cookie)
    {
        var r = new HttpRequestMessage(method, url);
        r.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
        return r;
    }

    /// <summary>
    /// True when Roblox's web API answers at all (the internet is up and Roblox isn't down).
    /// Any answer below 500 counts, even "rate limited", because it proves the connection works.
    /// </summary>
    public static async Task<bool> IsReachableAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var r = await Http.GetAsync("https://users.roblox.com/v1/users/1", HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return (int)r.StatusCode < 500;
        }
        catch { return false; }
    }

    public static async Task<UserInfo?> GetAuthenticatedUserAsync(string cookie)
    {
        using var r = Req(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated", cookie);
        using var resp = await Http.SendAsync(r);
        // Only 401/403 mean "this sign-in is no longer valid". Rate limits (429) and outages must not mark it signed out.
        if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden) return null;
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Roblox answered {(int)resp.StatusCode}. Try again in a moment.");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var e = doc.RootElement;
        return new UserInfo(e.GetProperty("id").GetInt64(),
                            e.GetProperty("name").GetString() ?? "",
                            e.GetProperty("displayName").GetString() ?? "");
    }

    public static async Task<string> GetAvatarUrlAsync(long userId)
    {
        try
        {
            var url = $"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={userId}&size=150x150&format=Png";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            return doc.RootElement.GetProperty("data")[0].GetProperty("imageUrl").GetString() ?? "";
        }
        catch { return ""; }
    }

    /// <summary>Gets the one-time ticket Roblox's player uses to sign in (handles the CSRF handshake).</summary>
    public static async Task<string> GetAuthTicketAsync(string cookie)
    {
        string? csrf = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var r = Req(HttpMethod.Post, "https://auth.roblox.com/v1/authentication-ticket", cookie);
            r.Headers.TryAddWithoutValidation("Referer", "https://www.roblox.com/");
            r.Headers.TryAddWithoutValidation("Origin", "https://www.roblox.com");
            if (csrf != null) r.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);
            r.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(r);
            if (resp.Headers.TryGetValues("rbx-authentication-ticket", out var ticket)) return ticket.First();
            if (resp.Headers.TryGetValues("x-csrf-token", out var token)) { csrf = token.First(); continue; }
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                throw new InvalidOperationException("This account is signed out. Add it again on the Accounts page.");
            if ((int)resp.StatusCode == 429)
                throw new InvalidOperationException("Roblox is limiting sign-ins right now. Raise the launch delay a little and try again.");
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Roblox couldn't start a sign-in (error {(int)resp.StatusCode}). Try again in a moment.");
        }
        throw new InvalidOperationException("Couldn't get a sign-in ticket. The account may need to sign in again.");
    }

    /// <summary>Resolves a private-server link code into the access code the launcher needs (best effort).</summary>
    public static async Task<string?> GetPrivateAccessCodeAsync(string cookie, long placeId, string linkCode)
    {
        try
        {
            using var r = Req(HttpMethod.Get,
                $"https://www.roblox.com/games/{placeId}?privateServerLinkCode={linkCode}", cookie);
            using var resp = await Http.SendAsync(r);
            var html = await resp.Content.ReadAsStringAsync();
            var m = Regex.Match(html, @"joinPrivateGame\(\d+,\s*'([\w-]+)'");
            if (!m.Success) m = Regex.Match(html, @"accessCode[""']?\s*[:=]\s*[""']([0-9a-fA-F-]{36})[""']", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Turns a roblox.com/share?code=...&amp;type=Server link into the place ID and private-server link code,
    /// using the account's own session. Returns the target, or a short message saying what went wrong.
    /// </summary>
    public static async Task<(LaunchTarget? Target, string? Error)> ResolveShareLinkAsync(string cookie, string code, string type)
    {
        try
        {
            string? csrf = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                using var r = Req(HttpMethod.Post, "https://apis.roblox.com/sharelinks/v1/resolve-link", cookie);
                r.Headers.TryAddWithoutValidation("Referer", "https://www.roblox.com/");
                r.Headers.TryAddWithoutValidation("Origin", "https://www.roblox.com");
                if (csrf != null) r.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);
                r.Content = new StringContent(
                    JsonSerializer.Serialize(new { linkId = code, linkType = type }), Encoding.UTF8, "application/json");
                using var resp = await Http.SendAsync(r);

                if (resp.StatusCode == HttpStatusCode.Forbidden && resp.Headers.TryGetValues("x-csrf-token", out var token))
                {
                    csrf = token.First();
                    continue;
                }
                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                    return (null, "That account is signed out. Check its sign-in, then try again.");
                if (!resp.IsSuccessStatusCode)
                    return (null, $"Roblox couldn't open that link (error {(int)resp.StatusCode}).");

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var root = doc.RootElement;

                if (root.TryGetProperty("privateServerInviteData", out var ps) && ps.ValueKind == JsonValueKind.Object)
                {
                    var status = ps.TryGetProperty("status", out var st) ? st.ToString() : "";
                    if (status.Length > 0 && !status.Equals("Valid", StringComparison.OrdinalIgnoreCase))
                        return (null, $"That private server link isn't usable ({status}). Ask for a fresh link.");
                    long place = ps.TryGetProperty("placeId", out var pl) && pl.ValueKind == JsonValueKind.Number ? pl.GetInt64() : 0;
                    var link = ps.TryGetProperty("linkCode", out var lc) ? lc.ToString() : "";
                    if (place == 0 || link.Length == 0) return (null, "Roblox didn't say which private server that link is for.");
                    return (new LaunchTarget(place, null, link), null);
                }

                if (root.TryGetProperty("experienceInviteData", out var inv) && inv.ValueKind == JsonValueKind.Object)
                {
                    long place = inv.TryGetProperty("placeId", out var pl) && pl.ValueKind == JsonValueKind.Number ? pl.GetInt64() : 0;
                    var job = inv.TryGetProperty("instanceId", out var ji) && ji.ValueKind == JsonValueKind.String ? ji.GetString() : null;
                    if (place == 0) return (null, "Roblox didn't say which game that invite is for.");
                    return (new LaunchTarget(place, job, null), null);
                }

                return (null, "That link isn't a private server or game invite.");
            }
            return (null, "Roblox refused the request. The account may need to sign in again.");
        }
        catch (Exception ex)
        {
            return (null, "Couldn't read that link: " + ex.Message);
        }
    }

    public record Presence(int Type, string Location, long PlaceId, string? GameId = null, long RootPlaceId = 0);

    /// <summary>Online / in game / in Studio status for one account, fetched with that account's own session.</summary>
    /// <summary>The account's Robux balance, or null when Roblox didn't say (signed out, rate limited, down).</summary>
    public static async Task<long?> GetRobuxAsync(string cookie, long userId)
    {
        try
        {
            using var r = Req(HttpMethod.Get, $"https://economy.roblox.com/v1/users/{userId}/currency", cookie);
            using var resp = await Http.SendAsync(r);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("robux", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
        }
        catch { return null; }
    }

    /// <summary>Whether the account has Roblox Premium, or null when Roblox didn't say.</summary>
    public static async Task<bool?> GetPremiumAsync(string cookie, long userId)
    {
        try
        {
            using var r = Req(HttpMethod.Get, $"https://premiumfeatures.roblox.com/v1/users/{userId}/validate-membership", cookie);
            using var resp = await Http.SendAsync(r);
            if (!resp.IsSuccessStatusCode) return null;
            var text = (await resp.Content.ReadAsStringAsync()).Trim();
            return bool.TryParse(text, out var b) ? b : null;
        }
        catch { return null; }
    }

    public static async Task<Presence?> GetPresenceAsync(string cookie, long userId)
    {
        string? csrf = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var r = Req(HttpMethod.Post, "https://presence.roblox.com/v1/presence/users", cookie);
            if (csrf != null) r.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);
            r.Content = new StringContent("{\"userIds\":[" + userId + "]}", Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(r);
            if (resp.StatusCode == HttpStatusCode.Forbidden && resp.Headers.TryGetValues("x-csrf-token", out var token))
            {
                csrf = token.First();
                continue;
            }
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var arr = doc.RootElement.GetProperty("userPresences");
            if (arr.GetArrayLength() == 0) return null;
            var e = arr[0];
            int type = e.GetProperty("userPresenceType").GetInt32();
            string loc = e.TryGetProperty("lastLocation", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() ?? "" : "";
            long place = e.TryGetProperty("placeId", out var pi) && pi.ValueKind == JsonValueKind.Number ? pi.GetInt64() : 0;
            long root = e.TryGetProperty("rootPlaceId", out var ri) && ri.ValueKind == JsonValueKind.Number ? ri.GetInt64() : 0;
            // the server's job ID: only shown when the other player's privacy settings let this account see where they are
            string? game = e.TryGetProperty("gameId", out var gi) && gi.ValueKind == JsonValueKind.String ? gi.GetString() : null;
            return new Presence(type, loc, place, string.IsNullOrWhiteSpace(game) ? null : game, root);
        }
        return null;
    }

    public static async Task<string?> GetPlaceNameAsync(string cookie, long placeId)
    {
        try
        {
            using var r = Req(HttpMethod.Get, $"https://games.roblox.com/v1/games/multiget-place-details?placeIds={placeId}", cookie);
            using var resp = await Http.SendAsync(r);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) return null;
            return doc.RootElement[0].GetProperty("name").GetString();
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ server browser

    public record ServerInfo(string Id, int Playing, int MaxPlayers, double? Ping, double? Fps);

    /// <summary>
    /// Lists public servers of a place (up to about 200, not full). No sign-in needed. Returns the servers, or a short message.
    /// </summary>
    public static async Task<(List<ServerInfo> Servers, string? Error)> GetPublicServersAsync(long placeId, bool fewestPlayersFirst)
    {
        var list = new List<ServerInfo>();
        try
        {
            string cursor = "";
            for (int page = 0; page < 2; page++)
            {
                var url = $"https://games.roblox.com/v1/games/{placeId}/servers/Public?sortOrder={(fewestPlayersFirst ? "Asc" : "Desc")}" +
                          "&excludeFullGames=true&limit=100" + (cursor.Length > 0 ? "&cursor=" + Uri.EscapeDataString(cursor) : "");
                using var resp = await Http.GetAsync(url);
                if ((int)resp.StatusCode == 429)
                    return (list, list.Count > 0 ? null : "Roblox is asking us to slow down. Wait a minute and press Refresh.");
                if (!resp.IsSuccessStatusCode)
                    return (list, list.Count > 0 ? null : $"Roblox couldn't list servers for that game (error {(int)resp.StatusCode}). Check the place ID.");

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in data.EnumerateArray())
                    {
                        var id = e.TryGetProperty("id", out var i) ? i.GetString() : null;
                        if (string.IsNullOrEmpty(id)) continue;
                        int playing = e.TryGetProperty("playing", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
                        int max = e.TryGetProperty("maxPlayers", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : 0;
                        double? ping = e.TryGetProperty("ping", out var pg) && pg.ValueKind == JsonValueKind.Number ? pg.GetDouble() : null;
                        double? fps = e.TryGetProperty("fps", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetDouble() : null;
                        list.Add(new ServerInfo(id!, playing, max, ping, fps));
                    }
                }
                cursor = root.TryGetProperty("nextPageCursor", out var nc) && nc.ValueKind == JsonValueKind.String ? nc.GetString() ?? "" : "";
                if (cursor.Length == 0) break;
                await Task.Delay(400);   // be gentle with the rate limit
            }
            return (list, null);
        }
        catch (Exception ex)
        {
            return (list, list.Count > 0 ? null : "Couldn't reach Roblox: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ join someone

    /// <summary>Turns a username into a user ID (a plain number is taken as the ID). Returns the ID, or a short message.</summary>
    public static async Task<(long Id, string? Error)> ResolveUserAsync(string cookie, string input)
    {
        input = (input ?? "").Trim().TrimStart('@');
        var link = Regex.Match(input, @"roblox\.com/(?:[a-z]{2}/)?users/(\d+)", RegexOptions.IgnoreCase);
        if (link.Success) return (long.Parse(link.Groups[1].Value), null);
        if (long.TryParse(input, out var numeric) && numeric > 0) return (numeric, null);
        if (input.Length < 3) return (0, "Type their Roblox username (or user ID).");

        try
        {
            string? csrf = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var r = Req(HttpMethod.Post, "https://users.roblox.com/v1/usernames/users", cookie);
                if (csrf != null) r.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);
                r.Content = new StringContent(
                    JsonSerializer.Serialize(new { usernames = new[] { input }, excludeBannedUsers = true }),
                    Encoding.UTF8, "application/json");
                using var resp = await Http.SendAsync(r);
                if (resp.StatusCode == HttpStatusCode.Forbidden && resp.Headers.TryGetValues("x-csrf-token", out var token))
                {
                    csrf = token.First();
                    continue;
                }
                if (!resp.IsSuccessStatusCode) return (0, $"Roblox couldn't look that name up (error {(int)resp.StatusCode}).");
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var data = doc.RootElement.GetProperty("data");
                if (data.GetArrayLength() == 0) return (0, $"No Roblox user is called “{input}”.");
                return (data[0].GetProperty("id").GetInt64(), null);
            }
            return (0, "Roblox refused the request. Try again.");
        }
        catch (Exception ex)
        {
            return (0, "Couldn't look that name up: " + ex.Message);
        }
    }
}
