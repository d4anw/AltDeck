namespace RoDeck.Models;

/// <summary>
/// One set of FPS &amp; FastFlags settings. There are two: "Playing" (pressing Play on Home, Games or Friends: you on
/// your own, so it can look good) and "Multi-launch" (launching from Multi-launch, launch presets and rejoins: many
/// accounts at once, so light). The one shown on the FPS &amp; FastFlags page lives in the flat AppSettings fields while
/// you edit it, and is copied back here before every save.
/// </summary>
public class PerfProfile
{
    public string Preset { get; set; } = "Roblox defaults";
    public PerfSnapshot Values { get; set; } = new();
    public PerfSnapshot? Custom { get; set; }
    public List<FastFlag> Flags { get; set; } = new();

    /// <summary>Playing on your own: Roblox as it is, nothing capped.</summary>
    public static PerfProfile PlayingDefault() => new()
    {
        Preset = "Roblox defaults",
        Values = new PerfSnapshot
        {
            FpsCap = AppSettings.FpsMax, LowestGraphics = false, LighterRendering = false, Msaa = "x4", TextureQuality = "High",
            RenderingMode = "Roblox default", GraySky = false, NoGrass = false, StartTiny = false, MinimizeOnLaunch = false,
            MuteOnStart = false, BackgroundCpuLimit = 0, AntiAfk = false,
        },
    };
}
