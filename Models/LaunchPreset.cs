using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AltDeck.Models;

/// <summary>A saved "who plays where": a set of accounts plus a target, launched with one click or a hotkey.</summary>
public partial class LaunchPreset : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private string name = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private string target = "";

    /// <summary>Account.Id values (not user IDs), in launch order.</summary>
    public List<string> AccountIds { get; set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    [NotifyPropertyChangedFor(nameof(HasHotkey))]
    private string hotkey = "";

    /// <summary>"Active: Ctrl+Shift+1" or why the hotkey couldn't be registered. Never saved.</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private string hotkeyStatus = "";

    [JsonIgnore] public bool HasHotkey => !string.IsNullOrWhiteSpace(Hotkey);

    [JsonIgnore]
    public string ToolTipText
    {
        get
        {
            var lines = new List<string>
            {
                $"{AccountIds.Count} account{(AccountIds.Count == 1 ? "" : "s")} → {Target}",
                "Click to launch. Right-click for more."
            };
            if (HasHotkey) lines.Add(string.IsNullOrEmpty(HotkeyStatus) ? "Hotkey: " + Hotkey : HotkeyStatus);
            return string.Join("\n", lines);
        }
    }

    /// <summary>Call after changing AccountIds (a plain list, so it can't announce changes by itself).</summary>
    public void AccountsChanged() => OnPropertyChanged(nameof(ToolTipText));
}
