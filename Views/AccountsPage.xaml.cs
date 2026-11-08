using System.Windows;
using System.Windows.Controls;
using RoDeck.ViewModels;

namespace RoDeck.Views;

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
            ("Paste cookies (one or many)", AddWithCookie),
            ("Import from a file (AccountData.json)…", ImportFromFile));
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
        if (dlg.Cookies.Count == 1)
        {
            var err = await VM.AddAccountFromCookieAsync(dlg.Cookies[0]);
            if (err != null) VM.StatusMessage = err;
            return;
        }
        await RunImportAsync(dlg.Cookies.Select(c => new ImportItem(c)).ToList());
    }

    private async void ImportFromFile()
    {
        var pick = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import accounts",
            Filter = "Account files (*.json;*.txt)|*.json;*.txt|All files (*.*)|*.*"
        };
        if (pick.ShowDialog(Window.GetWindow(this)) != true) return;

        var (items, error) = MainViewModel.ReadAccountFile(pick.FileName);
        if (error != null)
        {
            MessageBox.Show(Window.GetWindow(this), error, "Import accounts", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var ok = MessageBox.Show(Window.GetWindow(this),
            $"Found {items.Count} account{(items.Count == 1 ? "" : "s")} in {System.IO.Path.GetFileName(pick.FileName)}.\n\n" +
            "Add them to RoDeck? Accounts you already have are refreshed, not doubled. Names, notes and groups from the file " +
            "only fill in what's empty. Passwords in the file are ignored.",
            "Import accounts", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok == MessageBoxResult.Yes) await RunImportAsync(items);
    }

    private async Task RunImportAsync(List<ImportItem> items)
    {
        IsEnabled = false;   // one bulk add at a time
        try
        {
            var result = await VM.ImportAccountsAsync(items);
            MessageBox.Show(Window.GetWindow(this), result.Summary, "Add accounts", MessageBoxButton.OK,
                result.Failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        finally { IsEnabled = true; }
    }
}
