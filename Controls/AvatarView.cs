using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace AltDeck.Controls;

/// <summary>Round avatar: shows initials until the image URL has loaded.</summary>
public class AvatarView : Grid
{
    private readonly Ellipse _bg = new();
    private readonly TextBlock _txt = new();
    private readonly Ellipse _img = new();

    public static readonly DependencyProperty UrlProperty = DependencyProperty.Register(
        nameof(Url), typeof(string), typeof(AvatarView),
        new PropertyMetadata("", (d, _) => ((AvatarView)d).UpdateImage()));

    public static readonly DependencyProperty InitialsProperty = DependencyProperty.Register(
        nameof(Initials), typeof(string), typeof(AvatarView),
        new PropertyMetadata("", (d, e) => ((AvatarView)d)._txt.Text = e.NewValue as string ?? ""));

    public string Url { get => (string)GetValue(UrlProperty); set => SetValue(UrlProperty, value); }
    public string Initials { get => (string)GetValue(InitialsProperty); set => SetValue(InitialsProperty, value); }

    public AvatarView()
    {
        _bg.SetResourceReference(Shape.FillProperty, "Line");
        _txt.HorizontalAlignment = HorizontalAlignment.Center;
        _txt.VerticalAlignment = VerticalAlignment.Center;
        _txt.FontWeight = FontWeights.SemiBold;
        _txt.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        Children.Add(_bg);
        Children.Add(_txt);
        Children.Add(_img);
        SizeChanged += (_, _) => _txt.FontSize = Math.Max(10, ActualHeight * 0.38);
    }

    private int _version;

    // Through the shared picture cache, so the same avatar isn't downloaded again on every page.
    private async void UpdateImage()
    {
        int v = ++_version;
        var bmp = await AltDeck.Services.ImageCache.GetAsync(Url);
        if (v != _version) return;
        _img.Fill = bmp != null ? new ImageBrush(bmp) { Stretch = Stretch.UniformToFill } : null;
    }
}
