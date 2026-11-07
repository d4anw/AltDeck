using AltDeck.Models;

namespace AltDeck.ViewModels;

/// <summary>
/// Back / forward like a browser: the mouse's side buttons and Alt+← / Alt+→ move through the pages, Multi-launch tabs
/// and game pages you visited.
/// </summary>
public partial class MainViewModel
{
    private sealed record NavState(string Page, string MultiTab, GameTile? Game);

    private readonly Stack<NavState> _back = new();
    private readonly Stack<NavState> _forward = new();
    private NavState? _here;
    private bool _restoring;

    /// <summary>Called whenever the page, tab or open game changes.</summary>
    private void RecordNav()
    {
        if (_restoring) return;
        var now = new NavState(Page, MultiTab, IsGameOpen && Page == "Games" ? _lastOpened : null);
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
