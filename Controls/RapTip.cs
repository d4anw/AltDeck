using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using RoDeck.Models;
using RoDeck.Services;

namespace RoDeck.Controls;

/// <summary>
/// A tooltip for a limited (its DataContext is a <see cref="LimitedTile"/>): the item's name, value and RAP, and a chart of
/// its average sale price over the last 180 days. The chart loads when the tooltip opens.
/// </summary>
public class RapTip : ToolTip
{
    private long _shownFor;

    public RapTip()
    {
        SetResourceReference(BackgroundProperty, "Card");
        SetResourceReference(BorderBrushProperty, "Line");
        SetResourceReference(ForegroundProperty, "Fg");
        BorderThickness = new Thickness(1);
        Padding = new Thickness(14, 12, 14, 12);
        Placement = PlacementMode.Right;
        HasDropShadow = true;
    }

    protected override async void OnOpened(RoutedEventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is not LimitedTile t || t.AssetId == 0) return;
        if (_shownFor == t.AssetId && Content is FrameworkElement) return;
        _shownFor = t.AssetId;
        Content = Build(t, null, "Loading sales…");
        try
        {
            var points = await RobloxTrades.GetPriceHistoryAsync(t.AssetId, CancellationToken.None, t.IsBundle);
            if (_shownFor != t.AssetId) return;
            Content = Build(t, points, points.Count < 2 ? "No sales history." : null);
        }
        catch (RobloxException ex)
        {
            if (_shownFor == t.AssetId) Content = Build(t, null, ex.TooMany ? "Roblox is busy, hover again in a moment." : "Couldn't load the sales history.");
            _shownFor = 0;
        }
    }

    private static FrameworkElement Build(LimitedTile t, List<(DateTime Day, long Price)>? all, string? note)
    {
        var root = new StackPanel { Width = 300 };
        root.Children.Add(new TextBlock { Text = t.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var nums = new TextBlock { FontSize = 12, Margin = new Thickness(0, 2, 0, 8) };
        nums.Inlines.Add(new System.Windows.Documents.Run($"Value {t.Worth:N0}") { Foreground = Brush("Accent") });
        nums.Inlines.Add(new System.Windows.Documents.Run("   "));
        nums.Inlines.Add(new System.Windows.Documents.Run($"RAP {t.Rap:N0}") { Foreground = Brush("Fg") });
        root.Children.Add(nums);

        if (note != null || all == null || all.Count < 2)
        {
            root.Children.Add(Muted(note ?? "No sales history."));
            return root;
        }

        var since = DateTime.UtcNow.AddDays(-180);
        var pts = all.Where(p => p.Day >= since).ToList();
        if (pts.Count < 2) pts = all.TakeLast(30).ToList();
        long lo = pts.Min(p => p.Price), hi = pts.Max(p => p.Price);
        if (hi == lo) hi = lo + 1;
        const double W = 300, H = 110;
        var line = new PointCollection();
        var area = new PointCollection { new Point(0, H) };
        for (int i = 0; i < pts.Count; i++)
        {
            var p = new Point(i * W / (pts.Count - 1), H - 4 - (pts[i].Price - lo) * (H - 8) / (hi - lo));
            line.Add(p);
            area.Add(p);
        }
        area.Add(new Point(W, H));
        var canvas = new Canvas { Width = W, Height = H, ClipToBounds = true };
        var accent = (Brush("Accent") as SolidColorBrush)?.Color ?? Colors.DodgerBlue;
        canvas.Children.Add(new Polygon { Points = area, Fill = new SolidColorBrush(Color.FromArgb(40, accent.R, accent.G, accent.B)) });
        canvas.Children.Add(new Polyline { Points = line, Stroke = Brush("Accent"), StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round });
        root.Children.Add(new Border { Child = canvas, BorderBrush = Brush("Line"), BorderThickness = new Thickness(0, 0, 0, 1) });

        var foot = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        foot.Children.Add(Muted($"{pts[0].Day:d MMM yyyy} – {pts[^1].Day:d MMM}"));
        var right = Muted($"low {lo:N0} · high {hi:N0}");
        right.HorizontalAlignment = HorizontalAlignment.Right;
        foot.Children.Add(right);
        root.Children.Add(foot);
        root.Children.Add(Muted("Average sale price per day (Roblox), which is what RAP follows."));
        return root;
    }

    private static TextBlock Muted(string text)
    {
        var tb = new TextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return tb;
    }

    private static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
