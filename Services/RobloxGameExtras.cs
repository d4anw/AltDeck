using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RoDeck.Services;

/// <summary>A private server of a game that this account can join (its own, or one it was invited to).</summary>
public sealed class PrivateServer
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public long OwnerId { get; init; }
    public string OwnerName { get; init; } = "";
    public int Players { get; init; }
    public int MaxPlayers { get; init; }
    public string AccessCode { get; init; } = "";
    public bool IsMine { get; init; }
    public string PlayersText => MaxPlayers > 0 ? $"{Players} / {MaxPlayers}" : Players.ToString(CultureInfo.InvariantCulture);
    public double Fill => MaxPlayers > 0 ? Math.Min(1, (double)Players / MaxPlayers) : 0;
    public string OwnerText => IsMine ? "Yours" : "By " + OwnerName;
    public bool CanJoin => AccessCode.Length > 0;
}

/// <summary>A game pass in a game's Store.</summary>
public sealed partial class GamePassInfo : ObservableObject
{
    public long Id { get; init; }
    public long ProductId { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public long? Price { get; init; }
    public bool ForSale { get; init; }
    public long CreatorId { get; init; }
    public string CreatorType { get; init; } = "User";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanBuy), nameof(PriceText))] private bool owned;
    [ObservableProperty] private string iconUrl = "";
    public bool CanBuy => !Owned && ForSale && Price is not null;
    public string PriceText => Owned ? "Owned" : ForSale && Price is long p ? (p == 0 ? "Free" : p.ToString("N0", CultureInfo.CurrentCulture)) : "Off sale";
    public bool ShowRobux => !Owned && ForSale && Price is > 0;
}

/// <summary>A post (announcement) on a community's page.</summary>
public sealed partial class CommunityPost : ObservableObject
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Text { get; init; } = "";
    public string Author { get; init; } = "";
    public string AuthorRole { get; init; } = "";
    public long AuthorId { get; init; }
    public DateTime At { get; init; }
    public long ImageAssetId { get; init; }
    public long Reactions { get; init; }
    [ObservableProperty] private string imageUrl = "";
    [ObservableProperty] private string authorHeadshot = "";
    public bool HasImage => ImageAssetId > 0;
    public bool HasText => Text.Length > 0;
    public string Meta => (AuthorRole.Length > 0 ? AuthorRole + " · " : "") + (At == default ? "" : Ago(At));
    public string ReactionsText => Reactions > 0 ? Fmt.Short(Reactions) : "";
    public string AuthorInitials => Author.Length > 0 ? Author[..Math.Min(2, Author.Length)].ToUpperInvariant() : "?";

    static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays} d ago";
        return utc.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }
}

/// <summary>A badge of a game, with how many players have it.</summary>
public sealed partial class BadgeInfo : ObservableObject
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public long Awarded { get; init; }
    public long AwardedYesterday { get; init; }
    /// <summary>Roblox's own rarity: the share of players who got it (0 when it doesn't say).</summary>
    public double WinRate { get; init; }
    [ObservableProperty] private string iconUrl = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(OwnedText))] private DateTime? ownedAt;
    public string RarityText => WinRate > 0
        ? $"{WinRate.ToString(WinRate < 1 ? "0.0#" : "0.#", CultureInfo.CurrentCulture)}% · {Rarity(WinRate)}"
        : "Rarity unknown";
    public string AwardedText => $"Won by {Fmt.Short(Awarded)} · {Fmt.Short(AwardedYesterday)} yesterday";
    public string OwnedText => OwnedAt is DateTime d ? "You got it " + d.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture) : "";
    public bool IsOwned => OwnedAt != null;

    // Roblox's own words for these bands
    static string Rarity(double pct) => pct switch
    {
        < 1 => "Impossible", < 5 => "Insane", < 10 => "Extreme", < 20 => "Hard", < 50 => "Challenging", < 80 => "Moderate", < 90 => "Easy",
        _ => "Freebie"
    };
}

/// <summary>The parts of a game page under the server finder: private servers, the Store (game passes) and badges.</summary>
public static class RobloxGameExtras
{
    /// <summary>Private servers of this place that the account can see (its own and the ones it was invited to).</summary>
    public static async Task<List<PrivateServer>> GetPrivateServersAsync(string cookie, long placeId, long myUserId, CancellationToken ct)
    {
        var list = new List<PrivateServer>();
        string? cursor = null;
        for (int page = 0; page < 5; page++)
        {
            using var doc = await RobloxHttp.GetJsonAsync(
                $"https://games.roblox.com/v1/games/{placeId}/private-servers?limit=100" + (cursor != null ? "&cursor=" + Uri.EscapeDataString(cursor) : ""), cookie, ct);
            foreach (var s in doc.RootElement.Arr("data"))
            {
                var owner = s.TryGetProperty("owner", out var o) ? o : default;
                long ownerId = owner.Num("id");
                list.Add(new PrivateServer
                {
                    Id = s.Num("vipServerId"), Name = s.Text("name"),
                    OwnerId = ownerId, OwnerName = owner.Text("displayName") is { Length: > 0 } d ? d : owner.Text("name"),
                    Players = s.Arr("players").Count(), MaxPlayers = (int)s.Num("maxPlayers"),
                    AccessCode = s.Text("accessCode"), IsMine = ownerId == myUserId,
                });
            }
            cursor = doc.RootElement.Text("nextPageCursor");
            if (cursor.Length == 0) break;
        }
        return list.OrderByDescending(s => s.IsMine).ThenByDescending(s => s.Players).ToList();
    }

