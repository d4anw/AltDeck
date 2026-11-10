using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoDeck.Services;

namespace RoDeck.ViewModels;

// On your own profile: edit your About text.
// Account settings (privacy, security…) open Roblox's own page, signed in as the account.
public partial class MainViewModel
{
    [ObservableProperty] private bool isEditingAbout;
    [ObservableProperty] private string aboutDraft = "";
    [ObservableProperty] private bool aboutBusy;

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
