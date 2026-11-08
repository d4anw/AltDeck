using System.Text.Json.Nodes;
using System.Windows.Media.Media3D;
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
        if (!Show3D) Show3D = true;   // adjusting is shown live on the 3D model
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
        _ = BindLiveAsync(worn.Id);
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
        UpdateLive();

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

    // ================================================================== live 3D while adjusting
    // Roblox's own app moves the accessory on its 3D model right away instead of waiting for a new render. Same here:
    // the 3D model is made of pieces (one per material, and every accessory has its own texture), so the piece with the
    // adjusted item's texture is moved, turned and resized on screen at once. Saving to Roblox still happens in the
    // background, and the next model Roblox makes already has the change in it.

    /// <summary>What the 3D view shows: the loaded model, with a handle on every piece so one can be moved.</summary>
    [ObservableProperty] private Avatar3D? avatar3DShown;
    /// <summary>Under the adjust panel: whether the 3D preview follows the sliders.</summary>
    [ObservableProperty] private string adjustLiveNote = "";

    private readonly List<Model3DGroup> _livePieces = new();
    private List<int> _adjustPieces = new();
    private long _livePiecesFor;
    /// <summary>Each accessory's adjustment as it was when the shown 3D model was made (position, rotation, scale).</summary>
    private Dictionary<long, (Vector3D Pos, Vector3D Rot, Vector3D Scale)> _modelBaseline = new();

    private void MakeLiveModel(Avatar3D? model)
    {
        _livePieces.Clear();
        _adjustPieces = new();
        _livePiecesFor = 0;
        if (model == null) { Avatar3DShown = null; return; }
        var live = new Model3DGroup();
        foreach (var piece in model.Model.Children)
        {
            var holder = new Model3DGroup();
            holder.Children.Add(piece);
            live.Children.Add(holder);
            _livePieces.Add(holder);
        }
        Avatar3DShown = model with { Model = live };
        if (AdjustItem is { } a) _ = BindLiveAsync(a.Id);
    }

    /// <summary>Every worn item's adjustment right now (the look on screen).</summary>
    private Dictionary<long, (Vector3D Pos, Vector3D Rot, Vector3D Scale)> SnapshotRefine()
    {
        var d = new Dictionary<long, (Vector3D, Vector3D, Vector3D)>();
        if (AvatarLook is not { } look) return d;
        foreach (var w in look.Assets)
        {
            var m = w.Meta as JsonObject;
            d[w.Id] = (new Vector3D(Read(m, "position", "X", 0), Read(m, "position", "Y", 0), Read(m, "position", "Z", 0)),
                       new Vector3D(Read(m, "rotation", "X", 0), Read(m, "rotation", "Y", 0), Read(m, "rotation", "Z", 0)),
                       new Vector3D(Read(m, "scale", "X", 1), Read(m, "scale", "Y", 1), Read(m, "scale", "Z", 1)));
        }
        return d;
    }

    /// <summary>Finds the pieces of the 3D model that are this item (by its textures).</summary>
    private async Task BindLiveAsync(long assetId)
    {
        if (Avatar3DShown is not { } shown || CurrentAccount is not { } a) { AdjustLiveNote = "Loading 3D…"; return; }
        if (_livePiecesFor == assetId && _adjustPieces.Count > 0) { UpdateLive(); return; }
        AdjustLiveNote = "Finding it on the 3D model…";
        HashSet<string> textures;
        try { textures = await RobloxAvatar3D.GetAssetTexturesAsync(a.Cookie, assetId, CancellationToken.None); }
        catch (Exception) { textures = new(); }
        if (AdjustItem?.Id != assetId || Avatar3DShown != shown) return;
        var found = new List<int>();
        for (int i = 0; i < shown.Textures.Count && i < _livePieces.Count; i++)
            if (shown.Textures[i] is { } t && textures.Contains(t)) found.Add(i);
        _adjustPieces = found;
        _livePiecesFor = assetId;
        AdjustLiveNote = found.Count > 0
            ? "Live: the 3D preview follows the sliders."
            : "This item can't be moved on the 3D model, so the preview updates a moment after you let go.";
        UpdateLive();
    }

    /// <summary>Moves / turns / resizes the adjusted item's pieces by how far the sliders are from what the model shows.</summary>
    private void UpdateLive()
    {
        if (AdjustItem is not { } item || _livePiecesFor != item.Id || _adjustPieces.Count == 0) return;
        (Vector3D Pos, Vector3D Rot, Vector3D Scale) b = _modelBaseline.TryGetValue(item.Id, out var known)
            ? known : (new Vector3D(), new Vector3D(), new Vector3D(1, 1, 1));

        Rect3D bounds = Rect3D.Empty;
        foreach (var i in _adjustPieces) bounds.Union(_livePieces[i].Children[0].Bounds);
        var c = new Point3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);

        static double Ratio(double now, double was) => was > 0.0001 ? now / was : now;
        var t = new Transform3DGroup();
        t.Children.Add(new ScaleTransform3D(Ratio(AdjScaleX, b.Scale.X), Ratio(AdjScaleY, b.Scale.Y), Ratio(AdjScaleZ, b.Scale.Z), c.X, c.Y, c.Z));
        t.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), AdjRotX - b.Rot.X), c));
        t.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), AdjRotY - b.Rot.Y), c));
        t.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), AdjRotZ - b.Rot.Z), c));
        t.Children.Add(new TranslateTransform3D(AdjPosX - b.Pos.X, AdjPosY - b.Pos.Y, AdjPosZ - b.Pos.Z));
        foreach (var i in _adjustPieces) _livePieces[i].Transform = t;
    }
}

