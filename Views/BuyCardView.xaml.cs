using System.Windows;
using System.Windows.Controls;
using RoDeck.ViewModels;

namespace RoDeck.Views;

/// <summary>The confirm card for buying one copy of a limited (a deal, or a seller on the item page). Trades and Marketplace show it.</summary>
public partial class BuyCardView : UserControl
{
    public BuyCardView() { InitializeComponent(); }

    // "View item" on the deal's confirm card: the card closes, the item page opens
    private void DealBuyView_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.ShowDealBuy = false;
    }

}
