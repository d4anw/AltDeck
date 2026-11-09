using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RoDeck.Models;

public partial class Account : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public long UserId { get; set; }
    public string Cookie { get; set; } = "";   // only ever stored inside the encrypted vault

    [ObservableProperty] private string username = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    [NotifyPropertyChangedFor(nameof(Initials))]
    private string displayName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    [NotifyPropertyChangedFor(nameof(Initials))]
    private string alias = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupList))]
    private string groups = "";

    [ObservableProperty] private string avatarUrl = "";
    [ObservableProperty] private string notes = "";
    [ObservableProperty] private string launchArgs = "";   // extra words for this account's clients, after the global ones
    [ObservableProperty] private bool fullQuality;
    [ObservableProperty] private bool bringBack = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StarGlyph))]
    private bool isFavourite;

    // ---- runtime-only state (never saved) ----
    [ObservableProperty]
    [property: JsonIgnore]
    private string statusKind = "offline";

    [ObservableProperty]
    [property: JsonIgnore]
    [NotifyPropertyChangedFor(nameof(PlayStatusText))]
    private string statusText = "Unchecked";

    [ObservableProperty]
    [property: JsonIgnore]
    private string checkedText = "";

    [ObservableProperty]
    [property: JsonIgnore]
    [NotifyPropertyChangedFor(nameof(PlayStatusText))]
    [NotifyPropertyChangedFor(nameof(CanPick))]
    private bool isPlaying;

    [ObservableProperty]
    [property: JsonIgnore]
    private bool isSelected;

    [ObservableProperty]
    [property: JsonIgnore]
    [NotifyPropertyChangedFor(nameof(RobuxText)), NotifyPropertyChangedFor(nameof(RobuxShort))]
    private long? robux;

    [ObservableProperty]
    [property: JsonIgnore]
    [NotifyPropertyChangedFor(nameof(RobuxText))]
    private bool isPremium;

    [JsonIgnore] public DateTime? LastChecked { get; set; }
    /// <summary>"R$ 1,234". Empty until the account has been checked.</summary>
    [JsonIgnore] public string RobuxText => Robux is long r ? $"R$ {r:N0}" : "";
    /// <summary>Just the amount ("1,234"), for the Robux pill top right. Empty until checked.</summary>
    [JsonIgnore] public string RobuxShort => Robux is long r ? r.ToString("N0") : "";
    [JsonIgnore] public string Label => string.IsNullOrWhiteSpace(Alias) ? DisplayName : Alias;
    [JsonIgnore] public string Initials => string.IsNullOrWhiteSpace(Label) ? "?" : Label.Trim().Substring(0, Math.Min(2, Label.Trim().Length)).ToUpperInvariant();
    [JsonIgnore] public string[] GroupList => (Groups ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    [JsonIgnore] public string StarGlyph => IsFavourite ? "\uE735" : "\uE734";
    [JsonIgnore] public string PlayStatusText => IsPlaying ? "Already playing" : StatusText;
    [JsonIgnore] public bool CanPick => !IsPlaying;
}
