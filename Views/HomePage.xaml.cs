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

    /// <summary>Everything about the client windows in one menu: tile on a screen, minimize / show, layouts.</summary>
    private void Windows_Click(object sender, RoutedEventArgs e)
    {
        var items = new List<(string, Action)>();
        var screens = VM.GetScreens();
        foreach (var sc in screens)
            items.Add((screens.Count == 1 ? "Tile windows" : $"Tile windows on {sc.Label}", () => VM.TileOn(sc)));
        items.Add(("Minimize all", () => VM.MinimizeAllCommand.Execute(null)));
        items.Add(("Show all", () => VM.ShowAllCommand.Execute(null)));
        items.Add(("Save this layout…", () => VM.SaveLayoutCommand.Execute(null)));
        foreach (var l in VM.Settings.Layouts.ToList())
            items.Add(($"Restore layout “{l.Name}”  ({l.Slots.Count} windows)", () => VM.RestoreLayout(l)));
        foreach (var l in VM.Settings.Layouts.ToList())
            items.Add(($"Delete layout “{l.Name}”", () => VM.DeleteLayout(l)));
        items.Add(("Clear disconnected cards", () => VM.DismissDisconnectedCommand.Execute(null)));
        MenuHelper.Show((FrameworkElement)sender, items.ToArray());
    }
}
