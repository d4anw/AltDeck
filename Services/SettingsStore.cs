using System.Text.Json;
using RoDeck.Models;

namespace RoDeck.Services;

/// <summary>App settings (no secrets) as plain JSON in %AppData%\RoDeck\settings.json.</summary>
public sealed class SettingsStore
{
    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RoDeck");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");
    public static string DataDir => Dir;

    /// <summary>Whether settings.json was there when RoDeck started, i.e. this isn't a fresh install.
    /// Read before anything is saved (the class is first touched when App loads the settings).</summary>
    public static readonly bool ExistedAtStart = File.Exists(FilePath);

    public AppSettings Load()
    {
        // the file, or (if it's damaged, e.g. the PC lost power while saving) the copy from the save before
        foreach (var path in new[] { FilePath, FilePath + ".bak" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                if (s == null) continue;
                if (!s.TrayDefaultApplied) { s.CloseToTray = true; s.TrayDefaultApplied = true; }   // Close to tray is now the default
                return s;
            }
            catch { }
        }
        return new AppSettings { TrayDefaultApplied = true };
    }

    public void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            // written next to it first and then swapped in, so a crash halfway never leaves a broken settings file;
            // the previous version stays as settings.json.bak
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, FilePath + ".bak", ignoreMetadataErrors: true);
            else File.Move(tmp, FilePath);
        }
        catch { }
    }
}
