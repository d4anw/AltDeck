using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RoDeck.Models;

/// <summary>One item an avatar wears. <see cref="Meta"/> is Roblox's extra info for layered clothing (order etc.), passed back as is.</summary>
public sealed class WornAsset
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public int TypeId { get; init; }
    public string TypeName { get; init; } = "";
    public JsonNode? Meta { get; init; }
}

/// <summary>An account's whole look.</summary>
public sealed class AvatarState
{
    public string Type { get; set; } = "R15";
    public Dictionary<string, double> Scales { get; set; } = new();
    /// <summary>headColor3, torsoColor3, rightArmColor3, leftArmColor3, rightLegColor3, leftLegColor3 → "RRGGBB".</summary>
    public Dictionary<string, string> Colors { get; set; } = new();
    public List<WornAsset> Assets { get; set; } = new();
}

/// <summary>An item from the account's inventory in the wardrobe grid.</summary>
public partial class WardrobeItem : ObservableObject
{
    public long AssetId { get; init; }
    public string Name { get; init; } = "";
    public int TypeId { get; init; }
    /// <summary>Worn and Roblox allows moving / turning / resizing it (hats, face, neck, shoulder, front, back).</summary>
    public bool IsAdjustable { get; init; }
    [ObservableProperty] private string thumbUrl = "";
    [ObservableProperty] private bool isWorn;
    [ObservableProperty] private bool isBusy;
    /// <summary>Its adjust panel is open (the tile in Wearing gets the accent).</summary>
    [ObservableProperty] private bool isBeingAdjusted;
}

/// <summary>A saved outfit.</summary>
public partial class OutfitInfo : ObservableObject
{
    public long Id { get; init; }
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string thumbUrl = "";
}

/// <summary>A wardrobe category: a chip with a name and the Roblox item types in it.</summary>
public sealed record WardrobeCategory(string Key, string Name, int[] Types);

/// <summary>One body part's colour on the Body tab.</summary>
public partial class BodyPartColor : ObservableObject
{
    public string Key { get; init; } = "";      // headColor3…
    public string Name { get; init; } = "";     // Head…
    [ObservableProperty] private string hex = "A3A2A5";
    [ObservableProperty] private bool isSelected;
}

/// <summary>A colour in Roblox's skin-tone palette.</summary>
public sealed record PaletteColor(string Hex, string Name);
