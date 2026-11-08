using System.IO;

namespace RoDeck.Services;

/// <summary>
/// RoDeck used to be called AltDeck. On the first start under the new name, the old data folder
/// (%AppData%\AltDeck: accounts vault, settings, logs, sign-in backups) is copied to %AppData%\RoDeck, so nobody loses
/// their accounts. It's a copy, not a move: an old AltDeck.exe still works with its own folder.
/// The picture cache in %LocalAppData% isn't copied; it fills itself again.
/// </summary>
public static class DataMigration
{
    static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>Runs before anything reads settings or the vault. Never throws.</summary>
    public static void FromAltDeck() => FromAltDeck(AppData);

    /// <summary>The same, inside <paramref name="root"/> (tests use a temporary folder).</summary>
    public static void FromAltDeck(string root)
    {
        try
        {
            var oldDir = Path.Combine(root, "AltDeck");
            var newDir = Path.Combine(root, "RoDeck");
            if (!Directory.Exists(oldDir)) return;
            // already moved, or RoDeck has its own data: leave both alone
            if (File.Exists(Path.Combine(newDir, "settings.json")) || File.Exists(Path.Combine(newDir, "vault.dat"))) return;

            // copy into a temporary folder first and rename it at the end, so a half-finished copy is never used
            var tmp = newDir + ".moving";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            CopyDir(oldDir, tmp);
            if (Directory.Exists(newDir))
            {
                // an empty RoDeck folder from an earlier start (e.g. only logs): fold it in
                CopyDir(newDir, tmp, overwrite: false);
                Directory.Delete(newDir, true);
            }
            Directory.Move(tmp, newDir);
            File.WriteAllText(Path.Combine(newDir, "moved-from-AltDeck.txt"),
                $"Copied from {oldDir} on {DateTime.Now:yyyy-MM-dd HH:mm}. RoDeck is the new name of AltDeck.\r\n" +
                "The old folder was left as it was; you can delete it once you no longer use AltDeck.\r\n");
            Moved = true;
        }
        catch
        {
            // couldn't copy (file in use, no space…): RoDeck starts empty and the old folder is untouched,
            // so the next start tries again
        }
    }

    /// <summary>True when this start copied the old AltDeck data over (shown once in the activity log).</summary>
    public static bool Moved { get; private set; }

    static void CopyDir(string from, string to, bool overwrite = true)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
        {
            var target = Path.Combine(to, Path.GetFileName(f));
            if (overwrite || !File.Exists(target)) File.Copy(f, target, overwrite);
        }
        foreach (var d in Directory.GetDirectories(from))
            CopyDir(d, Path.Combine(to, Path.GetFileName(d)), overwrite);
    }
}
