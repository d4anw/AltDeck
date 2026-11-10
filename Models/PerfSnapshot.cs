namespace RoDeck.Models;

/// <summary>The user's own saved performance preset: every fine-tune value, stored in settings.json.</summary>
public class PerfSnapshot
{
    public int FpsCap { get; set; } = 30;
    public bool UnlimitedFps { get; set; }
    public bool LowestGraphics { get; set; }
    public bool LighterRendering { get; set; }
    public string Msaa { get; set; } = "x1";
    public string TextureQuality { get; set; } = "Low";
    public string RenderingMode { get; set; } = "Roblox default";
    public bool GraySky { get; set; }
    public bool NoGrass { get; set; }
    public bool StartTiny { get; set; }
    public bool MinimizeOnLaunch { get; set; }
    public bool MuteOnStart { get; set; }
    public int BackgroundCpuLimit { get; set; }
    public bool AntiAfk { get; set; }
}
