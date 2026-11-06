using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AltDeck.Models;
using AltDeck.ViewModels;

namespace AltDeck.Views;

public partial class PlayPage : UserControl
{
    public PlayPage() { InitializeComponent(); }

    private MainViewModel VM => (MainViewModel)DataContext;

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var a in VM.Accounts) a.IsSelected = a.CanPick;
    }

    private void MoreOptions_Click(object sender, RoutedEventArgs e) =>
        MoreOpts.Visibility = MoreOpts.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void Preset_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var el = (FrameworkElement)sender;
        if (el.DataContext is not LaunchPreset p) return;
        MenuHelper.Show(el,
            ("Launch", () => _ = VM.RunPresetAsync(p, "Play page")),
            ("Load without launching", () => VM.LoadPreset(p)),
            ("Replace with current picks and target", () => VM.UpdatePresetFromCurrent(p)),
            (p.HasHotkey ? $"Change hotkey ({p.Hotkey})…" : "Set hotkey…", () => VM.SetPresetHotkey(p)),
            ("Rename…", () => VM.RenamePreset(p)),
            ("Delete", () => VM.DeletePreset(p)));
    }

    private void Fav_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is Favourite f) VM.RemoveFavourite(f);
        e.Handled = true;
    }
}
