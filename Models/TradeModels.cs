using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AltDeck.Models;

/// <summary>One limited's numbers from the values list. <see cref="Value"/> is -1 when it has no value (RAP is used then).</summary>
public sealed record LimitedInfo
{
    public long AssetId { get; init; }
    public string Name { get; init; } = "";
    public string Acronym { get; init; } = "";
    public long Rap { get; init; }
    public long Value { get; init; } = -1;
    /// <summary>-1 none, 0 terrible … 4 amazing.</summary>
    public int Demand { get; init; } = -1;
    /// <summary>-1 none, 0 lowering, 1 unstable, 2 stable, 3 raising, 4 fluctuating.</summary>
    public int Trend { get; init; } = -1;
    /// <summary>The RAP was pushed up on purpose and isn't a fair price.</summary>
    public bool Projected { get; init; }
    public bool Hyped { get; init; }
    public bool Rare { get; init; }

    public bool HasValue => Value > 0;
    /// <summary>What it's worth: its value, or its RAP when it has none.</summary>
    public long Worth => HasValue ? Value : Rap;

    public string DemandText => Limited.DemandText(Demand);
    public string TrendText => Trend switch { 0 => "Lowering", 1 => "Unstable", 2 => "Stable", 3 => "Raising", 4 => "Fluctuating", _ => "" };
}

public static class Limited
{
    public static string DemandText(int d) => d switch { 0 => "Terrible", 1 => "Low", 2 => "Normal", 3 => "High", 4 => "Amazing", _ => "" };
    public static string Robux(long r) => $"R$ {r:N0}";
    /// <summary>475,397 stays "475,397"; 1,234,567 → "1.23M".</summary>
    public static string Short(long r) => r >= 1_000_000 ? $"{r / 1_000_000d:0.##}M" : r.ToString("N0");

    /// <summary>"+R$ 216 (+2.7%)" / "−255 (−3.2%)" for a difference against a base.</summary>
    public static string Diff(long diff, long of) =>
        $"{(diff >= 0 ? "+" : "−")}{Math.Abs(diff):N0} ({(diff >= 0 ? "+" : "−")}{(of > 0 ? Math.Abs((double)diff / of) : 0):0.#%})";
}

/// <summary>A limited in a grid: the catalog, an inventory, a trade, or a side of a trade being made.</summary>
public partial class LimitedTile : ObservableObject
{
    public long AssetId { get; init; }
    public string Name { get; init; } = "";
    public long Rap { get; init; }
    public long Value { get; init; } = -1;
    public int Demand { get; init; } = -1;
    public bool Projected { get; init; }
    public bool Rare { get; init; }
    /// <summary>One copy: its serial (0 none), its instance ID (what trades send) and whether it's on trade hold.</summary>
    public long Serial { get; init; }
    public string InstanceId { get; init; } = "";
    public bool IsOnHold { get; init; }

    [ObservableProperty] private string thumbUrl = "";
    [ObservableProperty] private bool isSelected;

    public bool HasValue => Value > 0;
    public long Worth => HasValue ? Value : Rap;
    public string RapText => "RAP " + Limited.Short(Rap);
    /// <summary>Its value, or its RAP when it has no value (as traders count it).</summary>
    public string ValueText => "Value " + Limited.Short(Worth);
    public string WorthNumber => Limited.Short(Worth);
    public string RapNumber => Limited.Short(Rap);
    public string DemandText => Limited.DemandText(Demand);
    public string SerialText => Serial > 0 ? $"#{Serial:N0}" : "";

    /// <summary>Builds a tile from what Roblox said plus the values list.</summary>
    public static LimitedTile From(long assetId, string name, long robloxRap, long serial = 0, string instanceId = "", bool onHold = false)
    {
        var v = Services.LimitedValues.Get(assetId);
        return new LimitedTile
        {
            AssetId = assetId,
            Name = v?.Name is { Length: > 0 } n ? n : name,
            Rap = v?.Rap > 0 ? v.Rap : robloxRap,
            Value = v?.Value ?? -1,
            Demand = v?.Demand ?? -1,
            Projected = v?.Projected ?? false,
            Rare = v?.Rare ?? false,
            Serial = serial,
            InstanceId = instanceId,
            IsOnHold = onHold
        };
    }
}

/// <summary>A new listing in the deal feed, measured against the item's value (or RAP when it has none).</summary>
public partial class DealTile : ObservableObject
{
    public long AssetId { get; init; }
    public string Name { get; init; } = "";
    public long Price { get; init; }
    public long Rap { get; init; }
    public long Value { get; init; } = -1;
    public int Demand { get; init; } = -1;
    public bool Projected { get; init; }
    public DateTime At { get; init; }
    /// <summary>Came in as a new listing (true) or was found by the price check (false: we only know it's this cheap now).</summary>
    public bool Listed { get; init; }
    [ObservableProperty] private string thumbUrl = "";
    [ObservableProperty] private string agoText = "";

