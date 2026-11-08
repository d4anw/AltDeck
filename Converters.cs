using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AltDeck;

/// <summary>True when the bound value equals ConverterParameter. Two-way, so it also drives radio buttons.</summary>
public class EqualsConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => string.Equals(v?.ToString(), p?.ToString(), StringComparison.Ordinal);
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => v is true ? (p ?? "") : Binding.DoNothing;
}

public class PageVisibleConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) =>
        string.Equals(v?.ToString(), p?.ToString(), StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public class StatusBrushConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c)
    {
        var key = (v?.ToString()) switch
        {
            "ingame" => "Accent",
            "online" => "Blue",
            "check" => "Blue",
            "studio" => "Blue",
            "signedout" => "Red",
            _ => "Muted"
        };
        return Application.Current.TryFindResource(key) ?? Brushes.Gray;
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public class GroupBrushConverter : IValueConverter
{
    private static readonly string[] Palette = { "#B6F23C", "#4DA3FF", "#FF8A3D", "#C084FC", "#2DD4BF", "#F472B6", "#FACC15", "#60A5FA" };
    public object Convert(object? v, Type t, object? p, CultureInfo c)
    {
        var s = v?.ToString() ?? "";
        int h = 0;
        foreach (var ch in s.ToLowerInvariant()) h = (h * 31 + ch) & 0x7fffffff;
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(Palette[h % Palette.Length]));
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v == null ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => string.IsNullOrEmpty(v as string) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public class InverseBoolVisibilityConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v is not true;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => v is not true;
}

/// <summary>Colour for a launch-queue row's state.</summary>
public class QueueStateBrushConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c)
    {
        var key = (v?.ToString()) switch
        {
            "Launched" => "Accent",
            "Launching" => "Fg",
            "Failed" or "Signed out" => "Red",
            "Waiting" => "Blue",
            _ => "Muted"
        };
        return Application.Current.TryFindResource(key) ?? Brushes.Gray;
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Colour for an activity log entry's kind.</summary>
public class LogKindBrushConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c)
    {
        var key = (v?.ToString()) switch
        {
            "Launch" or "Rejoin" or "Preset" => "Accent",
            "Drop" or "Give up" or "Frozen" or "Sign-in" or "Error" => "Red",
            "Stop" => "Muted",
            _ => "Blue"
        };
        return Application.Current.TryFindResource(key) ?? Brushes.Gray;
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>A count: Visible when above 0, else Collapsed. With ConverterParameter=Inverse: Visible only when 0.</summary>
public class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c)
    {
        bool any = v is int n && n > 0;
        if (string.Equals(p as string, "Inverse", StringComparison.OrdinalIgnoreCase)) any = !any;
        return any ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>"RRGGBB" (or "#RRGGBB") to a brush, for colour swatches.</summary>
public class HexBrushConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c)
    {
        var hex = (v as string ?? "").Trim().TrimStart('#');
        try { return new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#" + hex)); }
        catch { return System.Windows.Media.Brushes.Gray; }
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
