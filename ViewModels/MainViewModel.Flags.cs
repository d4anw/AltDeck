using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using AltDeck.Models;
using CommunityToolkit.Mvvm.Input;

namespace AltDeck.ViewModels;

/// <summary>FPS &amp; FastFlags page: the user's own FastFlags (written with AltDeck's own during each launch).</summary>
public partial class MainViewModel
{
    public string CustomFlagsSummary
    {
        get
        {
            int on = Settings.CustomFlags.Count(f => f.Enabled && f.IsValid);
            int bad = Settings.CustomFlags.Count(f => f.Enabled && !f.IsValid);
            if (Settings.CustomFlags.Count == 0) return "No flags of your own yet.";
            return $"{on} flag{(on == 1 ? "" : "s")} written at each launch" + (bad > 0 ? $" · {bad} with a problem (skipped)" : "");
        }
    }

    /// <summary>Called once from the constructor: every change to the list or a flag is saved.</summary>
    private void SetupFlags()
    {
        foreach (var f in Settings.CustomFlags) f.PropertyChanged += OnFlagChanged;
        Settings.CustomFlags.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null) foreach (FastFlag f in e.NewItems) f.PropertyChanged += OnFlagChanged;
            if (e.OldItems != null) foreach (FastFlag f in e.OldItems) f.PropertyChanged -= OnFlagChanged;
            FlagsChanged();
        };
    }

    private void OnFlagChanged(object? sender, PropertyChangedEventArgs e) => FlagsChanged();

    private void FlagsChanged()
    {
        OnPropertyChanged(nameof(CustomFlagsSummary));
        QueueSettingsSave();
    }

    [RelayCommand]
    private void AddFlag() => Settings.CustomFlags.Add(new FastFlag { Name = "", Value = "" });

    [RelayCommand]
    private void RemoveFlag(FastFlag? f)
    {
        if (f != null) Settings.CustomFlags.Remove(f);
    }

    /// <summary>Adds the flags in a JSON object on the clipboard ({"FFlagX": true, "DFIntY": "10"}), replacing ones with the same name.</summary>
    [RelayCommand]
    private void PasteFlags()
    {
        string text;
        try { text = Clipboard.GetText(); } catch { text = ""; }
        if (string.IsNullOrWhiteSpace(text)) { StatusMessage = "The clipboard is empty. Copy a list of flags first."; return; }

        JsonObject? obj;
        try { obj = JsonNode.Parse(text.Trim()) as JsonObject; }
        catch (JsonException) { obj = null; }
        if (obj == null || obj.Count == 0)
        {
            StatusMessage = "The clipboard doesn't hold a list of flags. It should look like {\"FFlagSomething\": true}.";
            return;
        }

        int added = 0, replaced = 0;
        foreach (var (name, node) in obj)
        {
            var value = node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node?.ToJsonString() ?? "";
            var existing = Settings.CustomFlags.FirstOrDefault(f => string.Equals(f.Name.Trim(), name, StringComparison.Ordinal));
            if (existing != null) { existing.Value = value; existing.Enabled = true; replaced++; }
            else { Settings.CustomFlags.Add(new FastFlag { Name = name, Value = value }); added++; }
        }
        int bad = Settings.CustomFlags.Count(f => !f.IsValid);
        StatusMessage = $"Added {added} flag{(added == 1 ? "" : "s")}" + (replaced > 0 ? $", updated {replaced}" : "") + "."
                      + (bad > 0 ? $" {bad} have a problem and will be skipped (marked in red)." : "");
    }

    /// <summary>Copies the enabled, valid flags as JSON, the usual way to share them.</summary>
    [RelayCommand]
    private void CopyFlags()
    {
        var obj = new JsonObject();
        foreach (var f in Settings.CustomFlags.Where(f => f.Enabled && f.IsValid))
        {
            var v = f.WrittenValue;
            obj[f.Name.Trim()] = v is "true" or "false" ? JsonValue.Create(v == "true") : JsonValue.Create(v);
        }
        if (obj.Count == 0) { StatusMessage = "No flags to copy."; return; }
        try
        {
            Clipboard.SetText(obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            StatusMessage = $"Copied {obj.Count} flag{(obj.Count == 1 ? "" : "s")} as JSON.";
        }
        catch { StatusMessage = "Couldn't copy to the clipboard."; }
    }

    /// <summary>Removes all the user's flags. They're copied to the clipboard first, so Paste brings them back.</summary>
    [RelayCommand]
    private void ClearFlags()
    {
        if (Settings.CustomFlags.Count == 0) return;
        var backup = new JsonObject();
        foreach (var f in Settings.CustomFlags.Where(f => !string.IsNullOrWhiteSpace(f.Name)))
            backup[f.Name.Trim()] = f.WrittenValue;
        int n = Settings.CustomFlags.Count;
        bool copied = false;
        try { Clipboard.SetText(backup.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); copied = true; } catch { }
        Settings.CustomFlags.Clear();
        StatusMessage = $"Removed {n} flag{(n == 1 ? "" : "s")}." + (copied ? " They're on the clipboard: press Paste to bring them back." : "");
    }
}
