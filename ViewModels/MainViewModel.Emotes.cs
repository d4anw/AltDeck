using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoDeck.Controls;
using RoDeck.Models;
using RoDeck.Services;

namespace RoDeck.ViewModels;

// Avatar → Emotes: the 8 slots of the emote wheel and the emotes the account owns. Pick a slot, then click an emote
// to put it there (or click an emote and it goes into the first empty slot).
public partial class MainViewModel
{
    public ObservableCollection<EmoteSlot> EmoteSlots { get; } = new(Enumerable.Range(1, 8).Select(p => new EmoteSlot { Position = p }));
    public ObservableCollection<WardrobeItem> OwnedEmotes { get; } = new();
    public Loader EmotesLoad { get; } = new();
    [ObservableProperty] private EmoteSlot? selectedEmoteSlot;
    [ObservableProperty] private bool emotesBusy;
    private Account? _emotesFor;

    partial void OnSelectedEmoteSlotChanged(EmoteSlot? value)
    {
        foreach (var s in EmoteSlots) s.IsSelected = s == value;
    }

    private void MaybeLoadEmotes()
    {
        if (Page != "Avatar" || AvatarTab != "Emotes" || _emotesFor == CurrentAccount) return;
        _ = LoadEmotesAsync();
    }

    [RelayCommand]
    private Task RefreshEmotes() => LoadEmotesAsync();

    private Task LoadEmotesAsync() => EmotesLoad.RunAsync(async ct =>
    {
        if (CurrentAccount is not { UserId: > 0 } a || string.IsNullOrEmpty(a.Cookie)) throw new RobloxException("Pick an account top right first.");
        var slotsTask = RobloxProfiles.GetEmoteSlotsAsync(a.Cookie, ct);
        var owned = new List<WardrobeItem>();
        string? cursor = null;
        for (int page = 0; page < 10; page++)
        {
            var (items, next) = await RobloxAvatar.GetInventoryAsync(a.Cookie, a.UserId, 61, cursor, ct);
            owned.AddRange(items.Where(i => owned.All(o => o.AssetId != i.AssetId)));
            if (next == null) break;
            cursor = next;
        }
        var slots = await slotsTask;
        if (a != CurrentAccount) return true;
        _emotesFor = a;
        for (int i = 0; i < 8; i++)
        {
            EmoteSlots[i].AssetId = slots[i].AssetId;
            EmoteSlots[i].Name = slots[i].Name;
            EmoteSlots[i].ThumbUrl = "";
        }
        OwnedEmotes.Clear();
        foreach (var e in owned) { e.IsWorn = EmoteSlots.Any(s => s.AssetId == e.AssetId); OwnedEmotes.Add(e); }
        SelectedEmoteSlot = EmoteSlots.FirstOrDefault(s => s.IsEmpty) ?? EmoteSlots[0];
        _ = FillEmotePicturesAsync();
        return true;
    }, "Loading your emotes…");

    private async Task FillEmotePicturesAsync()
    {
        try
        {
            var ids = OwnedEmotes.Select(e => e.AssetId).Concat(EmoteSlots.Where(s => !s.IsEmpty).Select(s => s.AssetId)).Distinct().ToList();
            if (ids.Count == 0) return;
            var pics = await Thumbnails.GetAsync("Asset", ids, "150x150");
            foreach (var e in OwnedEmotes) if (pics.TryGetValue(e.AssetId, out var u)) e.ThumbUrl = u;
            foreach (var s in EmoteSlots) if (pics.TryGetValue(s.AssetId, out var u)) s.ThumbUrl = u;
        }
        catch { }
    }

    [RelayCommand] private void SelectEmoteSlot(EmoteSlot? s) { if (s != null) SelectedEmoteSlot = s; }

    /// <summary>Puts the emote in the picked slot (an emote already on the wheel moves there).</summary>
    [RelayCommand]
    private async Task EquipEmote(WardrobeItem? e)
    {
        if (e == null || EmotesBusy || CurrentAccount is not { } a) return;
        var slot = SelectedEmoteSlot ?? EmoteSlots.FirstOrDefault(s => s.IsEmpty) ?? EmoteSlots[0];
        if (slot.AssetId == e.AssetId) return;
        EmotesBusy = true;
        e.IsBusy = true;
        try
        {
            // Roblox allows an emote once on the wheel: take it out of its old slot first
            if (EmoteSlots.FirstOrDefault(s => s.AssetId == e.AssetId) is { } old)
            {
                await RobloxProfiles.RemoveEmoteAsync(a.Cookie, old.Position, CancellationToken.None);
                old.AssetId = 0; old.Name = ""; old.ThumbUrl = "";
            }
            if (!slot.IsEmpty && OwnedEmotes.FirstOrDefault(o => o.AssetId == slot.AssetId) is { } replaced) replaced.IsWorn = false;
            await RobloxProfiles.SetEmoteAsync(a.Cookie, e.AssetId, slot.Position, CancellationToken.None);
            slot.AssetId = e.AssetId;
            slot.Name = e.Name;
            slot.ThumbUrl = e.ThumbUrl;
            e.IsWorn = true;
            StatusMessage = $"{e.Name} is in slot {slot.Position} now.";
            // the next empty slot is picked, so clicking emotes one after another fills the wheel
            SelectedEmoteSlot = EmoteSlots.FirstOrDefault(s => s.IsEmpty) ?? slot;
        }
        catch (RobloxException ex) { StatusMessage = ex.Message; }
        finally { e.IsBusy = false; EmotesBusy = false; }
    }

    [RelayCommand]
    private async Task RemoveEmote(EmoteSlot? slot)
    {
        if (slot is not { IsEmpty: false } || EmotesBusy || CurrentAccount is not { } a) return;
        EmotesBusy = true;
        try
        {
            await RobloxProfiles.RemoveEmoteAsync(a.Cookie, slot.Position, CancellationToken.None);
            if (OwnedEmotes.FirstOrDefault(o => o.AssetId == slot.AssetId) is { } e) e.IsWorn = false;
            StatusMessage = $"{slot.Name} is off the wheel.";
            slot.AssetId = 0; slot.Name = ""; slot.ThumbUrl = "";
            SelectedEmoteSlot = slot;
        }
        catch (RobloxException ex) { StatusMessage = ex.Message; }
        finally { EmotesBusy = false; }
    }
}
