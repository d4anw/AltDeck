using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoDeck.Services;

namespace RoDeck.ViewModels;

// On a profile: block / unblock a player (asked twice), and on your own profile, edit your About text.
// Account settings (privacy, security…) open Roblox's own page, signed in as the account.
public partial class MainViewModel
{
    /// <summary>Whether the account top right has blocked the open profile (null: unknown yet).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ProfileBlockText))] private bool? profileBlocked;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ProfileBlockText))] private bool blockArmed;
    public string ProfileBlockText => ProfileBlocked == true ? "Unblock" : BlockArmed ? "Click again to block" : "Block";

    [ObservableProperty] private bool isEditingAbout;
    [ObservableProperty] private string aboutDraft = "";
    [ObservableProperty] private bool aboutBusy;

    /// <summary>Called when a profile opens: its block state, for someone else's profile.</summary>
    private async Task LoadBlockStateAsync(long userId)
    {
        ProfileBlocked = null;
        BlockArmed = false;
        IsEditingAbout = false;
        if (CurrentAccount is not { UserId: > 0 } a || a.UserId == userId) return;
        var blocked = await RobloxGameExtras.IsBlockedAsync(a.Cookie, userId, CancellationToken.None);
        if (OpenProfile?.UserId == userId) ProfileBlocked = blocked ?? false;
    }

    [RelayCommand]
    private async Task ToggleBlock()
    {
        if (OpenProfile is not { } p || CurrentAccount is not { UserId: > 0 } a || a.UserId == p.UserId) return;
        bool block = ProfileBlocked != true;
        if (block && !BlockArmed)
        {
            BlockArmed = true;
            _ = Task.Delay(4000).ContinueWith(_ => BlockArmed = false, TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }
        BlockArmed = false;
        try
        {
            await RobloxGameExtras.SetBlockedAsync(a.Cookie, p.UserId, block, CancellationToken.None);
            ProfileBlocked = block;
            var who = p.DisplayName.Length > 0 ? p.DisplayName : p.Name;
            StatusMessage = block ? $"{a.Label} blocked {who}. They can't message, friend or join you anymore." : $"{a.Label} unblocked {who}.";
        }
        catch (RobloxException ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    private void EditAbout()
    {
        if (!ProfileIsMe || OpenProfile is not { } p) return;
        AboutDraft = p.About;
        IsEditingAbout = true;
    }

    [RelayCommand] private void CancelAbout() { if (!AboutBusy) IsEditingAbout = false; }

    [RelayCommand]
    private async Task SaveAbout()
    {
        if (OpenProfile is not { } p || CurrentAccount is not { UserId: > 0 } a || a.UserId != p.UserId || AboutBusy) return;
        AboutBusy = true;
        try
        {
            var saved = await RobloxGameExtras.SetAboutAsync(a.Cookie, (AboutDraft ?? "").Trim(), CancellationToken.None);
            IsEditingAbout = false;
            StatusMessage = saved.Contains('#') ? "Saved. Roblox filtered some words (the #s)." : "Your About text is saved.";
            await OpenProfileAsync(p.UserId);
        }
        catch (RobloxException ex) { StatusMessage = ex.Message; }
        finally { AboutBusy = false; }
    }

    /// <summary>The account menu: this account's own profile.</summary>
    [RelayCommand]
    private Task OpenMyProfile() => CurrentAccount is { UserId: > 0 } a ? OpenProfileAsync(a.UserId) : Task.CompletedTask;

    /// <summary>The account menu: Roblox's settings page (privacy, security, parental controls…), signed in as the account.</summary>
    [RelayCommand]
    private void OpenRobloxSettings()
    {
        if (CurrentAccount is not { } a) { StatusMessage = "Pick an account top right first."; return; }
        OpenRobloxPageRequested?.Invoke(a, "https://www.roblox.com/my/account", "Roblox settings");
    }
}
