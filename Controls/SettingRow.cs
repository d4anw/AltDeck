using System.ComponentModel;
using System.Runtime.CompilerServices;
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

    /// <summary>The last row shown in its group: it has no divider line under it (the card's edge is already there).</summary>
    public static readonly DependencyProperty IsLastProperty =
        DependencyProperty.Register(nameof(IsLast), typeof(bool), typeof(SettingRow), new PropertyMetadata(false));
    public bool IsLast { get => (bool)GetValue(IsLastProperty); set => SetValue(IsLastProperty, value); }

    // panels already watched, so each group is hooked once
    private static readonly ConditionalWeakTable<Panel, object> Watched = new();
    private static readonly DependencyPropertyDescriptor VisibilityDesc =
        DependencyPropertyDescriptor.FromProperty(VisibilityProperty, typeof(UIElement));

    public SettingRow()
    {
        Loaded += (_, _) =>
        {
            if (Parent is not Panel p) return;
            if (!Watched.TryGetValue(p, out _))
            {
                Watched.Add(p, new object());
                // rows that appear or disappear (shown only when a switch is on) change which one is last
                foreach (UIElement c in p.Children) VisibilityDesc.AddValueChanged(c, (_, _) => MarkLast(p));
            }
            MarkLast(p);
        };
    }

    private static void MarkLast(Panel p)
    {
        UIElement? last = null;
        foreach (UIElement c in p.Children) if (c.Visibility == Visibility.Visible) last = c;
        foreach (UIElement c in p.Children) if (c is SettingRow r) r.IsLast = ReferenceEquals(r, last);
    }
}
