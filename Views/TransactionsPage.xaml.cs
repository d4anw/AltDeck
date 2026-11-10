using System.Windows;
using System.Windows.Controls;

namespace RoDeck.Views;

public partial class TransactionsPage : UserControl
{
    public TransactionsPage() => InitializeComponent();

    /// <summary>Buy Robux opens Roblox's own payment page, signed in as the account top right (see MainWindow).</summary>
    private void BuyRobux_Click(object sender, RoutedEventArgs e) =>
        (Window.GetWindow(this) as MainWindow)?.OpenBuyRobux();
}
