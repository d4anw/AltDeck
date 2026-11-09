using System.Windows;
using System.Windows.Controls;
using RoDeck.Services;
using RoDeck.ViewModels;

namespace RoDeck.Views;

public partial class HomePage : UserControl
{
    public HomePage() { InitializeComponent(); }

    private MainViewModel VM => (MainViewModel)DataContext;

    private void CardMore_Click(object sender, RoutedEventArgs e)
    {
        var b = (FrameworkElement)sender;
        if (b.DataContext is not ClientInfo c) return;
        MenuHelper.Show(b,
            ("Show window", () => VM.ShowClientCommand.Execute(c)),
            ("Minimize", () => VM.MinimizeClientCommand.Execute(c)),
            ("Close client", () => VM.CloseClientCommand.Execute(c)),
            ("Dismiss card", () => VM.DismissClientCommand.Execute(c)));
    }

    /// <summary>Tile: on the only screen at once; with more screens, pick which.</summary>
    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        var screens = VM.GetScreens();
        if (screens.Count <= 1) { if (screens.Count == 1) VM.TileOn(screens[0]); return; }
        MenuHelper.Show((FrameworkElement)sender, screens.Select(sc => ($"Tile on {sc.Label}", (Action)(() => VM.TileOn(sc)))).ToArray());
    }

    /// <summary>Saved window layouts: save the current one, restore or delete a saved one.</summary>
    private void Layouts_Click(object sender, RoutedEventArgs e)
    {
        var items = new List<(string, Action)> { ("Save this layout…", () => VM.SaveLayoutCommand.Execute(null)) };
        foreach (var l in VM.Settings.Layouts.ToList())
            items.Add(($"Restore “{l.Name}”  ({l.Slots.Count} windows)", () => VM.RestoreLayout(l)));
        foreach (var l in VM.Settings.Layouts.ToList())
            items.Add(($"Delete “{l.Name}”", () => VM.DeleteLayout(l)));
        MenuHelper.Show((FrameworkElement)sender, items.ToArray());
    }
}
