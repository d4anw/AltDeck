using System.Windows;
using System.Windows.Input;
using RoDeck.Services;

namespace RoDeck;

/// <summary>Records a key combination (the box listens to the keys you press, so typos can't happen).</summary>
public partial class HotkeyDialog : Window
{
    public string Value { get; private set; } = "";

    public HotkeyDialog(string title, string prompt, string initial)
    {
        InitializeComponent();
        Title = title;
        Prompt.Text = prompt;
        Value = initial ?? "";
        Keys.Text = Value.Length == 0 ? "(none)" : Value;
        Loaded += (_, _) => Keys.Focus();
    }

    private void Keys_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab) return;
        e.Handled = true;
        if (key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { Ok_Click(this, new RoutedEventArgs()); return; }

        var text = HotkeyService.FromKeyPress(key, Keyboard.Modifiers);
        if (text == null) return;
        Value = text;
        Keys.Text = text.Length == 0 ? "(none)" : text;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Value = "";
        DialogResult = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Value.Length > 0)
        {
            if (!HotkeyService.TryParse(Value, out _, out _, out var normalized, out var problem))
            {
                ErrorText.Text = problem;
                ErrorText.Visibility = Visibility.Visible;
                return;
            }
            Value = normalized;
        }
        DialogResult = true;
    }

    /// <summary>Returns the combination ("" = no hotkey), or null if the person cancelled.</summary>
    public static string? Ask(Window? owner, string title, string prompt, string initial = "")
    {
        var d = new HotkeyDialog(title, prompt, initial) { Owner = owner };
        return d.ShowDialog() == true ? d.Value : null;
    }
}
