using System.Windows;
using System.Windows.Controls;
using AltDeck.ViewModels;

namespace AltDeck.Views;

public partial class AccountsPage : UserControl
{
    public AccountsPage() { InitializeComponent(); }

    private MainViewModel VM => (MainViewModel)DataContext;

    private void MoreDetails_Click(object sender, RoutedEventArgs e) =>
        DetailsPanel.Visibility = DetailsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        MenuHelper.Show((FrameworkElement)sender,
            ("Log in with a browser window", AddWithLogin),
            ("Paste a cookie", AddWithCookie));
    }

    private async void AddWithLogin()
    {
        var win = new LoginWindow { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() != true || win.Cookie == null) return;
        var err = await VM.AddAccountFromCookieAsync(win.Cookie);
        if (err != null) VM.StatusMessage = err;
    }

    private async void AddWithCookie()
    {
        var dlg = new CookieDialog { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        var err = await VM.AddAccountFromCookieAsync(dlg.Cookie);
        if (err != null) VM.StatusMessage = err;
    }
}