    /// <summary>The game's passes. Signed in, Roblox also says which ones the account owns.</summary>
    public static async Task<List<GamePassInfo>> GetPassesAsync(string? cookie, long universeId, CancellationToken ct)
    {
        var list = new List<GamePassInfo>();
        string? token = null;
        for (int page = 0; page < 5; page++)
        {
            using var doc = await RobloxHttp.GetJsonAsync(
                $"https://apis.roblox.com/game-passes/v1/universes/{universeId}/game-passes?passView=Full&pageSize=100"
                + (token != null ? "&pageToken=" + Uri.EscapeDataString(token) : ""), cookie, ct);
            foreach (var p in doc.RootElement.Arr("gamePasses"))
            {
                var creator = p.TryGetProperty("creator", out var c) ? c : default;
                long? price = p.TryGetProperty("price", out var pr) && pr.ValueKind == JsonValueKind.Number ? pr.GetInt64() : null;
                list.Add(new GamePassInfo
                {
                    Id = p.Num("id"), ProductId = p.Num("productId"),
                    Name = p.Text("displayName") is { Length: > 0 } dn ? dn : p.Text("name"),
                    Description = p.Text("displayDescription") is { Length: > 0 } dd ? dd : p.Text("description"),
                    Price = price, ForSale = RobloxCommunities.Bool(p, "isForSale"),
                    Owned = RobloxCommunities.Bool(p, "isOwned"),
                    CreatorId = creator.Num("creatorId"), CreatorType = creator.Text("creatorType") is { Length: > 0 } ct2 ? ct2 : "User",
                });
            }
            token = doc.RootElement.Text("nextPageToken");
            if (token.Length == 0) break;
        }
        return list.OrderByDescending(p => p.ForSale).ThenBy(p => p.Price ?? long.MaxValue).ToList();
    }

    /// <summary>
    /// Buys a game pass at exactly <paramref name="pass"/>'s price (Roblox refuses instead of charging something else
    /// if the price changed). Returns Roblox's reason when it didn't go through.
    /// </summary>
    public static async Task<PurchaseResult> BuyPassAsync(string cookie, long userId, long universeId, GamePassInfo pass, CancellationToken ct)
    {
        if (pass.ProductId <= 0 || pass.Price is not long price) return new(false, "Roblox didn't say how to buy this pass.");
        // the same request as the Buy button on roblox.com (its game-pass service), with one key: never charged twice
        using var doc = await RobloxHttp.PostJsonAsync($"https://apis.roblox.com/game-passes/v1/game-passes/{pass.ProductId}/purchase", cookie,
            new
            {
                expectedCurrency = 1, expectedPrice = price, expectedSellerId = pass.CreatorId,
                expectedPurchaserId = userId.ToString(CultureInfo.InvariantCulture), expectedPurchaserType = "User",
                idempotencyKey = Guid.NewGuid().ToString(),
            }, ct);
        var r = doc.RootElement;
        var why = r.Text("errorMessage") is { Length: > 0 } e1 ? e1 : r.Text("errorMsg") is { Length: > 0 } e2 ? e2 : r.Text("reason");
        if (RobloxCommunities.Bool(r, "purchased")) return new(true, "");
        // the answer doesn't always say: whether the account owns it now does
        try
        {
            var passes = await GetPassesAsync(cookie, universeId, ct);
            if (passes.FirstOrDefault(x => x.Id == pass.Id) is { Owned: true }) return new(true, "");
        }
        catch (RobloxException) { }
        return new(false, why.Length > 0 ? why : "Roblox didn't sell it. Nothing was charged.");
    }

