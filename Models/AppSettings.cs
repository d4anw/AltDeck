using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RoDeck.Models;

public class Favourite
{
    public string Name { get; set; } = "";
    public string Target { get; set; } = "";
}

public partial class AppSettings : ObservableObject
{
    // General
    [ObservableProperty] private string theme = "Dark";
    [ObservableProperty] private string accent = "#FF8A1F";
    [ObservableProperty] private string backgroundImage = "";
    [ObservableProperty] private string density = "Detailed";
    [ObservableProperty] private bool showOnDiscord = true;
    [ObservableProperty] private bool autoUpdate = true;
    [ObservableProperty] private string lastSeenVersion = "";
    [ObservableProperty] private string currentAccountId = "";   // the account Home, Games, Avatar, Friends… show (top right)   // to show "What's new" once after an update
    [ObservableProperty] private string discordAppId = "";   // not used any more (RoDeck has its own built-in app); kept so older settings.json files load as before
    [ObservableProperty] private bool closeToTray = true;      // X hides RoDeck to the tray (on by default)
    [ObservableProperty] private bool trayDefaultApplied;     // set once: older settings.json files get Close to tray turned on once
    [ObservableProperty] private bool trayTipShown;           // the "still running in the tray" note was shown (it shows only once ever)
    [ObservableProperty] private bool skipSplash;
    [ObservableProperty] private string windowPlacement = "";   // "left,top,width,height,maximized" of RoDeck's window
    [ObservableProperty] private bool detailedView = true;

    // Automation / security
    [ObservableProperty] private string panicHotkey = "off";
    [ObservableProperty] private int healthCheckMinutes = 30;   // sign-in check for every account, 0 = off

    // Launching
    [ObservableProperty] private bool multiInstance = true;
    [ObservableProperty] private bool protectSignIn = true;   // not used (the feature was dropped); kept so older settings.json files load as before
    [ObservableProperty] private int launchDelay = 10;
    [ObservableProperty] private bool autoRejoin;
    [ObservableProperty] private bool autoRestart;
    [ObservableProperty] private bool isolateSignIns = true;   // empty Roblox's shared sign-in store before each launch
    [ObservableProperty] private bool antiAfk;
    [ObservableProperty] private int giveUpTries = 3;
    [ObservableProperty] private int giveUpMinutes = 10;
    [ObservableProperty] private bool keepTrying = true;   // past the give-up limit: one try every 5 minutes instead of stopping
    [ObservableProperty] private int closeGrace = 5;
    [ObservableProperty] private string launchArgs = "";
    [ObservableProperty] private string windowLayout = "Side by side";
    /// <summary>When a launch of several accounts is done, tile their windows into a grid (as many cells as clients).</summary>
    [ObservableProperty] private bool tileOnLaunch;   // "Side by side" or "On top of each other"

    // Performance
    public const int FpsMin = 5;
    public const int FpsMax = 240;   // the top of the slider: 240 FPS (no cap at all is the Unlimited FPS switch)

    [ObservableProperty] private string perfPreset = "Max alts";
    [ObservableProperty] private int fpsCap = 10;
    [ObservableProperty] private bool unlimitedFps;   // write "no limit" to Roblox's FPS flag instead of a cap
    [ObservableProperty] private bool startTiny = true;
    [ObservableProperty] private bool minimizeOnLaunch = true;
    [ObservableProperty] private int backgroundCpuLimit = 25;   // % of the whole CPU per background client, 0 = no limit
    [ObservableProperty] private bool muteOnStart;
    /// <summary>Marketplace: the most Robux one purchase may cost (0 = no limit). Above it, RoDeck won't buy.</summary>
    [ObservableProperty] private long marketSpendLimit;

    // Notifications (Windows pop-ups) for the account top right: all off until turned on
    [ObservableProperty] private bool notifyFriendOnline;
    [ObservableProperty] private bool notifyFavouritesOnly = true;   // with friends online: only starred friends
    [ObservableProperty] private bool notifyTrades;
    /// <summary>A trade you sent was answered: "Off", "Accepted", "Declined" or "Both".</summary>
    [ObservableProperty] private string notifyOutbound = "Off";
    [ObservableProperty] private bool notifyFriendRequests;
    // Friend requests: decline them all automatically (off by default)
    [ObservableProperty] private bool autoDeclineRequests;
    // Trades → Watchlist: limiteds and the price you'd buy at
    [ObservableProperty] private List<WatchEntry> watchlist = new();
    [ObservableProperty] private bool autoDeclineAllAccounts;   // false: only the account selected top right
    [ObservableProperty] private bool notifyDeals;
    [ObservableProperty] private int notifyDealPercent = 30;
    [ObservableProperty] private bool notifyDealsAffordable;

