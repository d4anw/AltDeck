using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace AltDeck.Services;

/// <summary>
/// Pictures from the web (game icons, avatars, items), kept in memory and on disk in
/// %LocalAppData%\AltDeck\cache\images so pages open fast and don't download the same picture twice.
/// Roblox's picture links change when the picture changes, so a cached file never goes stale; files unused for
/// 14 days are removed at start.
/// </summary>
public static class ImageCache
{
    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AltDeck", "cache", "images");

    const int MemoryLimit = 600;
    static readonly ConcurrentDictionary<string, BitmapImage> Memory = new();
    static readonly ConcurrentDictionary<string, Task<BitmapImage?>> Loading = new();
    static readonly SemaphoreSlim Downloads = new(6);   // at most 6 downloads at once

    /// <summary>The picture at <paramref name="url"/>, or null when it can't be loaded. Safe to call from any thread.</summary>
    public static Task<BitmapImage?> GetAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return Task.FromResult<BitmapImage?>(null);
        if (Memory.TryGetValue(url, out var hit)) return Task.FromResult<BitmapImage?>(hit);
        return Loading.GetOrAdd(url, u => LoadAsync(u));
    }

    static async Task<BitmapImage?> LoadAsync(string url)
    {
        try
        {
            var file = Path.Combine(Dir, Hash(url));
            byte[]? bytes = null;
            if (File.Exists(file))
            {
                try { bytes = await File.ReadAllBytesAsync(file); File.SetLastAccessTimeUtc(file, DateTime.UtcNow); } catch { }
            }
            if (bytes == null || bytes.Length == 0)
            {
                await Downloads.WaitAsync();
                try { bytes = await RobloxApi.Http.GetByteArrayAsync(url); }
                finally { Downloads.Release(); }
                try { Directory.CreateDirectory(Dir); await File.WriteAllBytesAsync(file, bytes); } catch { }
            }

            var img = new BitmapImage();
            using (var ms = new MemoryStream(bytes))
            {
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.StreamSource = ms;
                img.EndInit();
            }
            img.Freeze();   // usable from any thread

            if (Memory.Count >= MemoryLimit) Memory.Clear();   // simple: start over rather than track use
            Memory[url] = img;
            return img;
        }
        catch { return null; }
        finally { Loading.TryRemove(url, out _); }
    }

    /// <summary>At start: removes pictures nobody looked at for 14 days.</summary>
    public static void Prune()
    {
        Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(Dir)) return;
                var cutoff = DateTime.UtcNow.AddDays(-14);
                foreach (var f in Directory.EnumerateFiles(Dir))
                    try { if (File.GetLastAccessTimeUtc(f) < cutoff && File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f); } catch { }
            }
            catch { }
        });
    }

    static string Hash(string url) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32].ToLowerInvariant();
}

/// <summary>
/// Roblox picture links for many things at once (thumbnails.roblox.com batch), remembered for the session.
/// Types: "GameIcon" (universe IDs), "GameThumbnail" (universe IDs), "AvatarHeadShot" / "Avatar" (user IDs), "Asset" (asset IDs).
/// </summary>
public static class Thumbnails
{
    static readonly ConcurrentDictionary<string, string> Known = new();

    public static async Task<Dictionary<long, string>> GetAsync(string type, IEnumerable<long> ids, string size = "150x150",
                                                              CancellationToken ct = default)
    {
        var result = new Dictionary<long, string>();
        var missing = new List<long>();
        foreach (var id in ids.Distinct())
        {
            if (Known.TryGetValue($"{type}:{size}:{id}", out var url)) result[id] = url;
            else missing.Add(id);
        }

        foreach (var chunk in missing.Chunk(100))   // Roblox takes up to 100 per request
        {
            var body = chunk.Select(id => new
            {
                requestId = $"{id}:{type}:{size}",
                type,
                targetId = id,
                size,
                format = "png",
                isCircular = false
            }).ToArray();
            try
            {
                using var doc = await RobloxHttp.PostJsonAsync("https://thumbnails.roblox.com/v1/batch", null, body, ct);
                foreach (var e in doc.RootElement.GetProperty("data").EnumerateArray())
                {
                    if (e.TryGetProperty("state", out var st) && st.GetString() != "Completed") continue;   // still rendering
                    if (!e.TryGetProperty("imageUrl", out var u) || u.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                    long id = e.GetProperty("targetId").GetInt64();
                    var url = u.GetString()!;
                    result[id] = url;
                    Known[$"{type}:{size}:{id}"] = url;
                }
            }
            catch (RobloxException) { }   // pictures are a nicety: the page still works without them
        }
        return result;
    }

    /// <summary>Forget an avatar's picture, e.g. after changing what it wears.</summary>
    public static void Forget(string type, long id)
    {
        foreach (var k in Known.Keys.Where(k => k.StartsWith(type + ":") && k.EndsWith(":" + id)).ToList())
            Known.TryRemove(k, out _);
    }
}
