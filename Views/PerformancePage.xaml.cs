using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AltDeck.Models;
using AltDeck.ViewModels;

namespace AltDeck.Views;

public partial class PerformancePage : UserControl
{
    public PerformancePage()
    {
        InitializeComponent();
    }

    // The slider is bound to Settings.FpsCap both ways, and the box shows it. The box is committed with Enter or when
    // you leave it, then shows the value that was really kept (for example 9999 becomes 240, and text that isn't a
    // number goes back to the current value).
    private void FpsBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Return) return;
        Commit();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void FpsBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => Commit();

    private void Commit()
    {
        if (DataContext is MainViewModel vm &&
            int.TryParse(FpsBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            vm.Settings.FpsCap = Math.Clamp(n, AppSettings.FpsMin, AppSettings.FpsMax);
        FpsBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
    }
}
