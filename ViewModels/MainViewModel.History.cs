using AltDeck.Models;

namespace AltDeck.ViewModels;

/// <summary>
/// Back / forward like a browser: the mouse's side buttons and Alt+← / Alt+→ move through the pages, Multi-launch tabs
/// game pages, profiles and Marketplace items you visited.
/// </summary>
public partial class MainViewModel
{
    private sealed record NavState(string Page, string MultiTab, GameTile? Game, long Profile = 0, MarketItem? Item = null,
                                   string TradesTab = "", LimitedTile? Limited = null, TradeRow? Trade = null);

    private readonly Stack<NavState> _back = new();
    private readonly Stack<NavState> _forward = new();
    private NavState? _here;
    private bool _restoring;

    /// <summary>Called whenever the page, tab or open game changes.</summary>
    private void RecordNav()
    {
        if (_restoring) return;
        var now = new NavState(Page, MultiTab, IsGameOpen && Page == "Games" ? _lastOpened : null,
                               IsProfileOpen && Page == "Friends" ? _lastProfileId : 0,
                               IsMarketItemOpen && Page == "Marketplace" ? _lastMarketItem : null,
                               Page == "Trades" ? TradesTab : "",
                               Page == "Trades" && TradesTab == "Limiteds" ? OpenLimited : null,
                               Page == "Trades" && TradesTab == "Trades" ? OpenTradeRow : null);
        if (_here == now) return;
        if (_here != null)
        {
            _back.Push(_here);
            if (_back.Count > 50) TrimBack();
        }
        _forward.Clear();
        _here = now;
    }

    partial void OnMultiTabChanged(string value) { if (Page == "Multi") RecordNav(); }
    partial void OnIsGameOpenChanged(bool value) => RecordNav();
    partial void OnIsProfileOpenChanged(bool value) => RecordNav();

    public void GoBack()
    {
        if (_back.Count == 0) return;
        if (_here != null) _forward.Push(_here);
        Restore(_back.Pop());
    }

    public void GoForward()
    {
        if (_forward.Count == 0) return;
        if (_here != null) _back.Push(_here);
        Restore(_forward.Pop());
    }

    private void Restore(NavState s)
    {
        _restoring = true;
        try
        {
            MultiTab = s.MultiTab;
            Page = s.Page;
            if (s.Game != null)
            {
                if (!IsGameOpen || _lastOpened != s.Game) _ = OpenGameAsync(s.Game);
            }
            else if (IsGameOpen && s.Page == "Games") CloseGame();
            if (s.Profile > 0)
            {
                if (!IsProfileOpen || _lastProfileId != s.Profile) _ = OpenProfileAsync(s.Profile);
            }
            else if (IsProfileOpen && s.Page == "Friends") CloseProfile();
            if (s.Item != null)
            {
                if (!IsMarketItemOpen || _lastMarketItem != s.Item) _ = OpenMarketItemAsync(s.Item);
            }
            else if (IsMarketItemOpen && s.Page == "Marketplace") CloseMarketItem();
            if (s.Page == "Trades")
            {
                if (s.TradesTab.Length > 0) TradesTab = s.TradesTab;
                if (s.Limited != null) { if (OpenLimited != s.Limited) _ = OpenLimitedTile(s.Limited); }
                else if (OpenLimited != null) { OpenLimited = null; _limitedFrom = ""; OnPropertyChanged(nameof(LimitedBackText)); }
                if (s.Trade != null) { if (OpenTradeRow != s.Trade && TradeRows.Contains(s.Trade)) _ = OpenTradeRowItem(s.Trade); }
                else if (s.TradesTab == "Trades" && OpenTradeRow != null) CloseTrade();
            }
        }
        finally
        {
            _restoring = false;
            _here = s;
        }
    }

    private void TrimBack()
    {
        var keep = _back.Take(40).Reverse().ToList();
        _back.Clear();
        foreach (var k in keep) _back.Push(k);
    }
}
