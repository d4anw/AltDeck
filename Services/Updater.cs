using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace AltDeck.Services;

/// <summary>A newer AltDeck.exe on GitHub. Sha256 is GitHub's own digest of the file (lowercase hex), when it gave one.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string Url, long Size, string? Sha256);

/// <summary>
/// Auto-update from GitHub Releases (the latest release of <see cref="Repo"/>, its asset named AltDeck.exe).
/// The new exe is downloaded next to the running one as AltDeck.exe.new, checked against its size and GitHub's SHA-256,
/// and swapped in when AltDeck closes: Windows can't overwrite a running exe but can rename it, so
/// AltDeck.exe becomes AltDeck.exe.old and AltDeck.exe.new becomes AltDeck.exe. The .old file is deleted at the next start.
/// Only the single-file AltDeck.exe updates itself; a developer build (dotnet run, bin\ folder) never does.
/// </summary>
public static class Updater
{
    public const string Repo = "d4anw/AltDeck";
    const string AssetName = "AltDeck.exe";
    const string AfterUpdateArg = "--after-update";

    /// <summary>This copy's version, from &lt;Version&gt; in AltDeck.csproj. Must come before Http, which uses it.</summary>
    public static Version Current { get; } = Normalize(ParseVersion(
        (Assembly.GetEntryAssembly() ?? typeof(Updater).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion) ?? new Version(0, 0, 0));

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("AltDeck/" + Current);   // GitHub's API refuses requests without one
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    static string? ExePath => Environment.ProcessPath;

    /// <summary>True for the published single-file AltDeck.exe. A framework-dependent build has AltDeck.dll next to it.</summary>
    public static bool CanUpdate =>
        ExePath is { } p
        && string.Equals(Path.GetFileName(p), AssetName, StringComparison.OrdinalIgnoreCase)
        && !File.Exists(Path.Combine(Path.GetDirectoryName(p)!, "AltDeck.dll"));

    /// <summary>The downloaded update waiting to be installed, if any.</summary>
    public static UpdateInfo? Staged { get; private set; }

    /// <summary>At start: removes the previous version and leftovers of an unfinished download.</summary>
    public static void CleanUp()
    {
        if (ExePath is not { } exe) return;
        foreach (var leftover in new[] { exe + ".old", exe + ".new", exe + ".download" })
            try { if (File.Exists(leftover)) File.Delete(leftover); } catch { }
    }

    /// <summary>A copy started by <see cref="Restart"/> waits for the old one to close, so the single-instance check lets it in.</summary>
    public static void WaitForPreviousCopy(string[] args)
    {
        int i = Array.IndexOf(args, AfterUpdateArg);
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out int pid)) return;
        try { using var p = Process.GetProcessById(pid); p.WaitForExit(20_000); } catch { }   // already gone: fine
    }

    /// <summary>Asks GitHub for the latest release. Null when it isn't newer than <paramref name="current"/> or has no AltDeck.exe.</summary>
    public static async Task<UpdateInfo?> CheckAsync(Version? current = null, CancellationToken ct = default)
    {
        using var r = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        if (r.StatusCode == System.Net.HttpStatusCode.NotFound) return null;   // no release yet
        if ((int)r.StatusCode == 403 || (int)r.StatusCode == 429) throw new IOException("GitHub is limiting checks right now. AltDeck tries again later.");
        r.EnsureSuccessStatusCode();

        var json = JsonNode.Parse(await r.Content.ReadAsStringAsync(ct));
        if (json == null || (bool?)json["draft"] == true || (bool?)json["prerelease"] == true) return null;

        var tag = (string?)json["tag_name"] ?? "";
        var version = ParseVersion(tag);
        if (version == null || Normalize(version) <= (current ?? Current)) return null;

        foreach (var asset in json["assets"]?.AsArray() ?? new JsonArray())
        {
            if (!string.Equals((string?)asset?["name"], AssetName, StringComparison.OrdinalIgnoreCase)) continue;
            var url = (string?)asset!["browser_download_url"];
            if (string.IsNullOrEmpty(url)) return null;
            var digest = (string?)asset["digest"];   // "sha256:<hex>"
            string? sha = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? digest[7..].ToLowerInvariant() : null;
            return new UpdateInfo(Normalize(version), tag, url, (long?)asset["size"] ?? 0, sha);
        }
        return null;
    }

    /// <summary>The release notes of one version (its GitHub release text), or null when there are none.</summary>
    public static async Task<string?> GetReleaseNotesAsync(Version v, CancellationToken ct = default)
    {
        using var r = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/tags/v{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}", ct);
        if (!r.IsSuccessStatusCode) return null;
        var body = (string?)JsonNode.Parse(await r.Content.ReadAsStringAsync(ct))?["body"];
        return string.IsNullOrWhiteSpace(body) ? null : body;
    }

    /// <summary>Downloads the update next to the running exe (or to <paramref name="targetExe"/>.new) and checks it.
    /// <paramref name="progress"/> gets (bytes so far, total bytes; 0 when unknown) about ten times a second.</summary>
    public static async Task DownloadAsync(UpdateInfo u, string? targetExe = null, CancellationToken ct = default,
                                           IProgress<(long Done, long Total)>? progress = null)
    {
        var exe = targetExe ?? ExePath ?? throw new InvalidOperationException("AltDeck can't tell where its exe is.");
        var part = exe + ".download";
        var staged = exe + ".new";

        try
        {
            using (var r = await Http.GetAsync(u.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                r.EnsureSuccessStatusCode();
                long size = u.Size > 0 ? u.Size : r.Content.Headers.ContentLength ?? 0;
                var lastReport = DateTime.MinValue;
                progress?.Report((0, size));
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using var src = await r.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long total = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    hash.AppendData(buffer, 0, n);
                    total += n;
                    if (progress != null && DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(100))
                    {
                        lastReport = DateTime.UtcNow;
                        progress.Report((total, size));
                    }
                }
                progress?.Report((total, size));

                if (u.Size > 0 && total != u.Size)
                    throw new IOException("The download was incomplete.");
                var got = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (u.Sha256 != null && got != u.Sha256)
                    throw new IOException("The download didn't match GitHub's checksum.");
            }
            File.Move(part, staged, overwrite: true);
            Staged = u;
        }
        catch
        {
            try { File.Delete(part); } catch { }
            throw;
        }
    }

    /// <summary>Swaps the downloaded exe in. Safe to call while AltDeck runs; the new version starts next time.</summary>
    public static void Install(string? targetExe = null)
    {
        var exe = targetExe ?? ExePath ?? throw new InvalidOperationException("AltDeck can't tell where its exe is.");
        var staged = exe + ".new";
        var old = exe + ".old";
        if (!File.Exists(staged)) throw new FileNotFoundException("No downloaded update.");

        File.Move(exe, old, overwrite: true);
        try { File.Move(staged, exe); }
        catch
        {
            File.Move(old, exe);   // put the working version back
            throw;
        }
        Staged = null;
    }

    /// <summary>Starts the (new) AltDeck.exe, which waits until this copy has closed.</summary>
    public static void Restart()
    {
        if (ExePath is not { } exe) return;
        Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            Arguments = $"{AfterUpdateArg} {Environment.ProcessId}",
            WorkingDirectory = Path.GetDirectoryName(exe)!
        });
    }

    static Version? ParseVersion(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimStart('v', 'V');
        int cut = s.IndexOfAny(new[] { '+', '-', ' ' });
        if (cut >= 0) s = s[..cut];
        return Version.TryParse(s, out var v) ? v : null;
    }

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
