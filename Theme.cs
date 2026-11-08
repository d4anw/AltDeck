using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace RoDeck;

public static class ThemeManager
{
    public static bool SystemUsesLight()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    public static void Apply(string theme, string accentHex)
    {
        var res = Application.Current.Resources;
        bool light = theme == "Light" || (theme == "System" && SystemUsesLight());

        void Set(string key, string hex) => res[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

        if (light)
        {
            Set("Bg", "#E8EAEE"); Set("Page", "#FFFFFF"); Set("Card", "#F5F6F8"); Set("Card2", "#E9ECF0");
            Set("Line", "#D3D8DF"); Set("Fg", "#14161A"); Set("Muted", "#5C636D");
        }
        else
        {
            Set("Bg", "#0A0B0D"); Set("Page", "#101215"); Set("Card", "#15181B"); Set("Card2", "#1C1F23");
            Set("Line", "#272A30"); Set("Fg", "#F2F3F5"); Set("Muted", "#9AA0A8");
        }

        Color accent;
        try { accent = (Color)ColorConverter.ConvertFromString(accentHex); }
        catch { accent = Color.FromRgb(0xFF, 0x8A, 0x1F); }
        res["Accent"] = new SolidColorBrush(accent);
        double lum = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B) / 255.0;
        Set("AccentFg", lum > 0.55 ? "#0B0C0E" : "#FFFFFF");
        Set("Blue", "#4DA3FF"); Set("Orange", "#FF8A3D"); Set("Red", "#FF5A5F");
        ApplyDepth(res, accent, light);

        foreach (Window w in Application.Current.Windows) TitleBar.Apply(w);
    }

    /// <summary>
    /// The extra colours that give the app depth: soft accent tints for selected and hovered things, a gentle gradient on
    /// main buttons and cards, and a faint accent glow in the page's top corner. All made from the theme and the accent.
    /// </summary>
    static void ApplyDepth(ResourceDictionary res, Color accent, bool light)
    {
        static Color Mix(Color a, Color b, double t) => Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        static Color Alpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
        static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
        Color Of(string key) => ((SolidColorBrush)res[key]).Color;

        res["AccentColor"] = accent;
        res["AccentSoft"] = Frozen(new SolidColorBrush(Alpha(accent, light ? (byte)30 : (byte)34)));    // selected tabs / rows
        res["AccentHover"] = Frozen(new SolidColorBrush(Alpha(accent, light ? (byte)18 : (byte)20)));   // hovered rows
        res["AccentLine"] = Frozen(new SolidColorBrush(Alpha(accent, 120)));                              // hovered borders
        res["AccentGrad"] = Frozen(new LinearGradientBrush(Mix(accent, Colors.White, 0.14), Mix(accent, Colors.Black, 0.06), 90));

        var card = Of("Card");
        res["CardGrad"] = Frozen(light
            ? new LinearGradientBrush(Mix(card, Colors.White, 0.6), card, 90)
            : new LinearGradientBrush(Mix(card, Colors.White, 0.035), card, 90));
        // a hair of light along a card's top edge (dark theme), like light falling on it
        res["CardEdge"] = Frozen(light
            ? new LinearGradientBrush(Of("Line"), Of("Line"), 90)
            : new LinearGradientBrush(Mix(Of("Line"), Colors.White, 0.08), Of("Line"), 90));

        var glow = new RadialGradientBrush { Center = new Point(0.05, 0), GradientOrigin = new Point(0.05, 0), RadiusX = 0.75, RadiusY = 0.55 };
        glow.GradientStops.Add(new GradientStop(Alpha(accent, light ? (byte)22 : (byte)26), 0));
        glow.GradientStops.Add(new GradientStop(Alpha(accent, 0), 1));
        res["PageGlow"] = Frozen(glow);
    }
}

/// <summary>Gives normal Windows title bars (dialogs, the login and server windows) the app's dark or light look.</summary>
public static class TitleBar
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    private const int DarkModeAttr = 20, DarkModeAttrOld = 19;   // Windows 11 / Windows 10 20H1+, and older Windows 10 builds

    /// <summary>Hook once at start-up: every window that loads afterwards gets the right title bar.</summary>
    public static void HookAllWindows() =>
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => { if (sender is Window w) Apply(w); }));

    public static void Apply(Window w)
    {
        try
        {
            var h = new WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero) return;
            int dark = IsDark() ? 1 : 0;
            if (DwmSetWindowAttribute(h, DarkModeAttr, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(h, DarkModeAttrOld, ref dark, sizeof(int));
        }
        catch { }
    }

    private static bool IsDark()
    {
        if (Application.Current?.TryFindResource("Fg") is SolidColorBrush b)
            return 0.299 * b.Color.R + 0.587 * b.Color.G + 0.114 * b.Color.B > 128;   // light text = dark theme
        return true;
    }
}
