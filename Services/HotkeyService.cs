using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace AltDeck.Services;

/// <summary>
/// Global hotkeys (they work while another app has focus), registered on the main window.
/// Id <see cref="PanicId"/> is the panic hotkey; launch presets use ids from <see cref="PresetIdBase"/> up.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    public const int PanicId = 0xA17D;
    public const int PresetIdBase = 0xA200;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private HwndSource? _source;
    private IntPtr _hwnd;
    private readonly HashSet<int> _registered = new();

    /// <summary>The panic hotkey was pressed.</summary>
    public event Action? Pressed;
    /// <summary>Any other registered hotkey was pressed (its id).</summary>
    public event Action<int>? PressedId;

    /// <summary>Hooks the window's message loop. Call once the window has a handle (SourceInitialized).</summary>
    public void Attach(Window window)
    {
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(Hook);
    }

    /// <summary>The panic hotkey. Empty or "off" removes it. Returns a short message for the user.</summary>
    public (bool Ok, string Message) Register(string? text) => Register(PanicId, text);

    /// <summary>Registers the hotkey text (like "Ctrl+Shift+F12") under this id. Empty or "off" removes it.</summary>
    public (bool Ok, string Message) Register(int id, string? text)
    {
        Unregister(id);
        text = (text ?? "").Trim();
        if (text.Length == 0 || text.Equals("off", StringComparison.OrdinalIgnoreCase))
            return (true, "Off.");

        if (!TryParse(text, out var mods, out var vk, out var normalized, out var problem))
            return (false, problem);
        if (_hwnd == IntPtr.Zero) return (false, "Not ready yet.");

        if (!RegisterHotKey(_hwnd, id, mods | ModNoRepeat, vk))
            return (false, $"{normalized} is already used (by another app, or another AltDeck hotkey). Pick a different one.");
        _registered.Add(id);
        return (true, $"Active: {normalized}");
    }

    public void Unregister(int id)
    {
        if (_registered.Remove(id) && _hwnd != IntPtr.Zero) UnregisterHotKey(_hwnd, id);
    }

    /// <summary>Removes every preset hotkey (the panic hotkey stays).</summary>
    public void UnregisterPresets()
    {
        foreach (var id in _registered.Where(i => i >= PresetIdBase).ToList()) Unregister(id);
    }

    public void Unregister() => Unregister(PanicId);

    public void Dispose()
    {
        foreach (var id in _registered.ToList()) Unregister(id);
        _source?.RemoveHook(Hook);
        _source = null;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            var id = wParam.ToInt32();
            if (_registered.Contains(id))
            {
                handled = true;
                if (id == PanicId) Pressed?.Invoke();
                else PressedId?.Invoke(id);
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Turns a key press in a "press the keys" box into text like "Ctrl+Shift+F12".
    /// Returns null while only a modifier is down, "" for Backspace / Delete / Esc (meaning: clear it).
    /// </summary>
    public static string? FromKeyPress(Key key, ModifierKeys mods)
    {
        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return null;
        if (mods == ModifierKeys.None && (key == Key.Back || key == Key.Delete || key == Key.Escape))
            return "";

        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    /// <summary>Turns "Ctrl+Shift+F12" into Windows modifier flags and a virtual-key code.</summary>
    public static bool TryParse(string text, out uint mods, out uint vk, out string normalized, out string problem)
    {
        mods = 0; vk = 0; normalized = ""; problem = "";
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) { problem = "That isn't a key combination."; return false; }

        Key key = Key.None;
        foreach (var raw in parts)
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= ModControl; break;
                case "alt": mods |= ModAlt; break;
                case "shift": mods |= ModShift; break;
                case "win": case "windows": mods |= ModWin; break;
                default:
                    if (key != Key.None) { problem = "Use one key plus Ctrl, Alt, Shift or Win."; return false; }
                    try { key = (Key)new KeyConverter().ConvertFromString(raw)!; }
                    catch { problem = $"“{raw}” isn't a key I know."; return false; }
                    break;
            }
        }
        if (key == Key.None) { problem = "Add a key, for example Ctrl+Shift+F12."; return false; }

        bool functionKey = key >= Key.F1 && key <= Key.F24;
        if (mods == 0 && !functionKey && key != Key.Pause)
        {
            problem = "Add Ctrl, Alt or Shift so normal typing isn't blocked.";
            return false;
        }

        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) { problem = "That key can't be used."; return false; }

        var names = new List<string>();
        if ((mods & ModControl) != 0) names.Add("Ctrl");
        if ((mods & ModAlt) != 0) names.Add("Alt");
        if ((mods & ModShift) != 0) names.Add("Shift");
        if ((mods & ModWin) != 0) names.Add("Win");
        names.Add(key.ToString());
        normalized = string.Join("+", names);
        return true;
    }
}
