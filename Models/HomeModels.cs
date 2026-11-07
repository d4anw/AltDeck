using CommunityToolkit.Mvvm.ComponentModel;

namespace AltDeck.Models;

/// <summary>A friend of the current account who is online (Home).</summary>
public partial class FriendTile : ObservableObject
{
    public long UserId { get; init; }
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    /// <summary>1 online, 2 in game, 3 in Studio.</summary>
    public int PresenceType { get; init; }
    public string Location { get; init; } = "";
    public long PlaceId { get; init; }
    public string? JobId { get; init; }

    [ObservableProperty] private string headshotUrl = "";
    [ObservableProperty] private bool isFavourite;

    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
    public string Initials => string.IsNullOrWhiteSpace(Label) ? "?" : Label.Trim()[..Math.Min(2, Label.Trim().Length)].ToUpperInvariant();
    public string StatusKind => PresenceType switch { 0 => "offline", 2 => "ingame", 3 => "studio", _ => "online" };
    public string StatusText => PresenceType switch
    {
        0 => "Offline",
        2 => string.IsNullOrWhiteSpace(Location) ? "In a game" : Location,
        3 => "In Studio",
        _ => "Online"
    };
    public bool IsOnline => PresenceType is >= 1 and <= 3;
    /// <summary>Their game is visible to this account, so we can follow them in.</summary>
    public bool CanJoin => PresenceType == 2 && PlaceId > 0;
}

/// <summary>A game in a row on Home (and later the Games page).</summary>
public partial class GameTile : ObservableObject
{
    public long UniverseId { get; init; }
    // filled in before the tile is shown (see RobloxHome.FillMissingAsync)
    public long PlaceId { get; set; }
    public string Name { get; set; } = "";
    public long Playing { get; set; }
    public long UpVotes { get; set; }
    public long DownVotes { get; set; }

    [ObservableProperty] private string iconUrl = "";

    public string PlayingText => Playing switch
    {
        >= 1_000_000 => $"{Playing / 1_000_000.0:0.#}M playing",
        >= 1_000 => $"{Playing / 1_000.0:0.#}K playing",
        > 0 => $"{Playing} playing",
        _ => ""
    };
    public string LikeText => UpVotes + DownVotes > 0 ? $"{Math.Round(UpVotes * 100.0 / (UpVotes + DownVotes))}%" : "";
    public bool HasLikes => UpVotes + DownVotes > 0;
}
