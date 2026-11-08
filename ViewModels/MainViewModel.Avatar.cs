using System.Collections.ObjectModel;
using System.Windows;
using RoDeck.Controls;
using RoDeck.Models;
using RoDeck.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RoDeck.ViewModels;

/// <summary>Avatar page: what the account top right wears, its wardrobe, body colours, scales, R6 / R15 and outfits.</summary>
public partial class MainViewModel
{
    public static IReadOnlyList<WardrobeCategory> WardrobeCategories { get; } = new[]
    {
        new WardrobeCategory("hats", "Hats", new[] { 8 }),
        new WardrobeCategory("hair", "Hair", new[] { 41 }),
        new WardrobeCategory("heads", "Heads & faces", new[] { 79 }),
        new WardrobeCategory("accessories", "Accessories", new[] { 42, 43, 44, 45, 46, 47 }),
        new WardrobeCategory("classic", "Shirts & pants", new[] { 11, 12, 2 }),
        new WardrobeCategory("layered", "Layered clothing", new[] { 64, 65, 66, 67, 68, 69, 72 }),
        new WardrobeCategory("shoes", "Shoes", new[] { 70, 71 }),
        new WardrobeCategory("body", "Body parts", new[] { 27, 28, 29, 30, 31 }),
        new WardrobeCategory("makeup", "Makeup", new[] { 88, 89, 90, 76, 77 }),
        new WardrobeCategory("animations", "Animations", new[] { 51, 55, 53, 52, 50, 48, 54, 78 }),
    };

    /// <summary>"Wardrobe", "Body" or "Outfits".</summary>
    [ObservableProperty] private string avatarTab = "Wardrobe";

    [ObservableProperty] private AvatarState? avatarLook;
    [ObservableProperty] private string avatarPicture = "";
    [ObservableProperty] private bool avatarBusy;
    [ObservableProperty] private string avatarSummary = "";
    public Loader AvatarLoad { get; } = new();

    public bool IsR15 => AvatarLook?.Type == "R15";
    partial void OnAvatarLookChanged(AvatarState? value) => OnPropertyChanged(nameof(IsR15));

    private AvatarRules? _rules;
    private DateTime _avatarLoadedAt = DateTime.MinValue;
    private Account? _avatarLoadedFor;

    private void MaybeLoadAvatar()
    {
        if (Page != "Avatar") return;
        if (_avatarLoadedFor == CurrentAccount && DateTime.Now - _avatarLoadedAt < TimeSpan.FromMinutes(5)) return;
        _ = LoadAvatarAsync();
    }

    [RelayCommand]
    private Task RefreshAvatar() => LoadAvatarAsync();

