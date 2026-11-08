using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace RoDeck.Services;

/// <summary>A 3D avatar ready to show: the model (frozen, usable on any thread), its centre and size, and the
/// Roblox link it came from (to notice when a newer one exists).</summary>
public sealed record Avatar3D(Model3DGroup Model, Point3D Center, double Size, string SourceUrl,
                              IReadOnlyList<string?>? PartTextures = null)
{
    /// <summary>The texture of each piece of the model (same order as Model.Children); null for untextured pieces.
    /// Every accessory has its own texture, which is how the piece of one accessory is found.</summary>
    public IReadOnlyList<string?> Textures => PartTextures ?? Array.Empty<string?>();
}

/// <summary>Some of the model's textures couldn't be downloaded (yet): showing it would give a grey avatar.</summary>
public sealed class MissingTexturesException : Exception
{
    public MissingTexturesException(int missing, int total) : base($"{missing} of {total} textures didn't download") { }
}

/// <summary>
/// Roblox's 3D avatar (thumbnails.roblox.com/v1/users/avatar-3d, needs a sign-in): a small JSON with the hashes of an
/// OBJ model, its MTL materials and PNG textures on Roblox's picture servers. This downloads and builds a WPF model.
/// </summary>
public static class RobloxAvatar3D
{
    /// <summary>The link to the current 3D avatar's description, "" while Roblox is still making it.</summary>
    public static async Task<string> GetMetaUrlAsync(string cookie, long userId, CancellationToken ct)
    {
        using var doc = await RobloxHttp.GetJsonAsync($"https://thumbnails.roblox.com/v1/users/avatar-3d?userId={userId}", cookie, ct);
        var root = doc.RootElement;
        var state = root.TryGetProperty("state", out var s) ? s.GetString() : "";
        return state == "Completed" && root.TryGetProperty("imageUrl", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() ?? "" : "";
    }

    /// <summary>Waits (up to ~15 s) for a 3D avatar newer than <paramref name="previous"/>, then loads it.</summary>
    public static async Task<Avatar3D?> LoadAsync(string cookie, long userId, string? previous, CancellationToken ct)
    {
        string url = "";
        for (int i = 0; i < 20; i++)   // ~20 s, once a second: this service is strict about how often it's asked
        {
            url = await GetMetaUrlAsync(cookie, userId, ct);
            if (url.Length > 0 && url != previous) break;
            await Task.Delay(1000, ct);
        }
        if (url.Length == 0) return null;
        return await BuildAsync(url, ct);
    }

    static async Task<Avatar3D> BuildAsync(string metaUrl, CancellationToken ct)
    {
        var http = RobloxApi.Http;
        using var meta = JsonDocument.Parse(await http.GetStringAsync(metaUrl, ct));
        var root = meta.RootElement;
        string objHash = root.GetProperty("obj").GetString()!;
        string mtlHash = root.GetProperty("mtl").GetString()!;

        var objTask = CachedTextAsync(objHash, ct);
        var mtlTask = CachedTextAsync(mtlHash, ct);
        var objText = await objTask;
        var mtlText = await mtlTask;

        // materials: colour and texture per name; textures are fetched in parallel
        var materials = ParseMtl(mtlText);
        var textureNames = materials.Values.Select(m => m.Texture).Where(t => t != null).Distinct().ToList();
        var textures = new Dictionary<string, BitmapImage>();
        int failed = 0;
        await Task.WhenAll(textureNames.Select(async name =>
        {
            try
            {
                if (!TextureCache.TryGetValue(name!, out var img))
                {
                    var bytes = await GetBytesAsync(name!, ct);
                    img = new BitmapImage();
                    using (var ms = new MemoryStream(bytes)) { img.BeginInit(); img.CacheOption = BitmapCacheOption.OnLoad; img.StreamSource = ms; img.EndInit(); }
                    img.Freeze();
                    if (TextureCache.Count > 80) TextureCache.Clear();
                    TextureCache[name!] = img;
                }
                lock (textures) textures[name!] = img;
            }
            catch { Interlocked.Increment(ref failed); }
        }));
        if (failed > 0) throw new MissingTexturesException(failed, textureNames.Count);

        return await Task.Run(() => BuildModel(objText, materials, textures, metaUrl), ct);
    }

    // Files are named by their content (a hash), so a remembered one is always the right one: skin, face and clothes that
    // didn't change aren't downloaded again after the next change.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> TextCache = new();
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, BitmapImage> TextureCache = new();

    static async Task<string> CachedTextAsync(string hash, CancellationToken ct)
    {
        if (TextCache.TryGetValue(hash, out var known)) return known;
        var text = await GetTextAsync(hash, ct);
        if (TextCache.Count > 20) TextCache.Clear();
        TextCache[hash] = text;
        return text;
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<long, HashSet<string>> AssetTextures = new();

    /// <summary>
    /// The texture names of one item's own 3D model (thumbnails.roblox.com/v1/assets-thumbnail-3d, needs a sign-in).
    /// The same textures appear on the avatar's model, so they tell which piece of the avatar is this item.
    /// Empty when Roblox has no 3D model for it.
    /// </summary>
    public static async Task<HashSet<string>> GetAssetTexturesAsync(string cookie, long assetId, CancellationToken ct)
    {
        if (AssetTextures.TryGetValue(assetId, out var known)) return known;
        string url = "";
        for (int i = 0; i < 8 && url.Length == 0; i++)
        {
            using var doc = await RobloxHttp.GetJsonAsync($"https://thumbnails.roblox.com/v1/assets-thumbnail-3d?assetId={assetId}", cookie, ct);
            var root = doc.RootElement;
            if (root.TryGetProperty("state", out var st) && st.GetString() == "Completed"
                && root.TryGetProperty("imageUrl", out var u) && u.ValueKind == JsonValueKind.String) url = u.GetString() ?? "";
            else await Task.Delay(1000, ct);
        }
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (url.Length > 0)
        {
            using var meta = JsonDocument.Parse(await RobloxApi.Http.GetStringAsync(url, ct));
            var r = meta.RootElement;
            if (r.TryGetProperty("textures", out var tx) && tx.ValueKind == JsonValueKind.Array)
                foreach (var t in tx.EnumerateArray()) if (t.GetString() is { Length: > 0 } name) set.Add(name);
            if (r.TryGetProperty("mtl", out var mtl) && mtl.GetString() is { Length: > 0 } mtlHash)
                foreach (var m in ParseMtl(await CachedTextAsync(mtlHash, ct)).Values)
                    if (m.Texture != null) set.Add(m.Texture);
        }
        AssetTextures[assetId] = set;
        return set;
    }

    // ------------------------------------------------------------------ OBJ / MTL

    sealed class Mtl { public Color Diffuse = Colors.LightGray; public string? Texture; public double Opacity = 1; }

    static Dictionary<string, Mtl> ParseMtl(string text)
    {
        var result = new Dictionary<string, Mtl>();
        Mtl? cur = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("newmtl ")) result[line[7..].Trim()] = cur = new Mtl();
            else if (cur == null) continue;
            else if (line.StartsWith("Kd "))
            {
                var p = line[3..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 3)
                    cur.Diffuse = Color.FromRgb(ToByte(p[0]), ToByte(p[1]), ToByte(p[2]));
            }
            else if (line.StartsWith("map_Kd ")) cur.Texture = line[7..].Trim();
            else if (line.StartsWith("d ") && double.TryParse(line[2..], NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) cur.Opacity = d;
        }
        return result;

        static byte ToByte(string s) => (byte)Math.Clamp(Math.Round(double.Parse(s, CultureInfo.InvariantCulture) * 255), 0, 255);
    }

    static Avatar3D BuildModel(string obj, Dictionary<string, Mtl> mats, Dictionary<string, BitmapImage> textures, string source)
    {
        var v = new List<Point3D>(); var vt = new List<Point>(); var vn = new List<Vector3D>();
        var groups = new Dictionary<string, (MeshGeometry3D Mesh, Dictionary<(int, int, int), int> Index)>();
        string mat = "";
        (MeshGeometry3D Mesh, Dictionary<(int, int, int), int> Index) Group()
        {
            if (!groups.TryGetValue(mat, out var g)) groups[mat] = g = (new MeshGeometry3D(), new Dictionary<(int, int, int), int>());
            return g;
        }

        foreach (var raw in obj.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 2) continue;
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (p[0])
            {
                case "v": v.Add(new Point3D(D(p[1]), D(p[2]), D(p[3]))); break;
                case "vt": vt.Add(new Point(D(p[1]), 1 - D(p[2]))); break;   // OBJ's V goes up, WPF's goes down
                case "vn": vn.Add(new Vector3D(D(p[1]), D(p[2]), D(p[3]))); break;
                case "usemtl": mat = p.Length > 1 ? p[1] : ""; break;
                case "f":
                {
                    var g = Group();
                    var corners = new List<int>();
                    for (int i = 1; i < p.Length; i++)
                    {
                        var parts = p[i].Split('/');
                        int pi = Idx(parts[0], v.Count);
                        int ti = parts.Length > 1 && parts[1].Length > 0 ? Idx(parts[1], vt.Count) : -1;
                        int ni = parts.Length > 2 && parts[2].Length > 0 ? Idx(parts[2], vn.Count) : -1;
                        var key = (pi, ti, ni);
                        if (!g.Index.TryGetValue(key, out int at))
                        {
                            at = g.Mesh.Positions.Count;
                            g.Mesh.Positions.Add(v[pi]);
                            if (ti >= 0) g.Mesh.TextureCoordinates.Add(vt[ti]); else g.Mesh.TextureCoordinates.Add(new Point());
                            if (ni >= 0) g.Mesh.Normals.Add(vn[ni]);
                            g.Index[key] = at;
                        }
                        corners.Add(at);
                    }
                    for (int i = 1; i + 1 < corners.Count; i++)   // fan: works for triangles and quads
                    {
                        g.Mesh.TriangleIndices.Add(corners[0]);
                        g.Mesh.TriangleIndices.Add(corners[i]);
                        g.Mesh.TriangleIndices.Add(corners[i + 1]);
                    }
                    break;
                }
            }
        }

        var model = new Model3DGroup();
        var partTextures = new List<string?>();
        foreach (var (name, g) in groups)
        {
            if (g.Mesh.TriangleIndices.Count == 0) continue;
            if (g.Mesh.Normals.Count != g.Mesh.Positions.Count) g.Mesh.Normals.Clear();   // let WPF work them out
            mats.TryGetValue(name, out var m);
            Brush brush;
            if (m?.Texture != null && textures.TryGetValue(m.Texture, out var tex))
                brush = new ImageBrush(tex) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1), TileMode = TileMode.Tile, Opacity = m.Opacity };
            else
                brush = new SolidColorBrush(m?.Diffuse ?? Colors.LightGray) { Opacity = m?.Opacity ?? 1 };
            brush.Freeze();
            var material = new DiffuseMaterial(brush);
            material.Freeze();
            g.Mesh.Freeze();
            var part = new GeometryModel3D(g.Mesh, material) { BackMaterial = material };
            part.Freeze();
            model.Children.Add(part);
            partTextures.Add(m?.Texture);
        }

        var b = model.Bounds;
        var center = new Point3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2);
        double size = Math.Max(b.SizeX, Math.Max(b.SizeY, b.SizeZ));
        model.Freeze();
        return new Avatar3D(model, center, size, source, partTextures);

        static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
        static int Idx(string s, int count) { int i = int.Parse(s, CultureInfo.InvariantCulture); return i < 0 ? count + i : i - 1; }
    }

    // ------------------------------------------------------------------ Roblox's picture servers

    static Task<string> GetTextAsync(string hash, CancellationToken ct) => GetAsync(hash, ct, (c, u) => c.GetStringAsync(u, ct));
    static Task<byte[]> GetBytesAsync(string hash, CancellationToken ct) => GetAsync(hash, ct, (c, u) => c.GetByteArrayAsync(u, ct));

    /// <summary>
    /// A file by its hash. Roblox's picture servers: tN.rbxcdn.com (N worked out from the hash, the classic way) and
    /// tr.rbxcdn.com. Right after a change a file can take a moment to appear, so each is tried a few times.
    /// </summary>
    static async Task<T> GetAsync<T>(string hash, CancellationToken ct, Func<HttpClient, string, Task<T>> get)
    {
        var urls = new List<string>();
        if (hash.StartsWith("http", StringComparison.OrdinalIgnoreCase)) urls.Add(hash);
        else
        {
            int i = 31;
            foreach (var c in hash) i ^= c;
            urls.Add($"https://t{i % 8}.rbxcdn.com/{hash}");
            urls.Add($"https://tr.rbxcdn.com/{hash}");
        }

        Exception? last = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            foreach (var url in urls)
            {
                try { return await get(RobloxApi.Http, url); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { last = ex; }
            }
            await Task.Delay(400 * (attempt + 1), ct);
        }
        throw last ?? new HttpRequestException("download failed");
    }
}
