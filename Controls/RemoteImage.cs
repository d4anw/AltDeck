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
        var r = CornerRadius.TopLeft;
        Clip = r > 0 ? new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), r, r) : null;
    }
}
