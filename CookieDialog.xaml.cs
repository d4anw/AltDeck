using System.Windows;

namespace AltDeck;

public partial class CookieDialog : Window
{
    public string Cookie { get; private set; } = "";

    public CookieDialog()
    {
        InitializeComponent();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Cookie = Input.Text;
        if (string.IsNullOrWhiteSpace(Cookie)) return;
        DialogResult = true;
    }
}
