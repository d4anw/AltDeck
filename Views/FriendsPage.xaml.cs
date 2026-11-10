using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RoDeck.Models;
using RoDeck.ViewModels;

namespace RoDeck.Views;

/// <summary>Friends: the friend list, requests, player search and profiles.</summary>
public partial class FriendsPage : UserControl
{
    public FriendsPage()
    {
        InitializeComponent();
        // coming back to Friends, or opening another profile: from the top
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) { ListScroll.ScrollToTop(); ProfileScroll.ScrollToTop(); } };
        ProfileScroll.IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) ProfileScroll.ScrollToTop(); };
    }

    private MainViewModel VM => (MainViewModel)DataContext;
    private string Me => VM.CurrentAccount?.Label ?? "this account";

    // ---- a friend card: a click (or right-click) opens its menu

    private void Friend_Click(object sender, RoutedEventArgs e) => ShowFriendMenu(sender as FrameworkElement);

    private void Friend_RightClick(object sender, MouseButtonEventArgs e)
    {
        ShowFriendMenu(sender as FrameworkElement);
        e.Handled = true;
    }

    private void ShowFriendMenu(FrameworkElement? target)
    {
        if (target?.DataContext is not FriendTile f) return;
        var items = new List<(string, Action)> { ("View profile", () => VM.OpenProfileCommand.Execute(f)) };
        if (f.CanJoin)
        {
            items.Add(($"Join {f.Label} with {Me}", () => VM.JoinFriendCommand.Execute(f)));
            items.Add(("Join with several alts…", () => VM.JoinFriendWithAltsCommand.Execute(f)));
        }
        items.Add((f.IsFavourite ? "Remove from favourites" : "★ Add to favourites", () => VM.ToggleFavouriteFriendCommand.Execute(f)));
        items.Add(("Copy profile link", () => VM.CopyLinkCommand.Execute(f)));
        items.Add(("Open profile on roblox.com", () => Open($"https://www.roblox.com/users/{f.UserId}/profile")));
        items.Add(("Unfriend…", () => ConfirmUnfriend(target, f.UserId, f.Label)));
        MenuHelper.Show(target, items.ToArray());
    }

    // ---- the profile's friendship button: Add friend / Accept request / Friends ✓ (menu to unfriend)

    private void FriendBtn_Click(object sender, RoutedEventArgs e)
    {
        var p = VM.OpenProfile;
        if (p == null) return;
        if (p.Friendship == "Friends") ConfirmUnfriend(FriendBtn, p.UserId, p.Label);
        else if (p.Friendship is "NotFriends" or "RequestReceived") VM.ProfileFriendActionCommand.Execute(null);
    }

    /// <summary>Unfriending changes the real account, so it's a second, explicit click.</summary>
    private void ConfirmUnfriend(FrameworkElement target, long userId, string name)
    {
        MenuHelper.Show(target,
            ($"Yes, unfriend {name} from {Me}", () => _ = VM.UnfriendAsync(userId, name)),
            ("Cancel", () => { }));
    }

    private static void Open(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    // ---- requests: as many columns as fit
    private System.Windows.Controls.Primitives.UniformGrid? _requestsGrid;
    private double _requestsWidth;

    private void RequestsGrid_Loaded(object sender, RoutedEventArgs e)
    {
        _requestsGrid = sender as System.Windows.Controls.Primitives.UniformGrid;
        FitRequestColumns();
    }

    private void Requests_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _requestsWidth = e.NewSize.Width;
        FitRequestColumns();
    }

    private void FitRequestColumns()
    {
        if (_requestsGrid == null || _requestsWidth <= 0) return;
        _requestsGrid.Columns = Math.Clamp((int)(_requestsWidth / 460), 1, 4);
    }
}
