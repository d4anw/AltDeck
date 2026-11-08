using CommunityToolkit.Mvvm.ComponentModel;

namespace AltDeck.Models;

/// <summary>An avatar item or bundle in the Marketplace.</summary>
public partial class MarketItem : ObservableObject
{
    public long Id { get; init; }
    /// <summary>"Asset" or "Bundle".</summary>
    public string ItemType { get; init; } = "Asset";
    public int AssetType { get; init; }
    public int BundleType { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; set; } = "";
    public string CreatorName { get; init; } = "";
    public long CreatorId { get; init; }
    /// <summary>"User" or "Group".</summary>
    public string CreatorType { get; init; } = "User";
    public bool CreatorVerified { get; init; }
    /// <summary>Price in Robux; null when it isn't sold by Roblox / the creator right now.</summary>
    public long? Price { get; init; }
    /// <summary>The cheapest a reseller sells it for (limiteds), 0 when none.</summary>
    public long LowestResale { get; init; }
    public string PriceStatus { get; init; } = "";
    public long Favorites { get; init; }
    public bool IsLimited { get; init; }
    public string CollectibleItemId { get; init; } = "";
    /// <summary>The asset IDs inside a bundle (with their type), for trying it on.</summary>
    public List<MarketPart> Parts { get; init; } = new();

    [ObservableProperty] private string thumbUrl = "";
    /// <summary>The current account owns it (null: not checked yet).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsOwned))] private bool? owned;
    public bool IsOwned => Owned == true;

    public bool IsBundle => ItemType == "Bundle";
    public bool IsFree => Price == 0;
    public bool ForSale => Price != null;
    /// <summary>Not sold by Roblox / the creator anymore, only by players reselling theirs.</summary>
    public bool IsResaleOnly => Price == null && LowestResale > 0;
    /// <summary>Can be bought one way or another: new, or from a reseller.</summary>
    public bool Buyable => ForSale || IsResaleOnly;

    /// <summary>"Free", "R$ 95" (also the best resale price for resale-only limiteds) or "Off sale".</summary>
    public string PriceText => Price switch
    {
        0 => "Free",
        long p => $"R$ {p:N0}",
        _ => LowestResale > 0 ? $"R$ {LowestResale:N0}" : "Off sale"
    };

    /// <summary>Under the price on a tile: "Lowest resale" when the price is a reseller's.</summary>
    public string PriceNote => IsResaleOnly ? "lowest resale" : "";

    public string KindText => IsBundle ? BundleKind(BundleType) : AssetKind(AssetType);

    public string WebUrl => IsBundle ? $"https://www.roblox.com/bundles/{Id}" : $"https://www.roblox.com/catalog/{Id}";

    public static string BundleKind(int t) => t switch
    {
        1 => "Body", 2 => "Animation pack", 3 => "Shoes", 4 => "Head", _ => "Bundle"
    };

    public static string AssetKind(int t) => t switch
    {
        2 => "Classic T-shirt", 8 => "Hat", 11 => "Classic shirt", 12 => "Classic pants", 17 => "Classic head", 18 => "Classic face",
        19 => "Gear", 41 => "Hair", 42 => "Face accessory", 43 => "Neck accessory", 44 => "Shoulder accessory",
        45 => "Front accessory", 46 => "Back accessory", 47 => "Waist accessory", 61 => "Emote",
        64 => "T-shirt", 65 => "Shirt", 66 => "Pants", 67 => "Jacket", 68 => "Sweater", 69 => "Shorts",
        70 => "Left shoe", 71 => "Right shoe", 72 => "Dress / skirt", 78 => "Mood", 79 => "Head",
        _ => "Avatar item"
    };
}

/// <summary>A player selling their copy of a limited.</summary>
public sealed class MarketSeller
{
    public string Name { get; init; } = "";
    public long SellerId { get; init; }
    public string SellerType { get; init; } = "User";
    public bool Verified { get; init; }
    public long Price { get; init; }
    /// <summary>The copy's serial number (#12 of 500), for limited U items; 0 when it has none.</summary>
    public long Serial { get; init; }
    public string ProductId { get; init; } = "";
    public string InstanceId { get; init; } = "";
    /// <summary>One of your own accounts is selling it (you can't buy from yourself).</summary>
    public bool IsYours { get; set; }

    public string PriceText => $"R$ {Price:N0}";
    public string SerialText => Serial > 0 ? $"#{Serial:N0}" : "";
}

/// <summary>One item inside a bundle.</summary>
public sealed record MarketPart(long Id, int Type, string Name);

/// <summary>A Marketplace category or subcategory chip. <see cref="Taxonomy"/> is what Roblox's search filters on.</summary>
public partial class MarketChip : ObservableObject
{
    public string Taxonomy { get; init; } = "";
    public string Name { get; init; } = "";
    public List<MarketChip> Subs { get; init; } = new();
    [ObservableProperty] private bool isSelected;
}

/// <summary>A choice in a Marketplace drop-down (sort, price).</summary>
public sealed record MarketOption(string Name, int Value)
{
    public override string ToString() => Name;
}
