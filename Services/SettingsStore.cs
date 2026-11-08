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
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
                if (!s.TrayDefaultApplied) { s.CloseToTray = true; s.TrayDefaultApplied = true; }   // Close to tray is now the default
                return s;
            }
        }
        catch { }
        return new AppSettings { TrayDefaultApplied = true };
    }

    public void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