    private async Task LoadAvatarAsync()
    {
        var a = CurrentAccount;
        _avatarLoadedFor = a;
        _avatarLoadedAt = DateTime.Now;
        _previewGeneration++;
        AdjustItem = null;
        AvatarLook = null;
        AvatarPicture = "";
        Avatar3DModel = null;
        PictureIsNewer = false;
        Preview3DNote = "";
        WornItems.Clear();
        WardrobeItems.Clear();
        _wardrobeAll = new();
        Outfits.Clear();
        _outfitsLoadedFor = null;
        if (a == null || a.UserId == 0) { AvatarLoad.State = LoadState.Empty; return; }

        // the wardrobe, outfits and picture don't need the look first: start them now, side by side
        if (AvatarTab == "Outfits") _ = LoadOutfitsAsync(); else _ = LoadWardrobeAsync();
        _ = LoadPictureAsync(a);
        _model3DStale = true;
        if (Show3D) _ = Open3DAsync();

        await AvatarLoad.RunAsync(async ct =>
        {
            var rules = _rules == null ? RobloxAvatar.GetRulesAsync(ct) : Task.FromResult(_rules);
            var lookTask = RobloxAvatar.GetAvatarAsync(a.UserId, ct);
            var both = Task.WhenAll(rules, lookTask);
            if (await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(20), ct)) != both)
                throw new RobloxException("Roblox is taking too long to answer. Try again in a moment.");
            if (_rules == null) { _rules = await rules; NotifyRules(); }
            var look = await lookTask;
            if (a != CurrentAccount) return false;
            AvatarLook = look;
            ShowLook(look);
            return true;
        }, "Loading avatar…");
    }

    /// <summary>Puts a look on screen: worn items (with pictures), body colours and scales.</summary>
    private void ShowLook(AvatarState look)
    {
        WornItems.Clear();
        foreach (var w in look.Assets.Where(w => w.TypeId != 61))   // emotes are a separate thing
            WornItems.Add(new WardrobeItem { AssetId = w.Id, Name = w.Name, TypeId = w.TypeId, IsWorn = true, IsAdjustable = CanAdjust(w.TypeId) });
        if (AdjustItem != null && !look.Assets.Any(x => x.Id == AdjustItem.Id)) AdjustItem = null;   // taken off: close its panel
        _ = FillItemPicturesAsync(WornItems.ToList());
        var worn = look.Assets.Select(w => w.Id).ToHashSet();
        foreach (var i in _wardrobeAll) i.IsWorn = worn.Contains(i.AssetId);   // all loaded items, also ones filtered out

        BodyParts.Clear();
        foreach (var (key, name) in PartNames)
            BodyParts.Add(new BodyPartColor { Key = key, Name = name, Hex = look.Colors.TryGetValue(key, out var h) ? h : "A3A2A5", IsSelected = key == _selectedPart });

        _suppressScale = true;
        ScaleHeight = look.Scales.GetValueOrDefault("height", 1);
        ScaleWidth = look.Scales.GetValueOrDefault("width", 1);
        ScaleHead = look.Scales.GetValueOrDefault("head", 1);
        ScaleProportion = look.Scales.GetValueOrDefault("proportion", 0);
        ScaleBodyType = look.Scales.GetValueOrDefault("bodyType", 0);
        _suppressScale = false;
        ScalesChanged = false;

        AvatarSummary = $"{look.Type} · wearing {WornItems.Count} item{(WornItems.Count == 1 ? "" : "s")}";
        OnPropertyChanged(nameof(IsR15));
    }

    private static async Task FillItemPicturesAsync(List<WardrobeItem> items)
    {
        try
        {
            var pics = await Thumbnails.GetAsync("Asset", items.Where(i => i.ThumbUrl.Length == 0).Select(i => i.AssetId), "150x150");
            foreach (var i in items) if (pics.TryGetValue(i.AssetId, out var url)) i.ThumbUrl = url;
        }
        catch { }
    }

    // ---- the preview: a picture, or a 3D model you can turn

    [ObservableProperty] private Avatar3D? avatar3DModel;
    /// <summary>Show the turnable 3D model instead of the picture. Off at first: the picture is fast, 3D loads when asked.</summary>
    [ObservableProperty] private bool show3D;
    [ObservableProperty] private string preview3DNote = "";
    /// <summary>The look changed and Roblox is drawing the new one.</summary>
    [ObservableProperty] private bool previewUpdating;
    private int _previewGeneration;

    /// <summary>The new picture arrived before the new 3D model: show the picture until the model catches up.</summary>
    [ObservableProperty] private bool pictureIsNewer;

    public bool Showing3D => Show3D && Avatar3DModel != null && !PictureIsNewer;
    partial void OnAvatar3DModelChanged(Avatar3D? value) { PictureIsNewer = false; OnPropertyChanged(nameof(Showing3D)); }
    partial void OnShow3DChanged(bool value)
    {
        OnPropertyChanged(nameof(Showing3D));
        if (value) _ = Open3DAsync();
    }
    partial void OnPictureIsNewerChanged(bool value) => OnPropertyChanged(nameof(Showing3D));

    private async Task LoadPictureAsync(Account a)
    {
        try
        {
            var url = await RobloxAvatar.GetPictureAsync(a.UserId, false, CancellationToken.None);
            if (a == CurrentAccount && url.Length > 0) AvatarPicture = url;
        }
        catch { }   // the page works without it; the next change brings a fresh one
    }

    /// <summary>The 3D model doesn't match the look anymore (a change was made while showing the picture).</summary>
    private bool _model3DStale = true;

    /// <summary>3D is only loaded when you look at it: it's the slowest part, and its service is easily rate-limited.</summary>
    private async Task Open3DAsync()
    {
        if (CurrentAccount is not { } a || !_model3DStale) return;
        _model3DStale = false;
        Preview3DNote = "Loading 3D…";
        await Load3DAsync(a, null, _previewGeneration);
        if (Avatar3DModel != null) Preview3DNote = "";
    }

    private async Task Load3DAsync(Account a, string? previous, int generation)
    {
        if (string.IsNullOrEmpty(a.Cookie)) return;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var model = await RobloxAvatar3D.LoadAsync(a.Cookie, a.UserId, previous, CancellationToken.None);
                if (generation != _previewGeneration || a != CurrentAccount) return;
                if (model != null) { Avatar3DModel = model; Preview3DNote = ""; }
                else if (Avatar3DModel == null) Preview3DNote = "3D isn't ready yet; showing the picture.";
                return;
            }
            catch (MissingTexturesException ex)
            {
                // never show a grey model: keep the last good one (or the picture) and try again in a moment
                if (generation != _previewGeneration) return;
                if (attempt == 2)
                {
                    if (Avatar3DModel == null) Preview3DNote = "3D textures didn't load; showing the picture.";
                    Log(ActivityLog.Error, "3D avatar: " + ex.Message);
                    return;
                }
                previous = null;   // the same model is fine, its textures just weren't there yet
                await Task.Delay(1500);
            }
            catch (Exception ex)
            {
                if (generation != _previewGeneration) return;
                if (Avatar3DModel == null) Preview3DNote = "3D view unavailable; showing the picture.";
                Log(ActivityLog.Error, "3D avatar: " + ex.Message);
                return;
            }
        }
    }

    /// <summary>
    /// After a change: Roblox keeps serving the old picture / model until the new one is drawn, so wait for ones that
    /// differ from what's on screen. A newer change makes an older wait stop (generation).
    /// </summary>
    private async Task RefreshPreviewAsync(Account a)
    {
        int generation = ++_previewGeneration;
        PreviewUpdating = true;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double pictureAt = -1, modelAt = -1;
        try
        {
            _ = RobloxAvatar.RedrawAsync(a.Cookie);   // a nudge; Roblox redraws after a change anyway, so don't wait for it
            var oldModel = Avatar3DModel;
            var pic = RobloxAvatar.WaitForNewPictureAsync(a.UserId, AvatarPicture, CancellationToken.None);
            Task model;
            if (Show3D) { _model3DStale = false; model = Load3DAsync(a, oldModel?.SourceUrl, generation); }
            else { _model3DStale = true; model = Task.CompletedTask; }   // loaded when you switch to 3D

            var url = await pic;
            pictureAt = clock.Elapsed.TotalSeconds;
            if (generation == _previewGeneration && a == CurrentAccount && url.Length > 0 && url != AvatarPicture)
            {
                AvatarPicture = url;
                // the 3D model is still the old look: show the new picture meanwhile
                if (Avatar3DModel == oldModel && Show3D) PictureIsNewer = true;
            }
            await model;
            if (Avatar3DModel != oldModel) modelAt = clock.Elapsed.TotalSeconds;
        }
        catch { }
        finally
        {
            if (generation == _previewGeneration)
            {
                PreviewUpdating = false;
                PictureIsNewer = false;
                Log(ActivityLog.Info, "Avatar preview: picture after " + (pictureAt < 0 ? "–" : $"{pictureAt:0.0} s")
                                      + (Show3D ? ", 3D after " + (modelAt < 0 ? "– (kept the last one)" : $"{modelAt:0.0} s") : "") + ".");
            }
        }
    }

    [RelayCommand]
    private void TogglePreview3D() => Show3D = !Show3D;

    /// <summary>Runs one change as the account top right, with the busy indicator and a readable message on failure.</summary>
    private async Task<bool> ChangeAvatarAsync(Func<Account, Task> change, string? done = null)
    {
        if (CurrentAccount is not { } a || AvatarLook == null) return false;
        if (string.IsNullOrEmpty(a.Cookie)) { StatusMessage = "This account is signed out."; return false; }
        AvatarBusy = true;
        try
        {
            await change(a);
            if (done != null) StatusMessage = done;
            _ = RefreshPreviewAsync(a);
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = "Couldn't change the avatar: " + ex.Message;
            return false;
        }
        finally { AvatarBusy = false; }
    }

    // ================================================================== wardrobe

    public ObservableCollection<WardrobeItem> WardrobeItems { get; } = new();
    public ObservableCollection<WardrobeItem> WornItems { get; } = new();
    public Loader WardrobeLoad { get; } = new();
    [ObservableProperty] private string wardrobeCategory = "hats";
    [ObservableProperty] private string wardrobeFilter = "";
    [ObservableProperty] private bool hasMoreWardrobe;

    private readonly Dictionary<int, string?> _wardrobeCursors = new();
    private List<WardrobeItem> _wardrobeAll = new();

    public ObservableCollection<ChartChip> WardrobeChips { get; } =
        new(WardrobeCategories.Select(c => new ChartChip { Id = c.Key, Name = c.Name, IsSelected = c.Key == "hats" }));

    [RelayCommand]
    private void SelectWardrobeCategory(string? key) { if (!string.IsNullOrEmpty(key)) WardrobeCategory = key; }

    partial void OnWardrobeCategoryChanged(string value)
    {
        foreach (var c in WardrobeChips) c.IsSelected = c.Id == value;
        _ = LoadWardrobeAsync();
    }
    partial void OnWardrobeFilterChanged(string value) => ShowWardrobe();
    partial void OnAvatarTabChanged(string value)
    {
        if (Page == "Avatar") RecordNav();
        if (value == "Outfits" && _outfitsLoadedFor != CurrentAccount) _ = LoadOutfitsAsync();
    }

    private async Task LoadWardrobeAsync(bool more = false)
    {
        if (CurrentAccount is not { } a || string.IsNullOrEmpty(a.Cookie)) return;
        var cat = WardrobeCategories.FirstOrDefault(c => c.Key == WardrobeCategory) ?? WardrobeCategories[0];
        if (!more) { _wardrobeCursors.Clear(); _wardrobeAll = new(); WardrobeItems.Clear(); }

        await WardrobeLoad.RunAsync(async ct =>
        {
            var types = more ? cat.Types.Where(t => _wardrobeCursors.TryGetValue(t, out var c) && c != null).ToArray() : cat.Types;
            var pages = await Task.WhenAll(types.Select(async t =>
            {
                _wardrobeCursors.TryGetValue(t, out var cursor);
                var (items, next) = await RobloxAvatar.GetInventoryAsync(a.Cookie, a.UserId, t, more ? cursor : null, ct);
                return (Type: t, Items: items, Next: next);
            }));
            if (a != CurrentAccount || ct.IsCancellationRequested) return false;
            foreach (var p in pages) _wardrobeCursors[p.Type] = p.Next;
            var worn = AvatarLook?.Assets.Select(w => w.Id).ToHashSet() ?? new HashSet<long>();
            foreach (var i in pages.SelectMany(p => p.Items))
            {
                if (_wardrobeAll.Any(x => x.AssetId == i.AssetId)) continue;
                i.IsWorn = worn.Contains(i.AssetId);
                _wardrobeAll.Add(i);
            }
            HasMoreWardrobe = _wardrobeCursors.Values.Any(c => c != null);
            ShowWardrobe();
            return _wardrobeAll.Count > 0;
        }, "Loading your items…");
    }

    [RelayCommand]
    private Task LoadMoreWardrobe() => LoadWardrobeAsync(more: true);

    private void ShowWardrobe()
    {
        var q = (WardrobeFilter ?? "").Trim();
        WardrobeItems.Clear();
        foreach (var i in _wardrobeAll.Where(i => q.Length == 0 || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)))
            WardrobeItems.Add(i);
        _ = FillItemPicturesAsync(WardrobeItems.ToList());
    }

    // Clicks change the look on screen at once; the save to Roblox waits 0.25 s for more clicks, then sends them together.
    private readonly System.Windows.Threading.DispatcherTimer _wearSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly SemaphoreSlim _wearSaveGate = new(1);
    private List<WornAsset>? _lastSavedAssets;
    private bool _wearTimerHooked;

    /// <summary>Hats, hair, face, neck, shoulder, front, back and waist accessories: worn in any mix, 10 at most in total
    /// (Roblox's own limit; its per-type numbers are only what its editor suggests).</summary>
    private static readonly HashSet<int> AccessoryTypes = new() { 8, 41, 42, 43, 44, 45, 46, 47 };
    private const int MaxAccessories = 10;

    /// <summary>Click on an item: put it on, or take it off if it's worn. Accessories in any mix up to 10, other things one per kind.</summary>
    [RelayCommand]
    private void ToggleWear(WardrobeItem? item)
    {
        if (item == null || AvatarLook is not { } look || CurrentAccount is not { } a) return;
        _lastSavedAssets ??= look.Assets.ToList();   // what Roblox has, to go back to if it refuses
        var list = look.Assets.ToList();
        if (list.Any(w => w.Id == item.AssetId))
        {
            list.RemoveAll(w => w.Id == item.AssetId);
            StatusMessage = $"Took off {item.Name}.";
        }
        else
        {
            if (AccessoryTypes.Contains(item.TypeId))
            {
                // accessories: any mix (several hairs, several face items…), up to Roblox's 10 in total
                var acc = list.Where(w => AccessoryTypes.Contains(w.TypeId)).ToList();
                while (acc.Count >= MaxAccessories) { list.Remove(acc[0]); acc.RemoveAt(0); }   // the oldest accessory makes room
            }
            else
            {
                // everything else: one of a kind (a second shirt or walk animation can't be worn)
                int max = _rules != null && _rules.Wearable.TryGetValue(item.TypeId, out var r) ? Math.Max(1, r.Max) : 1;
                var same = list.Where(w => w.TypeId == item.TypeId).ToList();
                while (same.Count >= max) { list.Remove(same[0]); same.RemoveAt(0); }
            }
            list.Add(new WornAsset { Id = item.AssetId, Name = item.Name, TypeId = item.TypeId });
            StatusMessage = $"Put on {item.Name}.";
        }
        look.Assets = list;
        ShowLook(look);          // instant: ticks, "Wearing" row
        PreviewUpdating = true;

        if (!_wearTimerHooked) { _wearSaveTimer.Tick += async (_, _) => { _wearSaveTimer.Stop(); await SaveWearingAsync(); }; _wearTimerHooked = true; }
        _wearSaveTimer.Stop();
        _wearSaveTimer.Start();
    }

    private async Task SaveWearingAsync()
    {
        if (AvatarLook is not { } look || CurrentAccount is not { } a) return;
        await _wearSaveGate.WaitAsync();
        try
        {
            var wanted = look.Assets.ToList();
            var refused = await RobloxAvatar.SetWearingAsync(a.Cookie, wanted);
            if (a != CurrentAccount) return;
            if (refused.Count > 0)
            {
                look.Assets = look.Assets.Where(w => !refused.Contains(w.Id)).ToList();
                ShowLook(look);
                var names = wanted.Where(w => refused.Contains(w.Id)).Select(w => w.Name).ToList();
                StatusMessage = names.Count == 1 ? $"Roblox didn't allow {names[0]} with this look." : $"Roblox didn't allow {names.Count} of those items.";
            }
            _lastSavedAssets = null;
            _ = RefreshPreviewAsync(a);
        }
        catch (Exception ex)
        {
            // put back what Roblox actually has
            if (_lastSavedAssets != null) { look.Assets = _lastSavedAssets; ShowLook(look); }
            _lastSavedAssets = null;
            PreviewUpdating = false;
            StatusMessage = "Couldn't change the avatar: " + ex.Message;
        }
        finally { _wearSaveGate.Release(); }
    }

    [RelayCommand]
    private void TakeOff(WardrobeItem? item) => ToggleWear(item);

    // ================================================================== body: type, colours, scales

    private static readonly (string Key, string Name)[] PartNames =
    {
        ("headColor3", "Head"), ("torsoColor3", "Torso"), ("leftArmColor3", "Left arm"),
        ("rightArmColor3", "Right arm"), ("leftLegColor3", "Left leg"), ("rightLegColor3", "Right leg")
    };

    public ObservableCollection<BodyPartColor> BodyParts { get; } = new();
    public IReadOnlyList<PaletteColor> SkinPalette => _rules?.Palette ?? new List<PaletteColor>();
    private string _selectedPart = "all";
    [ObservableProperty] private bool allPartsSelected = true;

    [RelayCommand]
    private void SelectBodyPart(string? key)
    {
        _selectedPart = string.IsNullOrEmpty(key) ? "all" : key;
        AllPartsSelected = _selectedPart == "all";
        foreach (var p in BodyParts) p.IsSelected = p.Key == _selectedPart;
    }

    [RelayCommand]
    private async Task PickSkinColorAsync(string? hex)
    {
        if (string.IsNullOrEmpty(hex) || AvatarLook is not { } look) return;
        var colors = new Dictionary<string, string>(look.Colors);
        foreach (var (key, _) in PartNames)
            if (_selectedPart == "all" || key == _selectedPart) colors[key] = hex;
        string which = _selectedPart == "all" ? "the whole body" : PartNames.First(p => p.Key == _selectedPart).Name.ToLowerInvariant();
        if (await ChangeAvatarAsync(a => RobloxAvatar.SetColorsAsync(a.Cookie, colors), $"Changed the colour of {which}."))
        {
            look.Colors = colors;
            foreach (var p in BodyParts) if (colors.TryGetValue(p.Key, out var h)) p.Hex = h;
        }
    }

    [RelayCommand]
    private async Task SetAvatarTypeAsync(string? type)
    {
        if (type is not ("R6" or "R15") || AvatarLook is not { } look || look.Type == type) return;
        if (await ChangeAvatarAsync(a => RobloxAvatar.SetTypeAsync(a.Cookie, type), $"Switched to {type}."))
        {
            look.Type = type;
            ShowLook(look);
        }
    }

    // scales (R15 only); the sliders change locally, "Apply" sends them
    [ObservableProperty] private double scaleHeight = 1;
    [ObservableProperty] private double scaleWidth = 1;
    [ObservableProperty] private double scaleHead = 1;
    [ObservableProperty] private double scaleProportion;
    [ObservableProperty] private double scaleBodyType;
    [ObservableProperty] private bool scalesChanged;
    private bool _suppressScale;

    partial void OnScaleHeightChanged(double value) => MarkScales();
    partial void OnScaleWidthChanged(double value) => MarkScales();
    partial void OnScaleHeadChanged(double value) => MarkScales();
    partial void OnScaleProportionChanged(double value) => MarkScales();
    partial void OnScaleBodyTypeChanged(double value) => MarkScales();
    private void MarkScales() { if (!_suppressScale) ScalesChanged = true; }

    // slider limits from Roblox's rules
    private double Lim(string k, bool max) => _rules?.Scales.TryGetValue(k, out var s) == true ? (max ? s.Max : s.Min) : (max ? 1 : 0);
    public double HeightMin => Lim("height", false); public double HeightMax => Lim("height", true);
    public double WidthMin => Lim("width", false); public double WidthMax => Lim("width", true);
    public double HeadMin => Lim("head", false); public double HeadMax => Lim("head", true);

    private void NotifyRules()
    {
        foreach (var n in new[] { nameof(HeightMin), nameof(HeightMax), nameof(WidthMin), nameof(WidthMax), nameof(HeadMin), nameof(HeadMax), nameof(SkinPalette) })
            OnPropertyChanged(n);
    }

    [RelayCommand]
    private async Task ApplyScalesAsync()
    {
        if (AvatarLook is not { } look) return;
        var scales = new Dictionary<string, double>(look.Scales)
        {
            ["height"] = ScaleHeight, ["width"] = ScaleWidth, ["head"] = ScaleHead,
            ["proportion"] = ScaleProportion, ["bodyType"] = ScaleBodyType
        };
        if (!scales.ContainsKey("depth")) scales["depth"] = 1;
        if (await ChangeAvatarAsync(a => RobloxAvatar.SetScalesAsync(a.Cookie, scales), "Saved the body scales."))
        {
            look.Scales = scales;
            ScalesChanged = false;
        }
    }

    [RelayCommand]
    private void ResetScales()
    {
        if (AvatarLook is { } look) ShowLook(look);
    }

    // ================================================================== outfits

    public ObservableCollection<OutfitInfo> Outfits { get; } = new();
    public Loader OutfitsLoad { get; } = new();
    private Account? _outfitsLoadedFor;

    private Task LoadOutfitsAsync()
    {
        var a = CurrentAccount;
        _outfitsLoadedFor = a;
        Outfits.Clear();
        if (a == null) return Task.CompletedTask;
        return OutfitsLoad.RunAsync(async ct =>
        {
            var list = await RobloxAvatar.GetOutfitsAsync(a.UserId, ct);
            if (a != CurrentAccount) return false;
            foreach (var o in list) Outfits.Add(o);
            return list.Count > 0;
        }, "Loading outfits…");
    }

    [RelayCommand]
    private async Task WearOutfitAsync(OutfitInfo? o)
    {
        if (o == null || AvatarBusy) return;
        AvatarState? look = null;
        List<long> refused = new();
        bool ok = await ChangeAvatarAsync(async a =>
        {
            look = await RobloxAvatar.GetOutfitAsync(o.Id, CancellationToken.None);
            refused = await RobloxAvatar.ApplyLookAsync(a.Cookie, look);
        }, $"Wearing {o.Name}.");
        if (!ok || look == null) return;
        look.Assets = look.Assets.Where(w => !refused.Contains(w.Id)).ToList();
        AvatarLook = look;
        ShowLook(look);
        if (refused.Count > 0) StatusMessage = $"Wearing {o.Name}, without {refused.Count} item{(refused.Count == 1 ? "" : "s")} this account doesn't own anymore.";
    }

    [RelayCommand]
    private async Task SaveOutfitAsync()
    {
        if (AvatarLook is not { } look) return;
        var name = PromptDialog.Ask(Application.Current.MainWindow, "Save outfit", "Name for this outfit:", "My outfit");
        if (name == null) return;
        if (await ChangeAvatarAsync(a => RobloxAvatar.CreateOutfitAsync(a.Cookie, name, look), $"Saved the outfit {name}."))
            await LoadOutfitsAsync();
    }

    /// <summary>Called by the page after the name was typed.</summary>
    public async Task RenameOutfitAsync(OutfitInfo o, string name)
    {
        if (CurrentAccount is not { } a) return;
        try
        {
            await RobloxAvatar.RenameOutfitAsync(a.Cookie, o.Id, name);
            o.Name = name;
            StatusMessage = $"Renamed the outfit to {name}.";
        }
        catch (Exception ex) { StatusMessage = "Couldn't rename it: " + ex.Message; }
    }

    /// <summary>Called by the page after the person confirmed.</summary>
    public async Task DeleteOutfitAsync(OutfitInfo o)
    {
        if (CurrentAccount is not { } a) return;
        try
        {
            await RobloxAvatar.DeleteOutfitAsync(a.Cookie, o.Id);
            Outfits.Remove(o);
            StatusMessage = $"Deleted the outfit {o.Name}.";
        }
        catch (Exception ex) { StatusMessage = "Couldn't delete it: " + ex.Message; }
    }

    // ================================================================== copy to another alt

    /// <summary>Puts this account's whole look on another of your accounts. Items it doesn't own are left out.</summary>
    public async Task CopyLookToAsync(Account target)
    {
        if (AvatarLook is not { } look || CurrentAccount is not { } from || target == from) return;
        if (string.IsNullOrEmpty(target.Cookie)) { StatusMessage = $"{target.Label} is signed out."; return; }
        AvatarBusy = true;
        try
        {
            var refused = await RobloxAvatar.ApplyLookAsync(target.Cookie, look);
            StatusMessage = refused.Count == 0
                ? $"{target.Label} now looks like {from.Label}."
                : $"{target.Label} now looks like {from.Label}, without {refused.Count} item{(refused.Count == 1 ? "" : "s")} it doesn't own.";
            Log(ActivityLog.Info, $"Copied {from.Label}'s look to {target.Label}" + (refused.Count > 0 ? $" ({refused.Count} items not owned)." : "."));
            Thumbnails.Forget("Avatar", target.UserId);
        }
        catch (Exception ex) { StatusMessage = $"Couldn't copy the look to {target.Label}: " + ex.Message; }
        finally { AvatarBusy = false; }
    }
}
