using System.Globalization;
using System.Text.Json;

namespace RoDeck.Services;

/// <summary>One of the account's own private servers, in the list of all of them.</summary>
public sealed partial class MyServer : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public long UniverseId { get; init; }
    public long PlaceId { get; init; }
    public string GameName { get; init; } = "";
    public bool Active { get; init; }
    public long Price { get; init; }
    public bool WillRenew { get; init; }
    public DateTime? Expires { get; init; }
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string iconUrl = "";
    public string SubText => (Price > 0 ? $"R$ {Price:N0} a month" : "Free") + (Active ? "" : " · joining off");
}

/// <summary>A private server's settings, as on its "Configure Private Server" page.</summary>
public sealed class ServerSettings
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public long UniverseId { get; init; }
    public long PlaceId { get; init; }
    public string GameName { get; init; } = "";
    public bool Active { get; init; }
    public bool FriendsAllowed { get; init; }
    public string Link { get; init; } = "";
    public long Price { get; init; }
    public bool SubscriptionActive { get; init; }
    public bool Expired { get; init; }
    public DateTime? Expires { get; init; }
    public List<(long Id, string Name, string DisplayName)> Members { get; init; } = new();
}

/// <summary>
/// The account's own private servers: the list of all of them, and each one's settings (name, joining on / off,
/// friends, members, the share link, renewing), the same services as roblox.com's Configure page.
/// </summary>
public static class RobloxPrivateServers
{
    const string Games = "https://games.roblox.com";

    /// <summary>Every private server the account owns, across all games.</summary>
    public static async Task<List<MyServer>> GetMineAsync(string cookie, CancellationToken ct)
    {
        var list = new List<MyServer>();
        string? cursor = null;
        for (int page = 0; page < 10; page++)
        {
            using var doc = await RobloxHttp.GetJsonAsync($"{Games}/v1/private-servers/my-private-servers?privateServersTab=MyPrivateServers&itemsPerPage=100"
                                                          + (cursor != null ? "&cursor=" + Uri.EscapeDataString(cursor) : ""), cookie, ct);
            foreach (var s in doc.RootElement.Arr("data"))
            {
                DateTime? exp = DateTime.TryParse(s.Text("expirationDate"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
                long id = s.Num("privateServerId") is > 0 and var pid ? pid : s.Num("vipServerId");
                list.Add(new MyServer
                {
                    Id = id, Name = s.Text("name"), UniverseId = s.Num("universeId"), PlaceId = s.Num("placeId"),
                    GameName = s.Text("universeName") is { Length: > 0 } un ? un : s.Text("placeName"),
                    Active = !(s.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.False),
                    Price = s.Num("priceInRobux"), WillRenew = RobloxCommunities.Bool(s, "willRenew"), Expires = exp,
                });
            }
            cursor = doc.RootElement.Text("nextPageCursor");
            if (cursor.Length == 0) break;
        }
        return list.Where(s => s.Id > 0).ToList();
    }

    public static async Task<ServerSettings> GetSettingsAsync(string cookie, long id, CancellationToken ct)
    {
        using var doc = await RobloxHttp.GetJsonAsync($"{Games}/v1/vip-servers/{id}", cookie, ct);
        var r = doc.RootElement;
        var game = r.TryGetProperty("game", out var g) ? g : default;
        var root = game.ValueKind == JsonValueKind.Object && game.TryGetProperty("rootPlace", out var rp) ? rp : default;
        var sub = r.TryGetProperty("subscription", out var sb) ? sb : default;
        var perm = r.TryGetProperty("permissions", out var pm) ? pm : default;
        DateTime? exp = DateTime.TryParse(sub.Text("expirationDate"), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
        return new ServerSettings
        {
            Id = r.Num("id") is > 0 and var sid ? sid : id,
            Name = r.Text("name"),
            UniverseId = game.Num("id"), PlaceId = root.Num("id"), GameName = game.Text("name"),
            Active = RobloxCommunities.Bool(r, "active"),
            FriendsAllowed = RobloxCommunities.Bool(perm, "friendsAllowed"),
            Link = r.Text("link"),
            Price = sub.Num("price"),
            SubscriptionActive = RobloxCommunities.Bool(sub, "active"),
            Expired = RobloxCommunities.Bool(sub, "expired"),
            Expires = exp,
            Members = perm.Arr("users").Select(u => (u.Num("id"), u.Text("name"), u.Text("displayName"))).Where(u => u.Item1 > 0).ToList(),
        };
    }

    public static async Task RenameAsync(string cookie, long id, string name, CancellationToken ct)
    {
        using var _ = await RobloxHttp.PatchJsonAsync($"{Games}/v1/vip-servers/{id}", cookie, new { name }, ct);
    }

    /// <summary>Joining on or off (off also ends the current link).</summary>
    public static async Task SetActiveAsync(string cookie, long id, bool active, CancellationToken ct)
    {
        using var _ = await RobloxHttp.PatchJsonAsync($"{Games}/v1/vip-servers/{id}", cookie, new { active }, ct);
    }

    /// <summary>A new share link: the old one stops working. Returns the new one.</summary>
    public static async Task<string> NewLinkAsync(string cookie, long id, CancellationToken ct)
    {
        using var doc = await RobloxHttp.PatchJsonAsync($"{Games}/v1/vip-servers/{id}", cookie, new { newJoinCode = true }, ct);
        return doc.RootElement.Text("link");
    }

    public static async Task SetFriendsAllowedAsync(string cookie, long id, bool allowed, CancellationToken ct)
    {
        using var _ = await RobloxHttp.PatchJsonAsync($"{Games}/v1/vip-servers/{id}/permissions", cookie,
            new { friendsAllowed = allowed }, ct);
    }

    public static async Task AddMemberAsync(string cookie, long id, long userId, CancellationToken ct)
    {
        using var _ = await RobloxHttp.PatchJsonAsync($"{Games}/v1/vip-servers/{id}/permissions", cookie,
            new { usersToAdd = new[] { userId } }, ct);
    }

    public static async Task RemoveMemberAsync(string cookie, long id, long userId, CancellationToken ct)
    {
        using var _ = await RobloxHttp.PatchJsonAsync($"{Games}/v1/vip-servers/{id}/permissions", cookie,
            new { usersToRemove = new[] { userId } }, ct);
    }

    /// <summary>A paid server: renew every month (on) or let it end when this month is over (off).</summary>
    public static async Task SetRenewingAsync(string cookie, long id, bool renew, long price, CancellationToken ct)
    {
        using var _ = await RobloxHttp.PatchJsonAsync($"{Games}/v1/vip-servers/{id}/subscription", cookie, new { active = renew, price }, ct);
    }

    /// <summary>A player by their exact username, or null.</summary>
    public static async Task<(long Id, string Name, string DisplayName)?> FindUserAsync(string username, CancellationToken ct)
    {
        using var doc = await RobloxHttp.PostJsonAsync("https://users.roblox.com/v1/usernames/users", null,
            new { usernames = new[] { username.Trim() }, excludeBannedUsers = true }, ct);
        foreach (var u in doc.RootElement.Arr("data"))
            if (u.Num("id") is > 0 and var uid) return (uid, u.Text("name"), u.Text("displayName"));
        return null;
    }
}
