using System.Windows;

namespace RoDeck.Controls;

/// <summary>
/// Extras for the app's button styles: <c>ctl:Btn.Icon</c> puts a Segoe Fluent glyph before the text, and
/// <c>ctl:Btn.External="True"</c> adds the "opens in your browser" arrow after it.
/// </summary>
public static class Btn
{
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.RegisterAttached("Icon", typeof(string), typeof(Btn), new FrameworkPropertyMetadata(""));
    public static string GetIcon(DependencyObject o) => (string)o.GetValue(IconProperty);
    public static void SetIcon(DependencyObject o, string v) => o.SetValue(IconProperty, v);

    public static readonly DependencyProperty ExternalProperty =
        DependencyProperty.RegisterAttached("External", typeof(bool), typeof(Btn), new FrameworkPropertyMetadata(false));
    public static bool GetExternal(DependencyObject o) => (bool)o.GetValue(ExternalProperty);
    public static void SetExternal(DependencyObject o, bool v) => o.SetValue(ExternalProperty, v);
}
