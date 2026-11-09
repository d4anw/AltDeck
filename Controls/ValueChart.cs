using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RoDeck.Services;

namespace RoDeck.Controls;

/// <summary>
/// Value and RAP over time as two lines (value in the accent colour with a soft fill, RAP in blue), with R$ labels on
/// the left and dates underneath. Hovering shows a line at that day, a dot on each line and a card with the numbers.
/// </summary>
public class ValueChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<ValuePoint>), typeof(ValueChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((ValueChart)d)._hover = -1));

    public IReadOnlyList<ValuePoint>? Points { get => (IReadOnlyList<ValuePoint>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }

    private int _hover = -1;
    private const double Left = 54, Right = 10, Top = 10, Bottom = 26;

    public ValueChart()
    {
        ClipToBounds = true;
        MouseMove += (_, e) => { _hover = IndexAt(e.GetPosition(this).X); InvalidateVisual(); };
        MouseLeave += (_, _) => { _hover = -1; InvalidateVisual(); };
        Cursor = Cursors.Cross;
    }

    private int IndexAt(double x)
    {
        var pts = Points;
        if (pts == null || pts.Count < 2) return -1;
        double w = ActualWidth - Left - Right;
        return (int)Math.Round(Math.Clamp((x - Left) / w, 0, 1) * (pts.Count - 1));
    }

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));   // so the mouse is seen everywhere
        var pts = Points;
        if (pts == null || pts.Count < 2 || ActualWidth < Left + Right + 20) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var muted = Res("Muted", Brushes.Gray);
        var fg = Res("Fg", Brushes.White);
        var accent = Res("Accent", Brushes.Orange);
        var blue = Res("Blue", Brushes.DodgerBlue);
        var line = Res("Line", Brushes.DimGray);
        var card = Res("Card", Brushes.Black);
        var typeface = new Typeface("Segoe UI");

        double w = ActualWidth - Left - Right, h = ActualHeight - Top - Bottom;
        long max = Math.Max(1, pts.Max(p => Math.Max(p.Value, p.Rap)));
        double top = NiceCeiling(max * 1.05);
        double X(int i) => Left + i * w / (pts.Count - 1);
        double Y(long v) => Top + h - v / top * h;

        // grid and R$ labels
        var gridPen = new Pen(line, 1); gridPen.Freeze();
        for (int k = 0; k <= 4; k++)
        {
            double v = top * k / 4, y = Math.Round(Y((long)v)) + 0.5;
            dc.DrawLine(gridPen, new Point(Left, y), new Point(Left + w, y));
            var t = Text(Short(v), 11, muted, typeface, dpi);
            dc.DrawText(t, new Point(Left - 8 - t.Width, y - t.Height / 2));
        }
        // dates underneath
        int labels = Math.Max(2, Math.Min(6, (int)(w / 110)));
        var span = pts[^1].Day - pts[0].Day;
        string fmt = span.TotalDays > 400 ? "MMM yyyy" : "d MMM";
        for (int k = 0; k < labels; k++)
        {
            int i = (int)Math.Round((double)k * (pts.Count - 1) / (labels - 1));
            var t = Text(pts[i].Day.ToString(fmt, CultureInfo.CurrentCulture), 11, muted, typeface, dpi);
            double x = Math.Clamp(X(i) - t.Width / 2, Left, Left + w - t.Width);
            dc.DrawText(t, new Point(x, Top + h + 7));
        }

        // value: soft fill and line; RAP: line on top
        var valueGeo = Path(pts, X, p => Y(p.Value));
        var fill = new StreamGeometry();
        using (var g = fill.Open())
        {
            g.BeginFigure(new Point(X(0), Top + h), true, true);
            for (int i = 0; i < pts.Count; i++) g.LineTo(new Point(X(i), Y(pts[i].Value)), true, true);
            g.LineTo(new Point(X(pts.Count - 1), Top + h), true, true);
        }
        fill.Freeze();
        // the fill fades out towards the bottom
        var c = accent is SolidColorBrush sc ? sc.Color : Colors.Orange;
        var soft = new LinearGradientBrush(Color.FromArgb(70, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90); soft.Freeze();
        dc.DrawGeometry(soft, null, fill);
        var valuePen = new Pen(accent, 2) { LineJoin = PenLineJoin.Round }; valuePen.Freeze();
        var rapPen = new Pen(blue, 2) { LineJoin = PenLineJoin.Round }; rapPen.Freeze();
        dc.DrawGeometry(null, rapPen, Path(pts, X, p => Y(p.Rap)));
        dc.DrawGeometry(null, valuePen, valueGeo);

        // the hovered day
        if (_hover >= 0 && _hover < pts.Count)
        {
            var p = pts[_hover];
            double x = X(_hover);
            var guide = new Pen(muted, 1) { DashStyle = DashStyles.Dash }; guide.Freeze();
            dc.DrawLine(guide, new Point(x, Top), new Point(x, Top + h));
            var ring = new Pen(card, 2); ring.Freeze();
            dc.DrawEllipse(blue, ring, new Point(x, Y(p.Rap)), 5, 5);
            dc.DrawEllipse(accent, ring, new Point(x, Y(p.Value)), 5, 5);

            // a card: the date, then Value and RAP with their amounts lined up on the right
            var date = Text(p.Day.ToString("d MMM yyyy", CultureInfo.CurrentCulture), 11, muted, typeface, dpi);
            var vl = Text("Value", 12, muted, typeface, dpi);
            var rl = Text("RAP", 12, muted, typeface, dpi);
            var va = Text($"R$ {p.Value:N0}", 12.5, fg, typeface, dpi, FontWeights.SemiBold);
            var ra = Text($"R$ {p.Rap:N0}", 12.5, fg, typeface, dpi, FontWeights.SemiBold);
            double inner = Math.Max(date.Width, 14 + Math.Max(vl.Width, rl.Width) + 18 + Math.Max(va.Width, ra.Width));
            double bw = inner + 24, bh = 70;
            double bx = x + 14 + bw > Left + w ? x - 14 - bw : x + 14;
            double by = Math.Clamp(Math.Min(Y(p.Value), Y(p.Rap)) - bh / 2, Top, Top + h - bh);
            var border = new Pen(line, 1); border.Freeze();
            dc.DrawRoundedRectangle(card, border, new Rect(bx, by, bw, bh), 10, 10);
            dc.DrawText(date, new Point(bx + 12, by + 9));
            dc.DrawEllipse(accent, null, new Point(bx + 16, by + 35), 4, 4);
            dc.DrawText(vl, new Point(bx + 26, by + 35 - vl.Height / 2));
            dc.DrawText(va, new Point(bx + bw - 12 - va.Width, by + 35 - va.Height / 2));
            dc.DrawEllipse(blue, null, new Point(bx + 16, by + 55), 4, 4);
            dc.DrawText(rl, new Point(bx + 26, by + 55 - rl.Height / 2));
            dc.DrawText(ra, new Point(bx + bw - 12 - ra.Width, by + 55 - ra.Height / 2));
        }
    }

    private static Geometry Path(IReadOnlyList<ValuePoint> pts, Func<int, double> x, Func<ValuePoint, double> y)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(x(0), y(pts[0])), false, false);
            for (int i = 1; i < pts.Count; i++) g.LineTo(new Point(x(i), y(pts[i])), true, true);
        }
        geo.Freeze();
        return geo;
    }

    private static FormattedText Text(string s, double size, Brush brush, Typeface face, double dpi, FontWeight? weight = null) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            weight is { } fw ? new Typeface(face.FontFamily, FontStyles.Normal, fw, FontStretches.Normal) : face, size, brush, dpi);

    /// <summary>A round number just above <paramref name="v"/> (so the grid lines land on 2.5k, 5k, 7.5k…).</summary>
    private static double NiceCeiling(double v)
    {
        double mag = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(1, v))));
        foreach (var step in new[] { 1, 1.2, 1.6, 2, 2.4, 3, 4, 5, 6, 8, 10 })
            if (step * mag >= v) return step * mag;
        return 10 * mag;
    }

    private static string Short(double v) =>
        v >= 1_000_000 ? $"{v / 1_000_000:0.##}M" : v >= 1000 ? $"{v / 1000:0.##}k" : v.ToString("0");
}
