using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AltDeck.Models;

/// <summary>One FastFlag the user added on the FPS &amp; FastFlags page. Saved in settings.json.</summary>
public partial class FastFlag : ObservableObject
{
    static readonly Regex NameRx = new(@"^(D|S)?F(Flag|Int|String|Log)[A-Za-z0-9_]+$", RegexOptions.Compiled);

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Problem), nameof(IsValid))]
    private string name = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Problem), nameof(IsValid))]
    private string value = "";

    [ObservableProperty] private bool enabled = true;

    /// <summary>Why this flag won't be written, or "" when it's fine.</summary>
    [JsonIgnore]
    public string Problem
    {
        get
        {
            var n = (Name ?? "").Trim();
            var v = (Value ?? "").Trim();
            if (n.Length == 0) return "Needs a name";
            if (!NameRx.IsMatch(n)) return "Flag names look like FFlagSomething, DFIntSomething or FStringSomething";
            if (n.Contains("Flag", StringComparison.Ordinal) && n.IndexOf("Flag", StringComparison.Ordinal) <= 2
                && !v.Equals("true", StringComparison.OrdinalIgnoreCase) && !v.Equals("false", StringComparison.OrdinalIgnoreCase))
                return "This flag takes true or false";
            if (n.Contains("Int", StringComparison.Ordinal) && n.IndexOf("Int", StringComparison.Ordinal) <= 2 && !long.TryParse(v, out _))
                return "This flag takes a whole number";
            return "";
        }
    }

    [JsonIgnore] public bool IsValid => Problem.Length == 0;

    /// <summary>The value as Roblox expects it in ClientAppSettings.json (true / false lowercase).</summary>
    [JsonIgnore]
    public string WrittenValue
    {
        get
        {
            var v = (Value ?? "").Trim();
            return v.Equals("true", StringComparison.OrdinalIgnoreCase) ? "true"
                 : v.Equals("false", StringComparison.OrdinalIgnoreCase) ? "false"
                 : v;
        }
    }
}
