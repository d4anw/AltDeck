using System.Windows;
using System.Windows.Controls;

namespace RoDeck.Views;

/// <summary>Placeholder for a roadmap page: icon, title, the version it arrives in, and what it will do.</summary>
public partial class ComingSoonPage : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(ComingSoonPage), new PropertyMetadata(""));
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(string), typeof(ComingSoonPage), new PropertyMetadata(""));
    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(nameof(Version), typeof(string), typeof(ComingSoonPage), new PropertyMetadata(""));
    public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(nameof(Body), typeof(string), typeof(ComingSoonPage), new PropertyMetadata(""));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string Version { get => (string)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    public string Body { get => (string)GetValue(BodyProperty); set => SetValue(BodyProperty, value); }

    public ComingSoonPage() { InitializeComponent(); }
}
