using System.Collections.ObjectModel;
using System.Windows;
using AltDeck.Controls;
using AltDeck.Models;
using AltDeck.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AltDeck.ViewModels;

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
        AvatarLook = null;
        AvatarPicture = "";
        WornItems.Clear();
        WardrobeItems.Clear();
        Outfits.Clear();
        _outfitsLoadedFor = null;
        if (a == null || a.UserId == 0) { AvatarLoad.State = LoadState.Empty; return; }

        await AvatarLoad.RunAsync(async ct =>
        {
            if (_rules == null) { _rules = await RobloxAvatar.GetRulesAsync(ct); NotifyRules(); }
            var look = await RobloxAvatar.GetAvatarAsync(a.UserId, ct);
            if (a != CurrentAccount) return false;
            AvatarLook = look;
            ShowLook(look);
            AvatarPicture = await RobloxAvatar.GetPictureAsync(a.UserId, false, ct);
            return true;
        }, "Loading avatar…");

        if (AvatarLook != null)
        {
            if (AvatarTab == "Outfits") _ = LoadOutfitsAsync();
            else _ = LoadWardrobeAsync();
        }
    }

    /// <summary>Puts a look on screen: worn items (with pictures), body colours and scales.</summary>
    private void ShowLook(AvatarState look)
    {
        WornItems.Clear();
        foreach (var w in look.Assets.Where(w => w.TypeId != 61))   // emotes are a separate thing
            WornItems.Add(new WardrobeItem { AssetId = w.Id, Name = w.Name, TypeId = w.TypeId, IsWorn = true });
        _ = FillItemPicturesAsync(WornItems.ToList());
        var worn = look.Assets.Select(w => w.Id).ToHashSet();
        foreach (var i in WardrobeItems) i.IsWorn = worn.Contains(i.AssetId);

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

    /// <summary>After a change: tell Roblox to draw the new look, then show the new picture when it's ready.</summary>
    private async Task RefreshPictureAsync(Account a)
    {
        await RobloxAvatar.RedrawAsync(a.Cookie);
        await Task.Delay(1500);
        if (a != CurrentAccount) return;
        var url = await RobloxAvatar.GetPictureAsync(a.UserId, true, CancellationToken.None);
        if (a == CurrentAccount && url.Length > 0) AvatarPicture = url;
    }

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
            _ = RefreshPictureAsync(a);
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

    /// <summary>Click on an item: put it on, or take it off if it's worn. Respects Roblox's limits (3 hats, 1 hair…).</summary>
    [RelayCommand]
    private async Task ToggleWearAsync(WardrobeItem? item)
    {
        if (item == null || AvatarLook is not { } look || AvatarBusy) return;
        var list = look.Assets.ToList();
        string done;
        if (list.Any(w => w.Id == item.AssetId))
        {
            list.RemoveAll(w => w.Id == item.AssetId);
            done = $"Took off {item.Name}.";
        }
        else
        {
            int max = _rules != null && _rules.Wearable.TryGetValue(item.TypeId, out var r) ? Math.Max(1, r.Max) : 1;
            var same = list.Where(w => w.TypeId == item.TypeId).ToList();
            while (same.Count >= max) { list.Remove(same[0]); same.RemoveAt(0); }   // the oldest of that kind makes room
            list.Add(new WornAsset { Id = item.AssetId, Name = item.Name, TypeId = item.TypeId });
            done = $"Put on {item.Name}.";
        }

        item.IsBusy = true;
        List<long> refused = new();
        bool ok = await ChangeAvatarAsync(async a => refused = await RobloxAvatar.SetWearingAsync(a.Cookie, list), done);
        item.IsBusy = false;
        if (!ok) return;
        look.Assets = list.Where(w => !refused.Contains(w.Id)).ToList();
        ShowLook(look);
        if (refused.Count > 0) StatusMessage = refused.Contains(item.AssetId)
            ? $"Roblox didn't allow {item.Name} on this avatar."
            : "Done, but Roblox took off something that didn't fit with it.";
    }

    [RelayCommand]
    private Task TakeOffAsync(WardrobeItem? item) => item == null ? Task.CompletedTask : ToggleWearAsync(item);

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