    public bool HasValue => Value > 0;
    /// <summary>What the price is compared with.</summary>
    public long Worth => HasValue ? Value : Rap;
    public string Basis => HasValue ? "value" : "RAP";
    /// <summary>0.35 = 35 % below what it's worth; negative = above.</summary>
    public double Discount => Worth > 0 ? 1 - (double)Price / Worth : 0;
    public string DiscountText => Discount >= 0 ? $"-{Discount:P0}" : $"+{-Discount:P0}";
    public long Saving => Math.Max(0, Worth - Price);
    public string PriceText => Limited.Robux(Price);
    public string WorthText => $"{Basis} {Limited.Short(Worth)}";
    public string DetailText => $"RAP {Limited.Short(Rap)}{(HasValue ? $" · value {Limited.Short(Value)}" : "")}{(Demand >= 0 ? $" · {Limited.DemandText(Demand)} demand" : "")}";

    public void Tick()
    {
        var s = (DateTime.Now - At).TotalSeconds;
        var ago = s < 60 ? $"{Math.Max(1, (int)s)}s ago" : s < 3600 ? $"{(int)(s / 60)}m ago" : $"{(int)(s / 3600)}h ago";
        AgoText = (Listed ? "listed " : "seen ") + ago;
    }
}

/// <summary>One account's limiteds and what they're worth.</summary>
public partial class AccountWorth : ObservableObject
{
    public Account Account { get; init; } = null!;
    [ObservableProperty] private int count;
    [ObservableProperty] private long rap;
    [ObservableProperty] private long value;
    [ObservableProperty] private bool loading;
    [ObservableProperty] private string note = "";
    [ObservableProperty] private bool isSelected;
    public List<LimitedTile> Items { get; set; } = new();

    public string RapText => Limited.Robux(Rap);
    public string ValueText => Limited.Robux(Value);
    partial void OnRapChanged(long value) => OnPropertyChanged(nameof(RapText));
    partial void OnValueChanged(long value) => OnPropertyChanged(nameof(ValueText));
}

/// <summary>A trade in the list. The value totals fill in once its details are loaded.</summary>
public partial class TradeRow : ObservableObject
{
    public long Id { get; init; }
    public long PartnerId { get; init; }
    public string PartnerName { get; init; } = "";
    public string PartnerDisplay { get; init; } = "";
    public DateTime Created { get; init; }
    public DateTime Expiration { get; init; }
    public string Status { get; init; } = "";
    public bool IsActive { get; init; }
    [ObservableProperty] private string headshotUrl = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSummary))] private string summary = "";
    /// <summary>1 win, -1 loss, 0 even / not known.</summary>
    [ObservableProperty] private int result;
    [ObservableProperty] private bool isSelected;
    public bool HasSummary => Summary.Length > 0;
    /// <summary>What you give and what you get (value), for the coloured bar.</summary>
    [ObservableProperty] private string giveText = "";
    [ObservableProperty] private string getText = "";
    /// <summary>The partner's whole inventory value (when it's public), under their name.</summary>
    [ObservableProperty] private string partnerWorth = "";

    /// <summary>"2 days", "5 hours", "3 min".</summary>
    public string AgoText
    {
        get
        {
            if (Created == default) return "";
            var s = DateTime.UtcNow - Created;
            return s.TotalDays >= 1 ? $"{(int)s.TotalDays} day{((int)s.TotalDays == 1 ? "" : "s")}"
                 : s.TotalHours >= 1 ? $"{(int)s.TotalHours} hour{((int)s.TotalHours == 1 ? "" : "s")}"
                 : $"{Math.Max(1, (int)s.TotalMinutes)} min";
        }
    }

    public string PartnerLabel => string.IsNullOrWhiteSpace(PartnerDisplay) ? PartnerName : PartnerDisplay;
    public string WhenText => Created == default ? "" : Created.ToLocalTime().ToString("d MMM, HH:mm");
}

/// <summary>One side of a trade.</summary>
public sealed class TradeSide
{
    public long UserId { get; init; }
    public string UserName { get; init; } = "";
    public long Robux { get; init; }
    public List<LimitedTile> Items { get; init; } = new();
    public long Worth => Items.Sum(i => i.Worth);
    public long Rap => Items.Sum(i => i.Rap);
}

/// <summary>A trade opened: what you give, what you get, and the verdict.</summary>
public sealed class TradeDetail
{
    public long Id { get; init; }
    public string Status { get; init; } = "";
    public TradeSide Mine { get; init; } = new();
    public TradeSide Theirs { get; init; } = new();
    // Robux you send count in full; Robux you get count at 70 %, because Roblox keeps 30 % of Robux in trades.
    public long Give => Mine.Worth + Mine.Robux;
    public long Get => Theirs.Worth + (long)(Theirs.Robux * 0.7);
    public long Difference => Get - Give;
    public long RapDifference => Theirs.Rap + (long)(Theirs.Robux * 0.7) - Mine.Rap - Mine.Robux;
}
