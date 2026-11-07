using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AltDeck.Services;

namespace AltDeck.Controls;

/// <summary>An image from a web link, loaded through <see cref="ImageCache"/>. Shows a soft placeholder until it's there,
/// with rounded corners (<see cref="CornerRadius"/>).</summary>
public class RemoteImage : Border
{
    private readonly Image _img = new() { Stretch = Stretch.UniformToFill };
    private int _version;

    public static readonly DependencyProperty UrlProperty = DependencyProperty.Register(
        nameof(Url), typeof(string), typeof(RemoteImage),
        new PropertyMetadata(null, (d, _) => ((RemoteImage)d).Load()));

    public static readonly DependencyProperty StretchProperty = DependencyProperty.Register(
        nameof(Stretch), typeof(Stretch), typeof(RemoteImage),
        new PropertyMetadata(Stretch.UniformToFill, (d, e) => ((RemoteImage)d)._img.Stretch = (Stretch)e.NewValue));

    /// <summary>Round only the top corners (a picture at the top of a card).</summary>
    public static readonly DependencyProperty TopCornersOnlyProperty = DependencyProperty.Register(
        nameof(TopCornersOnly), typeof(bool), typeof(RemoteImage),
        new PropertyMetadata(false, (d, _) => ((RemoteImage)d).UpdateClip()));

    public bool TopCornersOnly { get => (bool)GetValue(TopCornersOnlyProperty); set => SetValue(TopCornersOnlyProperty, value); }

    public string? Url { get => (string?)GetValue(UrlProperty); set => SetValue(UrlProperty, value); }
    public Stretch Stretch { get => (Stretch)GetValue(StretchProperty); set => SetValue(StretchProperty, value); }

    public RemoteImage()
    {
        SetResourceReference(BackgroundProperty, "Card2");
        ClipToBounds = true;
        Child = _img;
        SizeChanged += (_, _) => UpdateClip();
    }

    private async void Load()
    {
        int v = ++_version;
        _img.Source = null;
        var bmp = await ImageCache.GetAsync(Url);
        if (v != _version) return;   // the link changed while this one loaded
        _img.Source = bmp;
    }

    // Border's CornerRadius doesn't clip its child, so clip by hand.
    private void UpdateClip()
    {
        double r = CornerRadius.TopLeft, w = ActualWidth, h = ActualHeight;
        if (r <= 0 || w <= 0 || h <= 0) { Clip = null; return; }
        if (!TopCornersOnly) { Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r); return; }

        // rounded top, square bottom (the placeholder background too)
        if (CornerRadius.BottomLeft != 0 || CornerRadius.BottomRight != 0) CornerRadius = new CornerRadius(r, r, 0, 0);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(0, h), true, true);
            c.LineTo(new Point(0, r), false, false);
            c.ArcTo(new Point(r, 0), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
            c.LineTo(new Point(w - r, 0), false, false);
            c.ArcTo(new Point(w, r), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
            c.LineTo(new Point(w, h), false, false);
        }
        g.Freeze();
        Clip = g;
    }
}