    /// <summary>The game's badges, the most-won first.</summary>
    public static async Task<List<BadgeInfo>> GetBadgesAsync(long universeId, CancellationToken ct)
    {
        var list = new List<BadgeInfo>();
        string? cursor = null;
        for (int page = 0; page < 5; page++)
        {
            using var doc = await RobloxHttp.GetJsonAsync(
                $"https://badges.roblox.com/v1/universes/{universeId}/badges?limit=100&sortOrder=Asc" + (cursor != null ? "&cursor=" + Uri.EscapeDataString(cursor) : ""), null, ct);
            foreach (var b in doc.RootElement.Arr("data"))
            {
                if (b.ValueKind == JsonValueKind.Object && b.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False) continue;
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
            cursor = doc.RootElement.Text("nextPageCursor");
            if (cursor.Length == 0) break;
        }
        return list;
    }

    /// <summary>Which of these badges the user has, and since when (Roblox may hide it for private inventories).</summary>
    public static async Task<Dictionary<long, DateTime>> GetOwnedBadgesAsync(string? cookie, long userId, IEnumerable<long> badgeIds, CancellationToken ct)
    {
        var result = new Dictionary<long, DateTime>();
        foreach (var chunk in badgeIds.Distinct().Chunk(100))
        {
            using var doc = await RobloxHttp.GetJsonAsync(
                $"https://badges.roblox.com/v1/users/{userId}/badges/awarded-dates?badgeIds={string.Join(',', chunk)}", cookie, ct);
            foreach (var x in doc.RootElement.Arr("data"))
                if (DateTime.TryParse(x.Text("awardedDate"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
                    result[x.Num("badgeId")] = d;
        }
        return result;
    }

    public static async Task<Dictionary<long, string>> GetIconsAsync(string service, string param, IEnumerable<long> ids, CancellationToken ct)
    {
        var result = new Dictionary<long, string>();
        foreach (var chunk in ids.Distinct().Where(i => i > 0).Chunk(100))
        {
            using var doc = await RobloxHttp.GetJsonAsync(
                $"https://thumbnails.roblox.com/v1/{service}?{param}={string.Join(',', chunk)}&size=150x150&format=Png&isCircular=false", null, ct);
            foreach (var t in doc.RootElement.Arr("data"))
                if (t.Text("state") == "Completed" && t.Text("imageUrl") is { Length: > 0 } u) result[t.Num("targetId")] = u;
        }
        return result;
    }

    // ------------------------------------------------------------------ your About text

    /// <summary>
    /// Changes the account's About text, the way roblox.com's profile editor does (a form to the users service).
    /// Roblox filters it; the saved text is read back from the profile.
    /// </summary>
    public static async Task<string> SetAboutAsync(string cookie, long userId, string text, CancellationToken ct)
    {
        using (await RobloxHttp.PostJsonAsync("https://users.roblox.com/v1/description", cookie,
                   new FormBody(new Dictionary<string, string> { ["description"] = text }), ct)) { }
        try
        {
            using var doc = await RobloxHttp.GetJsonAsync($"https://users.roblox.com/v1/users/{userId}", null, ct);
            return doc.RootElement.Text("description");
        }
        catch (RobloxException) { return text; }
    }

    /// <summary>The latest posts (announcements) of a community, newest first. Communities that hide them give none.</summary>
    public static async Task<(List<CommunityPost> Posts, string? Next)> GetPostsAsync(long groupId, string? cookie, string? cursor, CancellationToken ct)
    {
        JsonDocument doc;
        try
        {
            doc = await RobloxHttp.GetJsonAsync($"https://groups.roblox.com/v1/groups/{groupId}/announcements?limit=10"
                                                + (cursor != null ? "&cursor=" + Uri.EscapeDataString(cursor) : ""), cookie, ct);
        }
        catch (RobloxException ex) when (ex.Status is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound)
        {
            return (new(), null);   // posts are off for this community, or only for members
        }
        using (doc)
        {
            var list = new List<CommunityPost>();
            foreach (var x in doc.RootElement.Arr("data"))
            {
                var who = x.TryGetProperty("creatorInfo", out var w) ? w : default;
                var msg = x.TryGetProperty("message", out var m) ? m : default;
                var content = msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("content", out var c) ? c : default;
                var media = msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("media", out var me) ? me : default;
                long reactions = msg.Arr("reactions").Sum(r => r.Num("reactionCount"));
                DateTime.TryParse(x.Text("createdAt"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at);
                list.Add(new CommunityPost
                {
                    Id = x.Text("id"),
                    Title = x.Text("nameToDisplay") is { Length: > 0 } t ? t : x.Text("name"),
                    Text = PostText(content),
                    Author = who.Text("displayName"),
                    AuthorRole = who.Text("groupRoleName"),
                    AuthorId = x.Num("createdBy"),
                    At = at,
                    ImageAssetId = media.ValueKind == JsonValueKind.Object && RobloxCommunities.Bool(media, "isApproved") ? media.Num("assetId") : 0,
                    Reactions = reactions,
                });
            }
            var next = doc.RootElement.Text("nextPageCursor");
            return (list, next.Length > 0 ? next : null);
        }
    }

    /// <summary>A post's text: Roblox keeps it as plain text, markdown or a "slate" document (blocks of text).</summary>
    static string PostText(JsonElement content)
    {
        if (content.Text("plainText") is { Length: > 0 } plain) return plain.Trim();
        if (content.Text("markdown") is { Length: > 0 } md) return md.Trim();
        var slate = content.Text("slate");
        if (slate.Length == 0) return "";
        try
        {
            using var doc = JsonDocument.Parse(slate);
            var blocks = new List<string>();
            void Walk(JsonElement e, System.Text.StringBuilder sb)
            {
                if (e.ValueKind != JsonValueKind.Object) return;
                if (e.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) sb.Append(t.GetString());
                foreach (var child in e.Arr("children")) Walk(child, sb);
            }
            foreach (var block in doc.RootElement.Arr("children"))
            {
                var sb = new System.Text.StringBuilder();
                Walk(block, sb);
                blocks.Add(sb.ToString());
            }
            return string.Join("\n", blocks).Trim();
        }
        catch (JsonException) { return ""; }
    }
}
