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

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        var screens = VM.GetScreens();
        if (screens.Count == 1) { VM.TileOn(screens[0]); return; }
        MenuHelper.Show((FrameworkElement)sender,
            screens.Select(s => (s.Label, (Action)(() => VM.TileOn(s)))).ToArray());
    }

    private void Windows_Click(object sender, RoutedEventArgs e)
    {
        var items = new List<(string, Action)>();
        if (VM.Settings.Layouts.Count == 0) items.Add(("No saved layouts yet — use Save layout", () => { }));
        foreach (var l in VM.Settings.Layouts.ToList())
            items.Add(($"Restore layout “{l.Name}”  ({l.Slots.Count} windows)", () => VM.RestoreLayout(l)));
        foreach (var l in VM.Settings.Layouts.ToList())
            items.Add(($"Delete layout “{l.Name}”", () => VM.DeleteLayout(l)));
        MenuHelper.Show((FrameworkElement)sender, items.ToArray());
    }

    private void HomeMore_Click(object sender, RoutedEventArgs e)
    {
        MenuHelper.Show((FrameworkElement)sender,
            ("Dismiss disconnected cards", () => VM.DismissDisconnectedCommand.Execute(null)),
            ("Stop all clients", () => VM.StopAllCommand.Execute(null)));
    }
}
