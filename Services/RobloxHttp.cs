using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AltDeck.Services;

/// <summary>A Roblox web request that didn't work, with a message that can be shown as is.</summary>
public sealed class RobloxException : Exception
{
    public HttpStatusCode? Status { get; }
    public RobloxException(string message, HttpStatusCode? status = null, Exception? inner = null) : base(message, inner) => Status = status;
    public bool SignedOut => Status is HttpStatusCode.Unauthorized;
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
        catch (JsonException ex) { throw new RobloxException("Roblox sent something AltDeck didn't understand.", null, ex); }
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
        string? key = cookie != null ? Key(cookie) : null;

        for (int attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, url);
            if (cookie != null) req.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
            if (changes && key != null && Csrf.TryGetValue(key, out var token)) req.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", token);
            if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            else if (changes) req.Content = new StringContent("", Encoding.UTF8, "application/json");

            HttpResponseMessage resp;
            try { resp = await Http.SendAsync(req, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (attempt < MaxTries) { await Task.Delay(1000 * attempt, ct); continue; }
                throw new RobloxException("Roblox isn't answering. Check your internet and try again.", null, ex);
            }

            using (resp)
            {
                // Roblox answers the first change with 403 + a fresh token: remember it and send the request again.
                if (resp.StatusCode == HttpStatusCode.Forbidden && key != null
                    && resp.Headers.TryGetValues("x-csrf-token", out var fresh) && attempt < MaxTries)
                {
                    Csrf[key] = fresh.First();
                    continue;
                }

                if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxTries)
                {
                    var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * attempt);
                    if (wait > TimeSpan.FromSeconds(10)) wait = TimeSpan.FromSeconds(10);
                    await Task.Delay(wait, ct);
                    continue;
                }

                if ((int)resp.StatusCode >= 500 && attempt < MaxTries)
                {
                    await Task.Delay(1000 * attempt, ct);
                    continue;
                }

                var text = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode) return text;
                throw new RobloxException(Describe(resp.StatusCode, text), resp.StatusCode);
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
