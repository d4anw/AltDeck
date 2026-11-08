using System.Collections.Specialized;
using RoDeck.Models;
using RoDeck.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RoDeck.ViewModels;

/// <summary>
/// The app shell (0.5): the sidebar pages, the Multi-launch tabs and the account shown top right.
/// Pages: Home, Games, Avatar, Marketplace, Trades, Friends, Multi, Performance (FPS &amp; FastFlags), Settings.
/// Multi-launch tabs: Running (the old Home), Launch (the old Play), Accounts, Activity.
/// </summary>
public partial class MainViewModel
{
    [ObservableProperty] private string multiTab = "Running";

    /// <summary>Opens Multi-launch on one of its tabs.</summary>
    public void GoMulti(string tab)
    {
        MultiTab = tab;
        Page = "Multi";
    }

    partial void OnPageChanged(string value)
    {
        RecordNav();
        MaybeLoadHome();
        MaybeLoadGames();
        MaybeLoadFriends();
        MaybeLoadAvatar();
        MaybeLoadMarket();
        MaybeLoadTrades();
    }

    [RelayCommand] private void GoPage(string? page) => Page = string.IsNullOrEmpty(page) ? "Home" : page;
    [RelayCommand] private void GoMultiTab(string? tab) => GoMulti(string.IsNullOrEmpty(tab) ? "Running" : tab);

    // ------------------------------------------------------------------ the account top right

    /// <summary>The account the Home, Games, Avatar, Marketplace, Trades and Friends pages use.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentAccount))]
    private Account? currentAccount;

    public bool HasCurrentAccount => CurrentAccount != null;

    /// <summary>Raised when the current account changes, so pages can reload for it.</summary>
    public event Action<Account?>? CurrentAccountChanged;

    partial void OnCurrentAccountChanged(Account? value)
    {
        var id = value?.Id ?? "";
        if (Settings.CurrentAccountId != id) Settings.CurrentAccountId = id;
        CurrentAccountChanged?.Invoke(value);
        MaybeLoadHome();
        if (IsProfileOpen) CloseProfile();   // a profile is seen through one account; don't mix
        MaybeLoadFriends();
        MaybeLoadAvatar();
        MarketAccountChanged();
        TradesAccountChanged();
        _ = RefreshRequestCountAsync();
    }

    [RelayCommand]
    private void SwitchAccount(Account? a)
    {
        if (a == null || a == CurrentAccount) return;
        CurrentAccount = a;
        StatusMessage = $"Now using {a.Label}.";
    }

    /// <summary>Called once from the constructor, after the accounts are loaded.</summary>
    private void SetupShell()
    {
        ImageCache.Prune();
        CurrentAccount = Accounts.FirstOrDefault(a => a.Id == Settings.CurrentAccountId) ?? DefaultAccount();
        Accounts.CollectionChanged += OnAccountsChangedForShell;
        RecordNav();   // the first page is a step too, so back from the next page returns to it
        SetupNotifications();
    }

    private Account? DefaultAccount() =>
        Accounts.FirstOrDefault(a => a.IsFavourite && a.StatusKind != "signedout")
        ?? Accounts.FirstOrDefault(a => a.StatusKind != "signedout")
        ?? Accounts.FirstOrDefault();

    private void OnAccountsChangedForShell(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (CurrentAccount == null || !Accounts.Contains(CurrentAccount))
            CurrentAccount = DefaultAccount();
    }
}
