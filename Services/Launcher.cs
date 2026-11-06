using System.Diagnostics;
using System.Text.RegularExpressions;
using AltDeck.Models;

namespace AltDeck.Services;

/// <summary>
/// Where to play. A newer "roblox.com/share?code=...&amp;type=Server" link has no place ID yet:
/// it carries ShareCode/ShareType and is turned into a normal target by RobloxApi.ResolveShareLinkAsync.
/// </summary>
public record LaunchTarget(long PlaceId, string? JobId, string? PrivateCode, string? ShareCode = null, string? ShareType = null);

public static class TargetParser
{
    /// <summary>Accepts a place ID, a roblox.com/games/... link, or a link with a job ID / private server code.</summary>
    public static LaunchTarget? Parse(string input)
    {
        input = (input ?? "").Trim();

        // Newer share links: https://www.roblox.com/share?code=<hex>&type=Server
        var share = Regex.Match(input, @"roblox\.com/share\?(?:[^#\s]*&)?code=([0-9a-fA-F]+)", RegexOptions.IgnoreCase);
        if (share.Success)
        {
            var type = Regex.Match(input, @"[?&]type=(\w+)", RegexOptions.IgnoreCase);
            return new LaunchTarget(0, null, null, share.Groups[1].Value, type.Success ? type.Groups[1].Value : "Server");
        }

        long placeId;
        if (!long.TryParse(input, out placeId))
        {
            var m = Regex.Match(input, @"roblox\.com/(?:[a-z]{2}/)?games/(\d+)", RegexOptions.IgnoreCase);
            if (!m.Success) m = Regex.Match(input, @"placeId=(\d+)", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            placeId = long.Parse(m.Groups[1].Value);
        }
        var code = Regex.Match(input, @"privateServerLinkCode=([\w-]+)");
        var job = Regex.Match(input, @"(?:gameInstanceId|jobId)=([0-9a-fA-F-]{36})");
        return new LaunchTarget(placeId, job.Success ? job.Groups[1].Value : null,
                                code.Success ? code.Groups[1].Value : null);
    }
}

public sealed class Launcher
{
    Mutex? _mutex;
    EventWaitHandle? _event;

    public bool IsActive => _mutex != null;

    /// <summary>Lets go of the lock (used when multi-instance is switched off in Settings).</summary>
    public void Disable()
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        _event?.Dispose();
        _mutex = null;
        _event = null;
    }

    /// <summary>
    /// Takes Roblox's single-instance lock (mutex + event). Fails if any Roblox process,
    /// including a hidden background one, already holds it.
    /// </summary>
    public bool EnableMultiInstance()
    {
        if (_mutex != null) return true;

        var m = new Mutex(true, "ROBLOX_singletonMutex", out bool mutexNew);
        if (!mutexNew) { m.Dispose(); return false; }

        EventWaitHandle? ev = null;
        bool eventTaken = false;
        try
        {
            ev = new EventWaitHandle(false, EventResetMode.AutoReset, "ROBLOX_singletonEvent", out bool evNew);
            eventTaken = !evNew;
        }
        catch { ev = null; }

        if (eventTaken)
        {
            ev!.Dispose();
            try { m.ReleaseMutex(); } catch { }
            m.Dispose();
            return false;
        }

        _mutex = m;
        _event = ev;
        return true;
    }

    /// <summary>Ends every Roblox client process (including windowless background ones).</summary>
    public static int CloseAllRobloxClients()
    {
        int closed = 0;
        foreach (var p in Process.GetProcessesByName("RobloxPlayerBeta"))
        {
            try { p.Kill(); p.WaitForExit(3000); closed++; }
            catch { }
            finally { p.Dispose(); }
        }
        return closed;
    }

    /// <summary>
    /// Starts Roblox for this account. <paramref name="extraArgs"/> go after the launch link on the launcher's command line.
    /// Extra arguments need the real launcher command (read from the roblox-player: handler in Windows), because
    /// Windows can't pass anything but the link when it opens a link directly.
    /// </summary>
    public async Task LaunchAsync(Account a, LaunchTarget t, string? extraArgs = null)
    {
        var ticket = await RobloxApi.GetAuthTicketAsync(a.Cookie);
        var tracker = Random.Shared.NextInt64(100_000_000_000, 130_000_000_000);

        string request;
        if (t.PrivateCode != null)
        {
            // The access code is looked up with this account's own session. If Roblox's page doesn't give one,
            // still try with just the link code instead of giving up.
            var access = await RobloxApi.GetPrivateAccessCodeAsync(a.Cookie, t.PlaceId, t.PrivateCode);
            request = $"RequestPrivateGame&placeId={t.PlaceId}" +
                      (access != null ? $"&accessCode={access}" : "") +
                      $"&linkCode={t.PrivateCode}";
        }
        else if (t.JobId != null)
            request = $"RequestGameJob&placeId={t.PlaceId}&gameId={t.JobId}";
        else
            request = $"RequestGame&placeId={t.PlaceId}";

        var placeLauncher = "https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=" + request +
                            $"&browserTrackerId={tracker}&isPlayTogetherGame=false";
        var uri = $"roblox-player:1+launchmode:play+gameinfo:{ticket}" +
                  $"+launchtime:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}" +
                  $"+placelauncherurl:{Uri.EscapeDataString(placeLauncher)}" +
                  $"+browsertrackerid:{tracker}+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp";
        extraArgs = (extraArgs ?? "").Trim();
        if (extraArgs.Length == 0)
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return;
        }

        var (exe, template) = FindHandler();
        if (exe == null)
            throw new InvalidOperationException("Couldn't find the Roblox launcher to pass your launch arguments to. Clear the launch arguments or reinstall Roblox.");
        if (uri.Contains('"')) throw new InvalidOperationException("The launch link contained an unexpected character.");

        var quoted = "\"" + uri + "\"";
        var args = template.Contains("%1") ? template.Replace("%1", quoted) : (template + " " + quoted).Trim();
        Process.Start(new ProcessStartInfo(exe, args + " " + extraArgs) { UseShellExecute = false });
    }

    /// <summary>Reads what Windows runs for roblox-player: links (Roblox's launcher, or Bloxstrap and the like).</summary>
    static (string? Exe, string Template) FindHandler()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"roblox-player\shell\open\command");
            var cmd = (k?.GetValue("") as string)?.Trim();
            if (string.IsNullOrEmpty(cmd)) return (null, "");

            string exe, rest;
            if (cmd[0] == '"')
            {
                var end = cmd.IndexOf('"', 1);
                if (end < 0) return (null, "");
                exe = cmd.Substring(1, end - 1);
                rest = cmd.Substring(end + 1).Trim();
            }
            else
            {
                var sp = cmd.IndexOf(' ');
                exe = sp < 0 ? cmd : cmd.Substring(0, sp);
                rest = sp < 0 ? "" : cmd.Substring(sp + 1).Trim();
            }
            return File.Exists(exe) ? (exe, rest) : (null, "");
        }
        catch { return (null, ""); }
    }
}