    // Trades → Trade ads: one Rolimons ad, posted again every few minutes while it's on (off until started)
    [ObservableProperty] private bool tradeAdOn;
    [ObservableProperty] private int tradeAdMinutes = 15;
    /// <summary>The Rolimons verification cookie, encrypted for this Windows user (see RolimonsAds.Protect).</summary>
    [ObservableProperty] private string roliVerification = "";
    public string TradeAdAccountId { get; set; } = "";
    public List<long> TradeAdOffer { get; set; } = new();
    public List<long> TradeAdRequest { get; set; } = new();
    public List<string> TradeAdTags { get; set; } = new();
    /// <summary>Each account's own trade ad (account id → slots). The lists above are the old single ad, moved in here once.</summary>
    public Dictionary<string, TradeAdDraft> TradeAds { get; set; } = new();
    public DateTime TradeAdLastPosted { get; set; } = DateTime.MinValue;
    public int TradeAdPosts { get; set; }
    [ObservableProperty] private bool lowestGraphics = true;
    [ObservableProperty] private bool lighterRendering = true;
    [ObservableProperty] private string msaa = "x1";
    [ObservableProperty] private string textureQuality = "Lowest";
    [ObservableProperty] private string renderingMode = "Roblox default";
    [ObservableProperty] private bool graySky = true;
    [ObservableProperty] private bool noGrass = true;

    /// <summary>Which FPS &amp; FastFlags profile the page shows: "Playing" or "Multi-launch" (see PerfProfile).</summary>
    [ObservableProperty] private string perfEditing = "Multi-launch";
    public PerfProfile? PlayPerf { get; set; }
    public PerfProfile? AltsPerf { get; set; }

    // The user's own preset, shown next to the three built-in ones (null until they save it)
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasCustomPerf), nameof(CustomPerfSummary))]
    private PerfSnapshot? customPerf;
    partial void OnUnlimitedFpsChanged(bool value) => OnPropertyChanged(nameof(UsesFpsCap));

    [System.Text.Json.Serialization.JsonIgnore] public bool HasCustomPerf => CustomPerf != null;
    [System.Text.Json.Serialization.JsonIgnore]
    public string CustomPerfSummary => CustomPerf == null ? "Not saved yet"
        : (CustomPerf.UnlimitedFps ? "Unlimited FPS" : $"{CustomPerf.FpsCap} FPS")
          + (CustomPerf.LowestGraphics ? ", lowest graphics" : "")
          + (CustomPerf.BackgroundCpuLimit > 0 ? $", CPU {CustomPerf.BackgroundCpuLimit}%" : "");

    /// <summary>The FPS cap is written to Roblox: always, except with Unlimited FPS (which writes its own) and with the
    /// "Roblox defaults" preset, which leaves Roblox's own frame rate alone.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesFpsCap => !UnlimitedFps && PerfPreset != "Roblox defaults";

    partial void OnFpsCapChanged(int value)
    {
        var clamped = Math.Clamp(value, FpsMin, FpsMax);
        if (clamped != value) FpsCap = clamped;
    }

    public ObservableCollection<Favourite> Favourites { get; set; } = new();
    public ObservableCollection<FastFlag> CustomFlags { get; set; } = new();   // FPS & FastFlags page
    public List<SavedGame> FavouriteGames { get; set; } = new();                // Games page: starred games
    public List<SavedGame> RecentGames { get; set; } = new();                  // Games page: newest first, at most 30
    public List<long> FavouriteFriends { get; set; } = new();                    // Roblox user IDs: shown first, with a star
    public ObservableCollection<LaunchPreset> Presets { get; set; } = new();
    public ObservableCollection<LayoutProfile> Layouts { get; set; } = new();
}

/// <summary>One account's trade ad: what it offers, what it wants (items and tags).</summary>
public class TradeAdDraft
{
    public List<long> Offer { get; set; } = new();
    public List<long> Request { get; set; } = new();
    public List<string> Tags { get; set; } = new();
}
