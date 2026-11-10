using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RoDeck.Models;
using RoDeck.ViewModels;

namespace RoDeck.Views;

/// <summary>Home: who you are, friends online, Continue and Recommended games, shortcuts.</summary>
public partial class StartPage : UserControl
{
    public StartPage()
    {
        InitializeComponent();
        // the banner row: a bit taller on a wide window, never as tall as the picture would make it
        SizeChanged += (_, e) => HeroRow.Height = Math.Round(Math.Clamp(e.NewSize.Width * 0.22, 320, 440));
    }

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

    // A friend: a click (or right-click) opens their menu. Joining is one choice, never a click by accident.
    private void Friend_Click(object sender, RoutedEventArgs e) => ShowFriendMenu(sender as FrameworkElement);

    private void Friend_RightClick(object sender, MouseButtonEventArgs e)
    {
        ShowFriendMenu(sender as FrameworkElement);
        e.Handled = true;
    }

    private void ShowFriendMenu(FrameworkElement? target)
    {
        if (target?.DataContext is not FriendTile f) return;
        var items = new List<(string, Action)>();
        if (f.CanJoin)
        {
            items.Add(($"Join {f.Label} with {VM.CurrentAccount?.Label ?? "this account"}", () => VM.JoinFriendCommand.Execute(f)));
            items.Add(("Join with several alts…", () => VM.JoinFriendWithAltsCommand.Execute(f)));
        }
        items.Add((f.IsFavourite ? "Remove from favourites" : "★ Add to favourites", () => VM.ToggleFavouriteFriendCommand.Execute(f)));
        items.Add(("Copy profile link", () => VM.CopyLinkCommand.Execute(f)));
        items.Add(("Open profile on roblox.com", () => OpenUrl($"https://www.roblox.com/users/{f.UserId}/profile")));
        MenuHelper.Show(target, items.ToArray());
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
