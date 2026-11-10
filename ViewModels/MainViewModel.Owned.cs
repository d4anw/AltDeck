using RoDeck.Models;
using RoDeck.Services;

namespace RoDeck.ViewModels;

// "You own this": a small tick on Marketplace tiles, community store items, related items and on other people's
// Wearing / Inventory tabs. What the account owns is read once (its inventory, by kind, and its bundles) and kept for
// half an hour; a purchase adds to it straight away.
public partial class MainViewModel
{
    private sealed class OwnedSet
    {
        public readonly HashSet<long> Assets = new();
        public readonly HashSet<long> Bundles = new();
        public DateTime At;
    }

    private readonly Dictionary<string, Task<OwnedSet?>> _owned = new();

    // every kind of item the Marketplace shows (wardrobe kinds, plus heads, faces, gear and emotes)
    private static readonly int[] OwnedKinds =
        WardrobeCategories.SelectMany(c => c.Types).Concat(new[] { 17, 18, 19, 61 }).Distinct().ToArray();

    private Task<OwnedSet?> OwnedFor(Account a)
    {
        if (_owned.TryGetValue(a.Id, out var t) && !(t.IsCompleted && (t.Result == null || DateTime.Now - t.Result.At > TimeSpan.FromMinutes(30))))
            return t;
        return _owned[a.Id] = LoadOwnedAsync(a);
    }

    private static async Task<OwnedSet?> LoadOwnedAsync(Account a)
    {
        if (a.UserId <= 0 || string.IsNullOrEmpty(a.Cookie)) return null;
        var set = new OwnedSet();
        using var gate = new SemaphoreSlim(4);
        try
        {
            await Task.WhenAll(OwnedKinds.Select(async kind =>
            {
                await gate.WaitAsync();
                try
                {
                    string? cursor = null;
                    for (int page = 0; page < 30; page++)
                    {
                        var (items, next) = await RobloxAvatar.GetInventoryAsync(a.Cookie, a.UserId, kind, cursor, CancellationToken.None);
                        lock (set) foreach (var i in items) set.Assets.Add(i.AssetId);
                        if (next == null) break;
                        cursor = next;
                    }
                }
                catch (RobloxException) { }
                finally { gate.Release(); }
            }));
            foreach (var id in await RobloxMarket.GetOwnedBundlesAsync(a.Cookie, a.UserId, CancellationToken.None)) set.Bundles.Add(id);
        }
        catch (Exception ex) when (ex is RobloxException or System.Text.Json.JsonException) { }
        set.At = DateTime.Now;
        return set;
    }

    /// <summary>Ticks the items the account top right owns (in the background; the tiles show it when it's known).</summary>
    private async Task MarkOwnedAsync(IEnumerable<MarketItem> items)
    {
        if (CurrentAccount is not { } a) return;
        var list = items.ToList();
        var set = await OwnedFor(a);
        if (set == null || CurrentAccount != a) return;
        foreach (var i in list)
            if ((i.IsBundle ? set.Bundles : set.Assets).Contains(i.Id)) i.Owned = true;
    }

    /// <summary>On someone else's profile: ticks their items that you own too.</summary>
    private async Task MarkOwnedAsync(IEnumerable<WardrobeItem> items)
    {
        if (CurrentAccount is not { } a) return;
        var list = items.ToList();
        var set = await OwnedFor(a);
        if (set == null || CurrentAccount != a) return;
        foreach (var i in list) i.OwnedByMe = set.Assets.Contains(i.AssetId);
    }

    /// <summary>Just bought or got for free: ticked right away, without reading the whole inventory again.</summary>
    private void RememberOwned(Account a, long id, bool bundle)
    {
        if (_owned.TryGetValue(a.Id, out var t) && t.IsCompletedSuccessfully && t.Result is { } set)
            (bundle ? set.Bundles : set.Assets).Add(id);
    }
}
