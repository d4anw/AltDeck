using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RoDeck.Services;

/// <summary>A Roblox web request that didn't work, with a message that can be shown as is.</summary>
public sealed class RobloxException : Exception
{
    public HttpStatusCode? Status { get; }
    /// <summary>For "too many requests": seconds until Roblox allows more (0 = unknown).</summary>
    public int WaitSeconds { get; init; }
    public RobloxException(string message, HttpStatusCode? status = null, Exception? inner = null) : base(message, inner) => Status = status;
    public bool SignedOut => Status is HttpStatusCode.Unauthorized;
    public bool TooMany => Status is HttpStatusCode.TooManyRequests;
    /// <summary>Roblox wants a security check (a captcha or similar) before it does this; only its own website can do that.</summary>
    public bool Challenge { get; init; }
}

/// <summary>
/// The one way new pages talk to Roblox's web services. It adds the account's sign-in, handles the security token
/// Roblox wants on every change (POST / PATCH / DELETE), waits and retries when Roblox says "too many requests" or
/// has a hiccup, and turns failures into a <see cref="RobloxException"/> with a readable message.
/// </summary>
public static class RobloxHttp
{
    static HttpClient Http => RobloxApi.Http;

    // Roblox's security token (X-CSRF-TOKEN), per sign-in. Keyed by a hash so the cookie itself isn't kept twice.
    static readonly ConcurrentDictionary<string, string> Csrf = new();

    const int MaxTries = 3;
    const int MaxLimitTries = 5;          // "too many requests" is retried more often: it always clears after a while
    const int MaxWaitSeconds = 20;        // longest single wait for Roblox's limit inside one request

    /// <summary>
    /// Per Roblox service (presence.roblox.com, users.roblox.com…): at most 4 requests at a time, and Roblox's own counter
    /// (x-ratelimit-remaining / -reset) so we slow down before it says no. When it does say no, every request to that
    /// service waits until the reset instead of each one failing.
    /// </summary>
    sealed class HostGate
    {
        public readonly SemaphoreSlim Slots = new(4);
        public int Remaining = int.MaxValue;
        public DateTime ResetAt = DateTime.MinValue;
        public DateTime BlockedUntil = DateTime.MinValue;
    }
    static readonly ConcurrentDictionary<string, HostGate> Gates = new();

