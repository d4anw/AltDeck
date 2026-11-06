using System.Text.Json;
using AltDeck.Models;

namespace AltDeck.Services;

/// <summary>App settings (no secrets) as plain JSON in %AppData%\AltDeck\settings.json.</summary>
public sealed class SettingsStore
{
    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AltDeck");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");
    public static string DataDir => Dir;

    /// <summary>Whether settings.json was there when AltDeck started, i.e. this isn't a fresh install.
    /// Read before anything is saved (the class is first touched when App loads the settings).</summary>
    public static readonly bool ExistedAtStart = File.Exists(FilePath);

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
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
