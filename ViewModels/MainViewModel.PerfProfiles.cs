using RoDeck.Models;
using RoDeck.Services;

namespace RoDeck.ViewModels;

// FPS & FastFlags has two profiles: "Playing" for Play on Home, Games and Friends (one account, good looking) and
// "Multi-launch" for Multi-launch, launch presets and rejoins (many accounts, light). The page edits one at a time;
// that one lives in the flat Settings fields (so every existing binding keeps working) and the other waits in its
// PerfProfile. Each launch, and each running client, uses its own profile.
public partial class MainViewModel
{
    private bool _switchingPerf;

    public const string PlayingProfile = "Playing";
    public const string AltsProfile = "Multi-launch";

    private bool EditingPlaying => Settings.PerfEditing == PlayingProfile;

    /// <summary>Called once from the constructor, before settings changes are listened to.</summary>
    private void SetupPerfProfiles()
    {
        var s = Settings;
        if (s.PerfEditing is not (PlayingProfile or AltsProfile)) s.PerfEditing = AltsProfile;
        // before profiles existed every setting was the Multi-launch one
        if (s.AltsPerf == null && s.PlayPerf == null) s.PerfEditing = AltsProfile;
        s.AltsPerf ??= new PerfProfile();
        s.PlayPerf ??= PerfProfile.PlayingDefault();
        StorePerf(EditingProfile);   // the flat fields are the profile being edited
    }

    private PerfProfile EditingProfile => EditingPlaying ? Settings.PlayPerf! : Settings.AltsPerf!;

    public string PerfEditingInfo => EditingPlaying
        ? "Used when you press Play on Home, Games or Friends: just you, so it can look its best."
        : "Used by Multi-launch, launch presets and auto-rejoin: many accounts at once, so keep it light.";

    private static PerfSnapshot Snap(AppSettings s) => new()
    {
        FpsCap = s.FpsCap, UnlimitedFps = s.UnlimitedFps, LowestGraphics = s.LowestGraphics, LighterRendering = s.LighterRendering,
        Msaa = s.Msaa, TextureQuality = s.TextureQuality, RenderingMode = s.RenderingMode,
        GraySky = s.GraySky, NoGrass = s.NoGrass, StartTiny = s.StartTiny, MinimizeOnLaunch = s.MinimizeOnLaunch,
        MuteOnStart = s.MuteOnStart, BackgroundCpuLimit = s.BackgroundCpuLimit, AntiAfk = s.AntiAfk,
    };

    private static void Put(AppSettings s, PerfSnapshot c)
    {
        s.FpsCap = c.FpsCap; s.UnlimitedFps = c.UnlimitedFps;
        s.LowestGraphics = c.LowestGraphics; s.LighterRendering = c.LighterRendering;
        s.Msaa = c.Msaa; s.TextureQuality = c.TextureQuality; s.RenderingMode = c.RenderingMode;
        s.GraySky = c.GraySky; s.NoGrass = c.NoGrass;
        s.StartTiny = c.StartTiny; s.MinimizeOnLaunch = c.MinimizeOnLaunch;
        s.MuteOnStart = c.MuteOnStart; s.BackgroundCpuLimit = c.BackgroundCpuLimit; s.AntiAfk = c.AntiAfk;
    }

    /// <summary>The page's values into a profile.</summary>
    private void StorePerf(PerfProfile p)
    {
        p.Preset = Settings.PerfPreset;
        p.Values = Snap(Settings);
        p.Custom = Settings.CustomPerf;
        p.Flags = Settings.CustomFlags.ToList();
    }

    /// <summary>A profile onto the page.</summary>
    private void LoadPerf(PerfProfile p)
    {
        bool was = _applyingPreset;
        _applyingPreset = true;
        try
        {
            Settings.PerfPreset = p.Preset;
            Put(Settings, p.Values);
            Settings.CustomPerf = p.Custom;
            foreach (var f in Settings.CustomFlags) f.PropertyChanged -= OnFlagChanged;
            Settings.CustomFlags.Clear();
            foreach (var f in p.Flags) Settings.CustomFlags.Add(f);
        }
        finally { _applyingPreset = was; }
    }

    /// <summary>The page switched profile: keep the one you left, show the other.</summary>
    private void SwitchPerfProfile()
    {
        if (_switchingPerf) return;
        _switchingPerf = true;
        try
        {
            StorePerf(EditingPlaying ? Settings.AltsPerf! : Settings.PlayPerf!);
            LoadPerf(EditingProfile);
        }
        finally { _switchingPerf = false; }
        OnPropertyChanged(nameof(PerfEditingInfo));
    }

    /// <summary>Everything saved to disk: the profile on the page is copied into its slot first.</summary>
    private void SaveSettingsToDisk()
    {
        StorePerf(EditingProfile);
        _store.Save(Settings);
    }

    /// <summary>
    /// The settings a launch (or a running client) uses: "Playing" for a game you started with Play, "Multi-launch" for
    /// everything else. The profile on the page is used live; the other is built from what it saved.
    /// </summary>
    private AppSettings PerfSettings(bool playing)
    {
        if (playing == EditingPlaying) return Settings;
        var p = playing ? Settings.PlayPerf! : Settings.AltsPerf!;
        var s = new AppSettings
        {
            WindowLayout = Settings.WindowLayout,
            DetailedView = Settings.DetailedView,
            PerfPreset = p.Preset,
        };
        Put(s, p.Values);
        s.CustomFlags = new(p.Flags);
        return s;
    }

    /// <summary>A running client: one you started with Play is "Playing", a farming / multi-launched one isn't.</summary>
    private AppSettings PerfSettings(ClientInfo c) => PerfSettings(!c.Farm);
}
