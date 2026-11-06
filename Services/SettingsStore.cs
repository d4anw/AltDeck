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
