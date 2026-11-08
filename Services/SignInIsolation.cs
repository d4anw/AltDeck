using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoDeck.Services;

/// <summary>
/// Every Roblox client on this PC shares one sign-in store: %LocalAppData%\Roblox\LocalStorage\RobloxCookies.dat
/// (the session cookie) and the account-switcher entries in appStorage.json. A new client loads whatever the
/// previous client left there before it signs in with its own ticket, and with 6 clients one session ended up
/// signed out within seconds of launching (seen in the logs as 401 Unauthorized on every request, so every
/// teleport fails with Error 769 and the game's own anti-AFK rejoin stops working).
///
/// So right before each launch the store is emptied: the new client starts with nothing of the other accounts.
/// Running clients keep their sign-in in memory, so they aren't affected. The user's own Roblox sign-in
/// (the normal Roblox app) is backed up first and put back ~90 s after the last launch, when RoDeck closes,
/// and at the next start after a crash.
/// </summary>
public static class SignInIsolation
{
    static readonly string Store = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "LocalStorage");
    static string CookieFile => Path.Combine(Store, "RobloxCookies.dat");
    static string AppStorage => Path.Combine(Store, "appStorage.json");

    static string BackupDir => Path.Combine(SettingsStore.DataDir, "roblox-signin-backup");
    static string CookieBackup => Path.Combine(BackupDir, "RobloxCookies.dat");
    static string KeysBackup => Path.Combine(BackupDir, "account-keys.json");
    static string NoCookieMarker => Path.Combine(BackupDir, "no-cookie-file");

    /// <summary>The account-switcher entries in appStorage.json (the rest of that file is app settings and is left alone).</summary>
    static readonly string[] AccountKeys = { "AccountBlob", "PreviousAccountsList", "AccountSwitcherMetadataMap", "UserId", "Username" };

    const int RestoreAfterSeconds = 90;
    static readonly object Gate = new();
    static readonly Timer RestoreTimer = new(_ => Restore(), null, Timeout.Infinite, Timeout.Infinite);

    /// <summary>Call right before each launch (inside the launch gate, so launches never overlap).</summary>
    public static void PrepareLaunch()
    {
        lock (Gate)
        {
            RestoreTimer.Change(Timeout.Infinite, Timeout.Infinite);
            BackupOnce();
            try { if (File.Exists(CookieFile)) File.Delete(CookieFile); } catch { }
            EditAccountKeys(o => { foreach (var k in AccountKeys) o.Remove(k); });
        }
    }

    /// <summary>Call after each launch: the user's own sign-in comes back once no launch has happened for a while.</summary>
    public static void LaunchStarted() =>
        RestoreTimer.Change(TimeSpan.FromSeconds(RestoreAfterSeconds), Timeout.InfiniteTimeSpan);

    /// <summary>Puts the user's own Roblox sign-in back (no-op when nothing was backed up).</summary>
    public static void Restore()
    {
        lock (Gate)
        {
            if (!Directory.Exists(BackupDir)) return;
            try
            {
                if (File.Exists(CookieBackup)) File.Copy(CookieBackup, CookieFile, overwrite: true);
                else if (File.Exists(NoCookieMarker) && File.Exists(CookieFile)) File.Delete(CookieFile);

                if (File.Exists(KeysBackup))
                {
                    var saved = JsonNode.Parse(File.ReadAllText(KeysBackup)) as JsonObject;
                    if (saved != null)
                        EditAccountKeys(o =>
                        {
                            foreach (var k in AccountKeys) o.Remove(k);
                            foreach (var kv in saved) o[kv.Key] = kv.Value?.DeepClone();
                        });
                }
                Directory.Delete(BackupDir, recursive: true);
            }
            catch { }   // a file in use: the backup stays and is tried again next time
        }
    }

    static void BackupOnce()
    {
        if (Directory.Exists(BackupDir)) return;   // already holds the user's own sign-in: never overwrite it with an alt's
        try
        {
            Directory.CreateDirectory(BackupDir);
            if (File.Exists(CookieFile)) File.Copy(CookieFile, CookieBackup, overwrite: true);
            else File.WriteAllText(NoCookieMarker, "");

            var keys = new JsonObject();
            if (ReadAppStorage() is JsonObject o)
                foreach (var k in AccountKeys)
                    if (o[k] is JsonNode v) keys[k] = v.DeepClone();
            File.WriteAllText(KeysBackup, keys.ToJsonString());
        }
        catch { }
    }

    static JsonObject? ReadAppStorage()
    {
        try
        {
            if (!File.Exists(AppStorage)) return null;
            using var fs = new FileStream(AppStorage, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return JsonNode.Parse(sr.ReadToEnd()) as JsonObject;
        }
        catch { return null; }
    }

    static void EditAccountKeys(Action<JsonObject> edit)
    {
        var o = ReadAppStorage();
        if (o == null) return;   // missing, or caught half-written by a client: leave it
        edit(o);
        try
        {
            var tmp = AppStorage + ".rodeck.tmp";
            File.WriteAllText(tmp, o.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
            File.Move(tmp, AppStorage, overwrite: true);
        }
        catch { }
    }
}
