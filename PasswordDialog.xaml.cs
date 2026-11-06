using System.Windows;
using System.Windows.Controls;

namespace AltDeck;

/// <summary>Asks for one or more passwords. The caller's validator can keep the dialog open with an error message.</summary>
public partial class PasswordDialog : Window
{
    private readonly List<PasswordBox> _boxes = new();
    private readonly Func<string[], string?>? _validate;

    public string[] Values { get; private set; } = Array.Empty<string>();

    public PasswordDialog(string title, string prompt, string[] labels, Func<string[], string?>? validate, string cancelText)
    {
        InitializeComponent();
        Title = title;
        Prompt.Text = prompt;
        CancelBtn.Content = cancelText;
        _validate = validate;

        foreach (var label in labels)
        {
            var text = new TextBlock { Text = label, FontSize = 12, Margin = new Thickness(0, _boxes.Count == 0 ? 0 : 10, 0, 4) };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            var box = new PasswordBox();
            _boxes.Add(box);
            Fields.Children.Add(text);
            Fields.Children.Add(box);
        }
        Loaded += (_, _) => _boxes[0].Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var values = _boxes.Select(b => b.Password).ToArray();
        var error = _validate?.Invoke(values);
        if (error != null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            foreach (var b in _boxes) b.Clear();
            _boxes[0].Focus();
            return;
        }
        Values = values;
        DialogResult = true;
    }

    /// <summary>Returns the typed values, or null if the person cancelled. <paramref name="owner"/> may be null (start-up).</summary>
    public static string[]? Ask(Window? owner, string title, string prompt, string[] labels,
                                Func<string[], string?>? validate = null, string cancelText = "Cancel")
    {
        var d = new PasswordDialog(title, prompt, labels, validate, cancelText);
        if (owner != null) d.Owner = owner;
        else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return d.ShowDialog() == true ? d.Values : null;
    }
}
