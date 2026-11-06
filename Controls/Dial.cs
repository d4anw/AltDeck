using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace AltDeck.Controls;

/// <summary>Circular gauge with a centred label, drawn directly (no XAML).</summary>
public class Dial : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(Dial),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(Dial),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 10) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        double r = size / 2 - 3;
        var track = (Brush)(TryFindResource("Line") ?? Brushes.Gray);
        var accent = (Brush)(TryFindResource("Accent") ?? Brushes.Orange);
        var fg = (Brush)(TryFindResource("Fg") ?? Brushes.White);

        dc.DrawEllipse(null, new Pen(track, 4), c, r, r);

        double v = Math.Clamp(Value, 0, 100);
        if (v > 0.5)
        {
            double ang = v / 100.0 * 359.99;
            double rad = ang * Math.PI / 180.0;
            var end = new Point(c.X + r * Math.Sin(rad), c.Y - r * Math.Cos(rad));
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(c.X, c.Y - r), false, false);
                ctx.ArcTo(end, new Size(r, r), 0, ang > 180, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            dc.DrawGeometry(null, new Pen(accent, 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
        }

        var ft = new FormattedText(Text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI Semibold"), 13, fg, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y - ft.Height / 2));
    }
}
