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
        var who = p.DisplayName.Length > 0 ? p.DisplayName : p.Name;
        try
        {
            await RobloxGameExtras.SetBlockedAsync(a.Cookie, p.UserId, block, CancellationToken.None);
            ProfileBlocked = block;
            StatusMessage = block ? $"{a.Label} blocked {who}. They can't message, friend or join you anymore." : $"{a.Label} unblocked {who}.";
        }
        catch (RobloxException ex) when (!ex.TooMany && !ex.SignedOut)
        {
            // Roblox wants it done on its own site (a security check, or a rule for this account): its profile page,
            // signed in as the account, where the ⋯ menu has Block
            Log(ActivityLog.Info, $"{(block ? "Blocking" : "Unblocking")} {who} from RoDeck didn't go through ({(int?)ex.Status}: {ex.Message}); opened Roblox's page instead.");
            OpenRobloxPageRequested?.Invoke(a, $"https://www.roblox.com/users/{p.UserId}/profile", who);
            StatusMessage = $"Roblox wants this done on its own page: use the ⋯ menu next to {who}'s name there, then close the window.";
            _ = RecheckBlockLaterAsync(a, p.UserId);
        }
        catch (RobloxException ex) { StatusMessage = ex.Message; }
    }

    /// <summary>After blocking on Roblox's page: the button shows the new state once the window has been used.</summary>
    private async Task RecheckBlockLaterAsync(Models.Account a, long userId)
    {
        for (int i = 0; i < 12; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            if (OpenProfile?.UserId != userId || CurrentAccount != a) return;
            if (await RobloxGameExtras.IsBlockedAsync(a.Cookie, userId, CancellationToken.None) is bool b && b != ProfileBlocked)
            {
                ProfileBlocked = b;
                return;
            }
        }
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
            var saved = await RobloxGameExtras.SetAboutAsync(a.Cookie, a.UserId, (AboutDraft ?? "").Trim(), CancellationToken.None);
            IsEditingAbout = false;
            StatusMessage = saved.Contains('#') ? "Saved. Roblox filtered some words (the #s)." : "Your About text is saved.";
            await OpenProfileAsync(p.UserId);
        }
        catch (RobloxException ex) when (!ex.TooMany && !ex.SignedOut)
        {
            // a locked account PIN, a security check, or a rule for this account: Roblox's own profile page can do it
            Log(ActivityLog.Info, $"Saving the About text from RoDeck didn't go through ({(int?)ex.Status}: {ex.Message}); opened Roblox's page instead.");
            try { System.Windows.Clipboard.SetText((AboutDraft ?? "").Trim()); } catch { }
            OpenRobloxPageRequested?.Invoke(a, $"https://www.roblox.com/users/{a.UserId}/profile", "Your profile");
            StatusMessage = $"Roblox said: {ex.Message} Your text is copied: paste it in the About box on Roblox's page (the pencil).";
            IsEditingAbout = false;
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
