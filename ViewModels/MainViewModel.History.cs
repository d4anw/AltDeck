using System.Windows.Threading;
using RoDeck.Models;
using RoDeck.Services;

namespace RoDeck.ViewModels;

/// <summary>
/// Back / forward like a browser, on every page: the mouse's side buttons and Alt+← / Alt+→.
///
/// How it works: a "place" is the page plus what's open on it (tab, game, profile, item, trade…), identified by IDs.
/// Every change during one click is collected and saved as ONE step once the click is done (so "switch tab and open an
/// item" is one step, not two). Back goes to the previous different place and opens it again exactly. If a confirm card
/// or the trade maker is open, back closes that first.
/// </summary>
public partial class MainViewModel
{
    private sealed record NavState(
        string Key, string Page, string MultiTab, string FriendsTab, string AvatarTab, string TradesTab, string TradeType,
        GameTile? Game, long ProfileId, MarketItem? Item, LimitedTile? Limited, TradeRow? Trade, string SettingsSection = "",
        string GamesView = "", string GameSearch = "");

    private readonly NavHistory<NavState> _history = new(s => s.Key);
    private bool _restoring, _commitPending;

    /// <summary>The place on screen right now.</summary>
    private NavState CaptureNav()
    {
        var game = Page == "Games" && IsGameOpen ? _lastOpened : null;
        long profile = Page == "Friends" && IsProfileOpen ? _lastProfileId : 0;
        var item = Page == "Marketplace" && IsMarketItemOpen ? _lastMarketItem : null;
        var limited = Page == "Trades" && TradesTab == "Limiteds" ? OpenLimited : null;
        var trade = Page == "Trades" && TradesTab == "Trades" ? OpenTradeRow : null;

        string sub = Page switch
        {
            "Multi" => MultiTab,
            "Games" => (GamesView.Length > 0 ? GamesView : "charts") + (GamesView == "search" ? $" {GameSearchText}" : "")
                       + (game != null ? $" game {game.UniverseId}" : ""),
            "Friends" => FriendsTab + (profile > 0 ? $" profile {profile}" : ""),
            "Avatar" => AvatarTab,
            "Settings" => SettingsSection,
            "Marketplace" => item != null ? $"item {item.ItemType} {item.Id}" : "",
            "Trades" => TradesTab
                        + (limited != null ? $" limited {limited.AssetId}" : "")
                        + (TradesTab == "Trades" ? $" {TradeType}" + (trade != null ? $" trade {trade.Id}" : "") : "")
                        + (TradesTab == "Profile" ? $" {TradeProfileId}" : ""),
            _ => ""
        };
        return new NavState($"{Page}|{sub}", Page, MultiTab, FriendsTab, AvatarTab, TradesTab, TradeType,
                            game, profile, item, limited, trade, SettingsSection, GamesView, GameSearchText ?? "");
    }

