using System.Windows;
using System.Windows.Controls;

namespace RoDeck.Controls;

/// <summary>A settings line: title + description on the left, the control (Content) on the right.</summary>
public class SettingRow : ContentControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingRow), new PropertyMetadata(""));
    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow), new PropertyMetadata(""));
    public static readonly DependencyProperty SoonProperty =
        DependencyProperty.Register(nameof(Soon), typeof(bool), typeof(SettingRow), new PropertyMetadata(false));

    public static readonly DependencyProperty DescriptionSizeProperty =
        DependencyProperty.Register(nameof(DescriptionSize), typeof(double), typeof(SettingRow), new PropertyMetadata(12.0));

    public double DescriptionSize { get => (double)GetValue(DescriptionSizeProperty); set => SetValue(DescriptionSizeProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public bool Soon { get => (bool)GetValue(SoonProperty); set => SetValue(SoonProperty, value); }
}
