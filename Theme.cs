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

        foreach (Window w in Application.Current.Windows) TitleBar.Apply(w);
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
