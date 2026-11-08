using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace RoDeck.Controls;

/// <summary>Search field: icon, hint text, ✕ to clear, Enter runs <see cref="SubmitCommand"/>.</summary>
public partial class SearchBox : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(SearchBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((SearchBox)d).UpdateParts(), null, false, UpdateSourceTrigger.PropertyChanged));

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(SearchBox), new PropertyMetadata("Search"));

    public static readonly DependencyProperty SubmitCommandProperty = DependencyProperty.Register(
        nameof(SubmitCommand), typeof(ICommand), typeof(SearchBox), new PropertyMetadata(null));

    /// <summary>Run <see cref="SubmitCommand"/> also when ✕ clears the box (e.g. to show the full list again).</summary>
    public static readonly DependencyProperty SubmitOnClearProperty = DependencyProperty.Register(
        nameof(SubmitOnClear), typeof(bool), typeof(SearchBox), new PropertyMetadata(false));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string Placeholder { get => (string)GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }
    public ICommand? SubmitCommand { get => (ICommand?)GetValue(SubmitCommandProperty); set => SetValue(SubmitCommandProperty, value); }
    public bool SubmitOnClear { get => (bool)GetValue(SubmitOnClearProperty); set => SetValue(SubmitOnClearProperty, value); }

    public SearchBox()
    {
        InitializeComponent();
        UpdateParts();
    }

    private void UpdateParts()
    {
        bool empty = string.IsNullOrEmpty(Text);
        if (Hint != null) Hint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (Clear != null) Clear.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Box_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SubmitCommand?.CanExecute(null) == true)
        {
            SubmitCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !string.IsNullOrEmpty(Text))
        {
            Clear_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Text = "";
        Box.Focus();
        if (SubmitOnClear && SubmitCommand?.CanExecute(null) == true) SubmitCommand.Execute(null);
    }
}
