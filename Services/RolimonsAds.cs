using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoDeck.Services;

/// <summary>The answer to one ad post. <see cref="WaitSeconds"/> &gt; 0 when Rolimons said "too soon".</summary>
public sealed record AdResult(bool Ok, string Message, int WaitSeconds = 0);

/// <summary>
/// Posts a trade ad on Rolimons (api.rolimons.com/tradeads/v1/createad), the same way their own site does. It needs
/// the account's Rolimons verification cookie, which the user gets by verifying on rolimons.com and pastes into RoDeck.
/// That cookie is kept encrypted for this Windows user and only ever sent to Rolimons. Rolimons allows one ad per
/// account every 15 minutes.
/// </summary>
public static class RolimonsAds
{
    /// <summary>What can be asked for besides items (Rolimons' request tags).</summary>
    public static readonly (string Tag, string Name)[] Tags =
    {
        ("any", "Any"), ("demand", "Demand"), ("rares", "Rares"), ("robux", "Robux"), ("upgrade", "Upgrade"),
        ("downgrade", "Downgrade"), ("rap", "RAP"), ("wishlist", "Wishlist"), ("projecteds", "Projecteds"), ("adds", "Adds")
    };

    public const int MinMinutes = 15;

    static readonly HttpClient Http = new(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(20) };

    public static async Task<AdResult> PostAsync(string verification, long playerId, IEnumerable<long> offer, IEnumerable<long> request,
                                                 IEnumerable<string> tags, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["player_id"] = playerId,
            ["offer_item_ids"] = new JsonArray(offer.Select(i => (JsonNode)i).ToArray()),
            ["request_item_ids"] = new JsonArray(request.Select(i => (JsonNode)i).ToArray()),
            ["request_tags"] = new JsonArray(tags.Select(t => (JsonNode)t).ToArray())
        }.ToJsonString();
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.rolimons.com/tradeads/v1/createad")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("Cookie", "_RoliVerification=" + CleanVerification(verification));
        req.Headers.TryAddWithoutValidation("User-Agent", "RoDeck");
        HttpResponseMessage resp;
        try { resp = await Http.SendAsync(req, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return new(false, "Rolimons isn't answering. Trying again later."); }
        using (resp)
        {
            string text;
            try { text = await resp.Content.ReadAsStringAsync(ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return new(false, "Rolimons isn't answering. Trying again later."); }
            string msg = "";
            bool success = false;
            try
            {
                using var doc = JsonDocument.Parse(text);
                success = doc.RootElement.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
                if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) msg = m.GetString() ?? "";
            }
            catch (JsonException) { }
            if (resp.IsSuccessStatusCode && success) return new(true, "Ad posted.");
            if (resp.StatusCode == HttpStatusCode.TooManyRequests || msg.Contains("cooldown", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("recent", StringComparison.OrdinalIgnoreCase))
                return new(false, msg.Length > 0 ? msg : "Rolimons says it's too soon for another ad.", 60);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || msg.Contains("verification", StringComparison.OrdinalIgnoreCase))
                return new(false, (msg.Length > 0 ? msg : "Rolimons didn't accept the verification cookie") + ". Paste a fresh one from rolimons.com.");
            return new(false, msg.Length > 0 ? $"Rolimons: {msg}" : $"Rolimons answered {(int)resp.StatusCode}.");
        }
    }

    /// <summary>What was pasted, without "_RoliVerification=", quotes, ";" or line breaks around it.</summary>
    public static string CleanVerification(string raw)
    {
        var v = (raw ?? "").Trim().Trim('"');
        const string prefix = "_RoliVerification=";
        if (v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) v = v[prefix.Length..];
        return new string(v.TrimEnd(';').Where(c => !char.IsWhiteSpace(c)).ToArray());
    }

    // ---- the verification cookie, encrypted for this Windows user (like the vault without a master password)

    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RoDeck.rolimons.v1");

    public static string Protect(string plain) =>
        plain.Length == 0 ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    public static string Unprotect(string stored)
    {
        if (stored.Length == 0) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }
}
