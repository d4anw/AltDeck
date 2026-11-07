using System.Windows.Controls;
using System.Windows.Input;
using AltDeck.ViewModels;

namespace AltDeck.Views;

/// <summary>Games: charts, search, favourites, recently played, and a page per game with its server finder.</summary>
public partial class GamesPage : UserControl
{
    public GamesPage() { InitializeComponent(); }

    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ((MainViewModel)DataContext).SearchGamesCommand.Execute(null);
        e.Handled = true;
    }
}
