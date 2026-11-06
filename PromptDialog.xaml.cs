using System.Windows;

namespace AltDeck;

public partial class PromptDialog : Window
{
    public string Value { get; private set; } = "";

    public PromptDialog(string title, string prompt, string initial = "")
    {
        InitializeComponent();
        Title = title;
        Prompt.Text = prompt;
        Input.Text = initial;
        Loaded += (_, _) => { Input.Focus(); Input.SelectAll(); };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Value = Input.Text.Trim();
        DialogResult = true;
    }

    public static string? Ask(Window? owner, string title, string prompt, string initial = "")
    {
        var d = new PromptDialog(title, prompt, initial) { Owner = owner };
        return d.ShowDialog() == true && d.Value.Length > 0 ? d.Value : null;
    }
}
