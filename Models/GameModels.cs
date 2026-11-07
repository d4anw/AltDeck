using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AltDeck.Models;

/// <summary>One public server in the server finder.</summary>
public partial class ServerEntry : ObservableObject
{
    public string JobId { get; init; } = "";
    public int Playing { get; init; }
    public int MaxPlayers { get; init; }
    /// <summary>Roblox's ping figure for the server (the average of its players), in ms.</summary>
    public double? Ping { get; init; }
    public double? Fps { get; init; }

    /// <summary>Friends of the current account in this server, e.g. "Bob, Ann".</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasFriends))]
    private string friends = "";

    public bool HasFriends => Friends.Length > 0;
    public int FreeSlots => Math.Max(0, MaxPlayers - Playing);
    public string PingText => Ping is double p ? $"{p:0} ms" : "–";
    public string FpsText => Fps is double f ? $"{f:0}" : "–";
    public string PlayersText => $"{Playing}/{MaxPlayers}";
    public double Fill => MaxPlayers > 0 ? Math.Min(1, Playing / (double)MaxPlayers) : 0;
    /// <summary>For colouring the ping: 0 good (under 80), 1 ok (under 150), 2 slow.</summary>
    public int PingLevel => Ping is not double p ? 1 : p < 80 ? 0 : p < 150 ? 1 : 2;
}

/// <summary>A game in "My favourites" or "Recently played". Saved in settings.json.</summary>
public class SavedGame
{
    public long UniverseId { get; set; }
    public long PlaceId { get; set; }
    public string Name { get; set; } = "";
    public DateTime When { get; set; } = DateTime.Now;

    [JsonIgnore]
    public GameTile Tile => new() { UniverseId = UniverseId, PlaceId = PlaceId, Name = Name };
}

/// <summary>A chip above the game list: a chart, or My favourites / Recently played.</summary>
public partial class ChartChip : ObservableObject
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Icon { get; init; } = "";
    [ObservableProperty] private bool isSelected;
}
