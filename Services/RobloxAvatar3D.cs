using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace AltDeck.Services;

/// <summary>A 3D avatar ready to show: the model (frozen, usable on any thread), its centre and size, and the
/// Roblox link it came from (to notice when a newer one exists).</summary>
public sealed record Avatar3D(Model3DGroup Model, Point3D Center, double Size, string SourceUrl);

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
        for (int i = 0; i < 20; i++)
        {
            url = await GetMetaUrlAsync(cookie, userId, ct);
            if (url.Length > 0 && url != previous) break;
            await Task.Delay(750, ct);
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

        var objTask = GetTextAsync(objHash, ct);
        var mtlTask = GetTextAsync(mtlHash, ct);
        var objText = await objTask;
        var mtlText = await mtlTask;

        // materials: colour and texture per name; textures are fetched in parallel
        var materials = ParseMtl(mtlText);
        var textureNames = materials.Values.Select(m => m.Texture).Where(t => t != null).Distinct().ToList();
        var textures = new Dictionary<string, BitmapImage>();
        await Task.WhenAll(textureNames.Select(async name =>
        {
            try
            {
                var bytes = await GetBytesAsync(name!, ct);
                var img = new BitmapImage();
                using (var ms = new MemoryStream(bytes)) { img.BeginInit(); img.CacheOption = BitmapCacheOption.OnLoad; img.StreamSource = ms; img.EndInit(); }
                img.Freeze();
                lock (textures) textures[name!] = img;
            }
            catch { }   // a missing texture just leaves that part in its plain colour
        }));

        return await Task.Run(() => BuildModel(objText, materials, textures, metaUrl), ct);
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
        }

        var b = model.Bounds;
        var center = new Point3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2);
        double size = Math.Max(b.SizeX, Math.Max(b.SizeY, b.SizeZ));
        model.Freeze();
        return new Avatar3D(model, center, size, source);

        static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
        static int Idx(string s, int count) { int i = int.Parse(s, CultureInfo.InvariantCulture); return i < 0 ? count + i : i - 1; }
    }

    // ------------------------------------------------------------------ Roblox's picture servers

    static Task<string> GetTextAsync(string hash, CancellationToken ct) => GetAsync(hash, ct, (c, u) => c.GetStringAsync(u, ct));
    static Task<byte[]> GetBytesAsync(string hash, CancellationToken ct) => GetAsync(hash, ct, (c, u) => c.GetByteArrayAsync(u, ct));

    /// <summary>A file by its hash: tN.rbxcdn.com (N worked out from the hash, the classic way), else tr.rbxcdn.com.</summary>
    static async Task<T> GetAsync<T>(string hash, CancellationToken ct, Func<HttpClient, string, Task<T>> get)
    {
        if (hash.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return await get(RobloxApi.Http, hash);
        int i = 31;
        foreach (var c in hash) i ^= c;
        try { return await get(RobloxApi.Http, $"https://t{i % 8}.rbxcdn.com/{hash}"); }
        catch (HttpRequestException) { return await get(RobloxApi.Http, $"https://tr.rbxcdn.com/{hash}"); }
    }
}