    /// <summary>
    /// Something navigational changed (page, tab, something opened or closed). Saved as one step once the current
    /// click / key press is fully handled, so a click that changes several things is one step.
    /// </summary>
    private void RecordNav()
    {
        if (_restoring || _commitPending) return;
        _commitPending = true;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(CommitNav));
    }

    private void CommitNav()
    {
        _commitPending = false;
        if (_restoring) return;
        _history.Record(CaptureNav());
    }

    // the things that count as moving somewhere
    partial void OnMultiTabChanged(string value) { if (Page == "Multi") RecordNav(); }
    partial void OnIsGameOpenChanged(bool value) => RecordNav();
    partial void OnIsProfileOpenChanged(bool value) => RecordNav();
    partial void OnFriendsTabChanged(string value) { if (Page == "Friends") RecordNav(); }

    public void GoBack() => Go(back: true);
    public void GoForward() => Go(back: false);

    private void Go(bool back)
    {
        // a card or panel on top: back closes it (it's what you're looking at), forward does nothing
        if (back && CloseOverlay()) return;

        if (_restoring) FinishRestore();          // pressed again quickly: the last step is done, carry on from it
        _commitPending = false;                   // CaptureNav below sees the latest state anyway
        var now = CaptureNav();
        var target = back ? _history.Back(now) : _history.Forward(now);
        if (target != null) Restore(target);
    }

    /// <summary>Closes a confirm card or the trade maker. True when something was closed.</summary>
    private bool CloseOverlay()
    {
        if (ShowTradeConfirm) { if (!TradeBusy) ShowTradeConfirm = false; return true; }
        if (ShowBuyConfirm) { if (!BuyBusy) ShowBuyConfirm = false; return true; }
        if (ShowInstaBuyWarning) { ShowInstaBuyWarning = false; return true; }
        if (ShowAutoBuyWarning) { CancelAutoBuy(); return true; }
        if (ShowDealBuy) { if (!DealBuyBusy) { ShowDealBuy = false; DealBuyDeal = null; } return true; }
        if (IsCalcPickerOpen && Page == "Trades") { IsCalcPickerOpen = false; return true; }
        if (IsAdPickerOpen && Page == "Trades") { IsAdPickerOpen = false; return true; }
        if (IsBuildingTrade && Page == "Trades") { IsBuildingTrade = false; return true; }
        return false;
    }

    private void Restore(NavState s)
    {
        _restoring = true;
        ShowBuyConfirm = false;
        if (!TradeBusy) ShowTradeConfirm = false;
        IsBuildingTrade = false;

        Page = s.Page;
        switch (s.Page)
        {
            case "Multi":
                MultiTab = s.MultiTab;
                break;

            case "Games":
                if (s.GamesView != GamesView || (s.GamesView == "search" && s.GameSearch != GameSearchText))
                {
                    if (s.GamesView.Length == 0) ShowGameRows();
                    else if (s.GamesView is "favourites" or "recent") ShowSaved(s.GamesView);
                    else if (s.GamesView == "search") { GameSearchText = s.GameSearch; _ = SearchGamesAsync(); }
                    else _ = SelectChart(s.GamesView);
                }
                if (s.Game != null) { if (!IsGameOpen || _lastOpened?.UniverseId != s.Game.UniverseId) _ = OpenGameAsync(s.Game); }
                else if (IsGameOpen) CloseGame();
                break;

            case "Friends":
                FriendsTab = s.FriendsTab;
                if (s.ProfileId > 0) { if (!IsProfileOpen || _lastProfileId != s.ProfileId) _ = OpenProfileAsync(s.ProfileId); }
                else if (IsProfileOpen) CloseProfile();
                break;

            case "Avatar":
                AvatarTab = s.AvatarTab;
                break;

            case "Settings":
                if (s.SettingsSection.Length > 0) SettingsSection = s.SettingsSection;
                break;

            case "Marketplace":
                if (s.Item != null)
                {
                    if (!IsMarketItemOpen || _lastMarketItem?.Id != s.Item.Id || _lastMarketItem.ItemType != s.Item.ItemType)
                        _ = OpenMarketItemAsync(s.Item);
                }
                else if (IsMarketItemOpen) CloseMarketItem();
                break;

            case "Trades":
                if (s.TradesTab == "Profile" && long.TryParse(s.Key.Split(' ')[^1], out var pid) && pid != TradeProfileId && CurrentAccount is { } pa)
                    _ = ShowTradeProfileAsync(pa, pid, "", "", "");
                TradesTab = s.TradesTab;
                if (s.TradesTab == "Limiteds")
                {
                    if (s.Limited != null) { if (OpenLimited?.AssetId != s.Limited.AssetId) _ = OpenLimitedTile(s.Limited); }
                    else if (OpenLimited != null) OpenLimited = null;
                }
                if (s.TradesTab == "Trades")
                {
                    if (TradeType != s.TradeType) TradeType = s.TradeType;   // reloads that list
                    if (s.Trade != null)
                    {
                        if (OpenTradeRow?.Id != s.Trade.Id)
                            _ = OpenTradeRowItem(TradeRows.FirstOrDefault(r => r.Id == s.Trade.Id) ?? s.Trade);
                    }
                    else if (OpenTradeRow != null) CloseTrade();
                }
                // the item page's own "Back to …" button follows the history from here on
                _limitedFrom = "";
                OnPropertyChanged(nameof(LimitedBackText));
                break;
        }

        // let everything the step started settle, then note where we really are
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(FinishRestore));
    }

    private void FinishRestore()
    {
        if (!_restoring) return;
        _restoring = false;
        _commitPending = false;
        _history.Settle(CaptureNav());
    }
}