    static async Task WaitForGateAsync(HostGate g, CancellationToken ct)
    {
        DateTime until;
        lock (g)
        {
            if (g.ResetAt <= DateTime.UtcNow) g.Remaining = int.MaxValue;   // a new window: the old count no longer applies
            until = g.BlockedUntil;
            // nearly out of requests for this window: wait for the reset (and use one of the next window's)
            if (g.Remaining <= 1 && g.ResetAt > DateTime.UtcNow && g.ResetAt > until) until = g.ResetAt;
            if (g.Remaining != int.MaxValue) g.Remaining--;
        }
        var wait = until - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait > TimeSpan.FromSeconds(MaxWaitSeconds) ? TimeSpan.FromSeconds(MaxWaitSeconds) : wait, ct);
    }

    static void ReadLimitHeaders(HostGate g, HttpResponseMessage resp)
    {
        int? remaining = Header(resp, "x-ratelimit-remaining"), reset = Header(resp, "x-ratelimit-reset");
        lock (g)
        {
            if (remaining != null) g.Remaining = remaining.Value;
            if (reset != null) g.ResetAt = DateTime.UtcNow.AddSeconds(Math.Max(1, reset.Value));
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                int secs = reset ?? (int)(resp.Headers.RetryAfter?.Delta?.TotalSeconds ?? 5);
                g.BlockedUntil = DateTime.UtcNow.AddSeconds(Math.Clamp(secs, 1, 60));
                g.Remaining = 0;
            }
        }
    }

    /// <summary>
    /// One pace per Roblox service (host + path, with IDs taken out), not per server: Roblox limits each service on its own,
    /// so a busy 3D-avatar service mustn't hold up the picture service on the same server.
    /// </summary>
    static string GateKey(string url)
    {
        var u = new Uri(url);
        // numbers and marketplace IDs (GUIDs) are the item, not the service: one gate for all of them
        return u.Host + System.Text.RegularExpressions.Regex.Replace(u.AbsolutePath,
            @"/(\d+|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})(?=/|$)", "/{id}");
    }

    static int? Header(HttpResponseMessage resp, string name) =>
        resp.Headers.TryGetValues(name, out var v) && int.TryParse(v.First().Split(',')[0].Trim(), out var n) ? n : null;

    /// <summary>GET and parse JSON. <paramref name="cookie"/> may be null for public data.</summary>
    public static Task<JsonDocument> GetJsonAsync(string url, string? cookie = null, CancellationToken ct = default) =>
        SendJsonAsync(HttpMethod.Get, url, cookie, null, ct);

    /// <summary>POST a JSON body (a string or any object, serialized) and parse the JSON reply.</summary>
    public static Task<JsonDocument> PostJsonAsync(string url, string? cookie, object? body, CancellationToken ct = default) =>
        SendJsonAsync(HttpMethod.Post, url, cookie, body, ct);

    public static Task<JsonDocument> PatchJsonAsync(string url, string? cookie, object? body, CancellationToken ct = default) =>
        SendJsonAsync(HttpMethod.Patch, url, cookie, body, ct);

    public static Task<JsonDocument> DeleteJsonAsync(string url, string? cookie, CancellationToken ct = default) =>
        SendJsonAsync(HttpMethod.Delete, url, cookie, null, ct);

    public static async Task<JsonDocument> SendJsonAsync(HttpMethod method, string url, string? cookie, object? body, CancellationToken ct)
    {
        var text = await SendAsync(method, url, cookie, body, ct);
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text); }
        catch (JsonException ex) { throw new RobloxException("Roblox sent something RoDeck didn't understand.", null, ex); }
    }

    /// <summary>Sends the request and returns the reply text, or throws a <see cref="RobloxException"/>.</summary>
    public static async Task<string> SendAsync(HttpMethod method, string url, string? cookie, object? body, CancellationToken ct)
    {
        string? json = body switch
        {
            null => null,
            string s => s,
            _ => JsonSerializer.Serialize(body)
        };
        bool changes = method != HttpMethod.Get && method != HttpMethod.Head;
        string? key = cookie != null ? Key(cookie) : "(signed out)";   // signed out still gets a CSRF token for POSTs

        var gate = Gates.GetOrAdd(GateKey(url), _ => new HostGate());

        for (int attempt = 1; ; attempt++)
        {
            await WaitForGateAsync(gate, ct);
            using var req = new HttpRequestMessage(method, url);
            if (cookie != null) req.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
            if (changes && key != null && Csrf.TryGetValue(key, out var token)) req.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", token);
            if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            else if (changes) req.Content = new StringContent("", Encoding.UTF8, "application/json");

            HttpResponseMessage resp;
            await gate.Slots.WaitAsync(ct);
            try { resp = await Http.SendAsync(req, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (attempt < MaxTries) { await Task.Delay(1000 * attempt, ct); continue; }
                throw new RobloxException("Roblox isn't answering. Check your internet and try again.", null, ex);
            }
            finally { gate.Slots.Release(); }

            using (resp)
            {
                ReadLimitHeaders(gate, resp);

                // Roblox answers the first change with 403 + a fresh token: remember it and send the request again.
                if (resp.StatusCode == HttpStatusCode.Forbidden && key != null
                    && resp.Headers.TryGetValues("x-csrf-token", out var fresh) && attempt < MaxTries)
                {
                    Csrf[key] = fresh.First();
                    continue;
                }

                // too many requests: the gate now holds every request to this service until Roblox's reset; just go again
                if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxLimitTries) continue;

                if ((int)resp.StatusCode >= 500 && attempt < MaxTries)
                {
                    await Task.Delay(1000 * attempt, ct);
                    continue;
                }

                var text = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode) return text;
                int waitLeft;
                lock (gate) waitLeft = (int)Math.Ceiling(Math.Max(0, (gate.BlockedUntil - DateTime.UtcNow).TotalSeconds));
                throw new RobloxException(Describe(resp.StatusCode, text), resp.StatusCode)
                {
                    WaitSeconds = waitLeft,
                    Challenge = resp.Headers.Contains("rblx-challenge-id") || resp.Headers.Contains("rblx-challenge-type"),
                };
            }
        }
    }

    static string Describe(HttpStatusCode status, string body)
    {
        // Roblox's own message, when it gives one: {"errors":[{"code":..,"message":".."}]}
        string? roblox = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0
                && errs[0].TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                roblox = m.GetString();
        }
        catch { }

        return status switch
        {
            HttpStatusCode.Unauthorized => "This account is signed out. Sign it in again on the Accounts tab.",
            HttpStatusCode.TooManyRequests => "Roblox is getting too many requests. Wait a moment and try again.",
            HttpStatusCode.NotFound => "Roblox couldn't find that.",
            HttpStatusCode.Forbidden => string.IsNullOrWhiteSpace(roblox) ? "Roblox didn't allow that for this account." : roblox!,
            _ when (int)status >= 500 => "Roblox is having trouble right now. Try again in a moment.",
            _ => string.IsNullOrWhiteSpace(roblox) ? $"Roblox answered {(int)status}." : roblox!
        };
    }

    static string Key(string cookie) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cookie)))[..24];
}
