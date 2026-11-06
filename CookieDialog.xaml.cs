using System.Windows;
using AltDeck.ViewModels;

namespace AltDeck;

/// <summary>Paste one cookie or many (one per line, or any text with cookies in it).</summary>
public partial class CookieDialog : Window
{
    public List<string> Cookies { get; private set; } = new();

    public CookieDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => Input.Focus();
    }

    private void Input_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        Cookies = MainViewModel.FindCookies(Input.Text);
        Count.Text = Cookies.Count switch
        {
            0 => string.IsNullOrWhiteSpace(Input.Text) ? "" : "No cookie found yet.",
            1 => "1 account",
            var n => $"{n} accounts"
        };
        AddBtn.Content = Cookies.Count > 1 ? $"Add {Cookies.Count} accounts" : "Add";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Cookies = MainViewModel.FindCookies(Input.Text);
        if (Cookies.Count == 0)
        {
            // something that doesn't look like a cookie: still try it, so the error comes from Roblox
            if (string.IsNullOrWhiteSpace(Input.Text)) return;
            Cookies = new() { Input.Text.Trim() };
        }
        DialogResult = true;
    }
}
