using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AltDeck.Models;
using AltDeck.ViewModels;

namespace AltDeck.Views;

/// <summary>Home: who you are, friends online, Continue and Recommended games, shortcuts.</summary>
public partial class StartPage : UserControl
{
    public StartPage() { InitializeComponent(); }

    private MainViewModel VM => (MainViewModel)DataContext;

    // ---- the ‹ › buttons and Shift + wheel scroll a row sideways; the plain wheel keeps scrolling the page

    private void ScrollLeft_Click(object sender, RoutedEventArgs e) => Step((sender as FrameworkElement)?.Tag as ScrollViewer, -1);
    private void ScrollRight_Click(object sender, RoutedEventArgs e) => Step((sender as FrameworkElement)?.Tag as ScrollViewer, +1);

    private static void Step(ScrollViewer? row, int dir)
    {
        if (row == null) return;
        row.ScrollToHorizontalOffset(row.HorizontalOffset + dir * Math.Max(200, row.ViewportWidth * 0.8));
    }

    private void Row_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Shift || sender is not ScrollViewer row) return;
        row.ScrollToHorizontalOffset(row.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    // ---- right-click menus

    private void GameTile_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GameTile g) return;
        var who = VM.CurrentAccount?.Label ?? "this account";
        MenuHelper.Show((FrameworkElement)sender,
            ($"Play with {who}", () => VM.PlayGameCommand.Execute(g)),
            ("Play with several alts…", () => VM.PlayGameWithAltsCommand.Execute(g)),
            ("Open on roblox.com", () => OpenUrl($"https://www.roblox.com/games/{g.PlaceId}")));
        e.Handled = true;
    }

    private void Friend_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FriendTile f) return;
        var items = new List<(string, Action)>();
        if (f.CanJoin)
        {
            items.Add(($"Join with {VM.CurrentAccount?.Label ?? "this account"}", () => VM.JoinFriendCommand.Execute(f)));
            items.Add(("Join with several alts…", () => VM.JoinFriendWithAltsCommand.Execute(f)));
        }
        items.Add(("Open profile on roblox.com", () => OpenUrl($"https://www.roblox.com/users/{f.UserId}/profile")));
        MenuHelper.Show((FrameworkElement)sender, items.ToArray());
        e.Handled = true;
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
