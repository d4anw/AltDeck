using System.Text.Json.Nodes;
using RoDeck.Models;
using RoDeck.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RoDeck.ViewModels;

/// <summary>
/// Accessory adjustment (Roblox calls it refinement): move, turn and resize a worn hat / face / neck / shoulder / front /
/// back accessory within Roblox's limits. Stored in the item's "meta" when the look is saved, like on roblox.com.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The worn accessory being adjusted (null: the panel is closed).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsAdjusting), nameof(AdjustThumb))]
    private WornAsset? adjustItem;
    public bool IsAdjusting => AdjustItem != null;
    /// <summary>The picture of the item being adjusted, for the panel's header.</summary>
    public string AdjustThumb => AdjustItem is { } a ? WornItems.FirstOrDefault(w => w.AssetId == a.Id)?.ThumbUrl ?? "" : "";

    partial void OnAdjustItemChanged(WornAsset? value)
    {
        foreach (var w in WornItems) w.IsBeingAdjusted = value != null && w.AssetId == value.Id;
    }

    /// <summary>The ⚙ on a worn hat / face / neck / shoulder / front / back item: open (or close) its adjust panel.</summary>
    [RelayCommand]
    private void AdjustWorn(WardrobeItem? item)
    {
        if (item == null || AvatarLook is not { } look) return;
        var worn = look.Assets.FirstOrDefault(w => w.Id == item.AssetId);
        if (worn == null || !CanAdjust(worn.TypeId)) return;
        if (AdjustItem?.Id == worn.Id) { AdjustItem = null; return; }
        OpenAdjust(worn);
    }

    /// <summary>Clicking a worn item takes it off (closing its adjust panel if that was open).</summary>
    [RelayCommand]
    private void TakeOffWorn(WardrobeItem? item)
    {
        if (item == null) return;
        if (AdjustItem?.Id == item.AssetId) AdjustItem = null;
        ToggleWear(item);
    }

    [RelayCommand]
    private void ResetAdjustGroup(string? group)
    {
        _adjLoading = true;
        switch (group)
        {
            case "Position": AdjPosX = AdjPosY = AdjPosZ = 0; break;
            case "Rotation": AdjRotX = AdjRotY = AdjRotZ = 0; break;
            case "Size": AdjScaleX = AdjScaleY = AdjScaleZ = 1; break;
        }
        _adjLoading = false;
        ApplyAdjust();
    }

    // position (studs), rotation (degrees), scale (1 = normal), per axis
    [ObservableProperty] private double adjPosX, adjPosY, adjPosZ;
    [ObservableProperty] private double adjRotX, adjRotY, adjRotZ;
    [ObservableProperty] private double adjScaleX = 1, adjScaleY = 1, adjScaleZ = 1;
    /// <summary>Resize evenly: moving one size slider moves all three.</summary>
    [ObservableProperty] private bool keepProportions = true;

    private bool _adjLoading;

    private RefineBounds? Bounds => AdjustItem != null && _rules?.Refine.TryGetValue(AdjustItem.TypeId, out var b) == true ? b : null;
    public double PosMin => Bounds?.PosMin[0] ?? -0.25;  public double PosMax => Bounds?.PosMax[0] ?? 0.25;
    public double RotXMin => Bounds?.RotMin[0] ?? -30;   public double RotXMax => Bounds?.RotMax[0] ?? 30;
    public double RotMin => Bounds?.RotMin[1] ?? -30;    public double RotMax => Bounds?.RotMax[1] ?? 30;
    public double ScaleMin => Bounds?.ScaleMin[0] ?? 0.8; public double ScaleMax => Bounds?.ScaleMax[0] ?? 1.2;

    private bool CanAdjust(int typeId) => _rules?.Refine.ContainsKey(typeId) == true;

    /// <summary>Click on a worn item: adjust it if Roblox allows that for its kind, else take it off.</summary>
    [RelayCommand]
    private void AdjustOrTakeOff(WardrobeItem? item)
    {
        if (item == null || AvatarLook is not { } look) return;
        var worn = look.Assets.FirstOrDefault(w => w.Id == item.AssetId);
        if (worn == null || !CanAdjust(worn.TypeId)) { ToggleWear(item); return; }
        OpenAdjust(worn);
    }

    private void OpenAdjust(WornAsset worn)
    {
        _adjLoading = true;
        AdjustItem = worn;
        var m = worn.Meta as JsonObject;
        AdjPosX = Read(m, "position", "X", 0); AdjPosY = Read(m, "position", "Y", 0); AdjPosZ = Read(m, "position", "Z", 0);
        AdjRotX = Read(m, "rotation", "X", 0); AdjRotY = Read(m, "rotation", "Y", 0); AdjRotZ = Read(m, "rotation", "Z", 0);
        AdjScaleX = Read(m, "scale", "X", 1); AdjScaleY = Read(m, "scale", "Y", 1); AdjScaleZ = Read(m, "scale", "Z", 1);
        KeepProportions = Math.Abs(AdjScaleX - AdjScaleY) < 0.001 && Math.Abs(AdjScaleY - AdjScaleZ) < 0.001;
        foreach (var n in new[] { nameof(PosMin), nameof(PosMax), nameof(RotXMin), nameof(RotXMax), nameof(RotMin), nameof(RotMax), nameof(ScaleMin), nameof(ScaleMax) })
            OnPropertyChanged(n);
        _adjLoading = false;
    }

    [RelayCommand]
    private void CloseAdjust() => AdjustItem = null;

    [RelayCommand]
    private void ResetAdjust()
    {
        _adjLoading = true;
        AdjPosX = AdjPosY = AdjPosZ = 0;
        AdjRotX = AdjRotY = AdjRotZ = 0;
        AdjScaleX = AdjScaleY = AdjScaleZ = 1;
        _adjLoading = false;
        ApplyAdjust();
        StatusMessage = "Put the accessory back to normal.";
    }

    [RelayCommand]
    private void TakeOffAdjusting()
    {
        if (AdjustItem is not { } w) return;
        AdjustItem = null;
        ToggleWear(new WardrobeItem { AssetId = w.Id, Name = w.Name, TypeId = w.TypeId });
    }

    partial void OnAdjPosXChanged(double value) => ApplyAdjust();
    partial void OnAdjPosYChanged(double value) => ApplyAdjust();
    partial void OnAdjPosZChanged(double value) => ApplyAdjust();
    partial void OnAdjRotXChanged(double value) => ApplyAdjust();
    partial void OnAdjRotYChanged(double value) => ApplyAdjust();
    partial void OnAdjRotZChanged(double value) => ApplyAdjust();
    partial void OnAdjScaleXChanged(double value) => EvenScale(value);
    partial void OnAdjScaleYChanged(double value) => EvenScale(value);
    partial void OnAdjScaleZChanged(double value) => EvenScale(value);

    private void EvenScale(double value)
    {
        if (!_adjLoading && KeepProportions)
        {
            _adjLoading = true;
            AdjScaleX = AdjScaleY = AdjScaleZ = value;
            _adjLoading = false;
        }
        ApplyAdjust();
    }

    /// <summary>Puts the slider values in the item's meta and saves with the next bundled save (shortly after the last change).</summary>
    private void ApplyAdjust()
    {
        if (_adjLoading || AdjustItem is not { } w || AvatarLook is not { } look || CurrentAccount == null) return;
        var meta = w.Meta is JsonObject old ? (JsonObject)old.DeepClone() : new JsonObject();
        meta["position"] = Vec3(AdjPosX, AdjPosY, AdjPosZ);
        meta["rotation"] = Vec3(AdjRotX, AdjRotY, AdjRotZ);
        meta["scale"] = Vec3(AdjScaleX, AdjScaleY, AdjScaleZ);
        meta["version"] = 1;

        var updated = new WornAsset { Id = w.Id, Name = w.Name, TypeId = w.TypeId, TypeName = w.TypeName, Meta = meta };
        _lastSavedAssets ??= look.Assets.ToList();
        look.Assets = look.Assets.Select(a => a.Id == w.Id ? updated : a).ToList();
        _adjLoading = true;
        AdjustItem = updated;   // keep the panel on the new version
        _adjLoading = false;
        PreviewUpdating = true;

        if (!_wearTimerHooked) { _wearSaveTimer.Tick += async (_, _) => { _wearSaveTimer.Stop(); await SaveWearingAsync(); }; _wearTimerHooked = true; }
        _wearSaveTimer.Stop();
        _wearSaveTimer.Start();
    }

    private static JsonObject Vec3(double x, double y, double z) =>
        new() { ["X"] = Math.Round(x, 3), ["Y"] = Math.Round(y, 3), ["Z"] = Math.Round(z, 3) };

    /// <summary>meta.position.X etc. (Roblox writes X or x), or the default when it isn't there.</summary>
    private static double Read(JsonObject? meta, string group, string axis, double fallback)
    {
        if (meta == null) return fallback;
        var g = meta.FirstOrDefault(kv => string.Equals(kv.Key, group, StringComparison.OrdinalIgnoreCase)).Value as JsonObject;
        var v = g?.FirstOrDefault(kv => string.Equals(kv.Key, axis, StringComparison.OrdinalIgnoreCase)).Value;
        return v is JsonValue jv && jv.TryGetValue<double>(out var d) ? d : fallback;
    }
}
