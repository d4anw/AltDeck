using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace RoDeck.Services;

/// <summary>The notification-area icon. A click opens RoDeck's tray panel; a double-click opens RoDeck.
/// Visible while RoDeck can keep running there (Close to tray, notifications, or a trade ad posting).</summary>
public sealed class TrayService : IDisposable
{
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

    private readonly Forms.NotifyIcon _icon;
    private readonly Icon _image;
    private readonly IntPtr _handle;

    public event Action? OpenRequested;
    public event Action? StopAllRequested;
    public event Action? QuitRequested;
    /// <summary>The icon was clicked (left or right): show RoDeck's tray panel.</summary>
    public event Action? PanelRequested;
    /// <summary>The last notification was clicked.</summary>
    public event Action? BalloonClicked;

    public TrayService()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var accent = new SolidBrush(Color.FromArgb(0xFF, 0x8A, 0x1F));
            using var muted = new SolidBrush(Color.FromArgb(0x9A, 0xA0, 0xA8));
            g.FillRectangle(accent, 2, 2, 14, 14);      // same little logo as the title bar
            g.FillRectangle(accent, 16, 16, 14, 14);
            g.FillRectangle(muted, 20, 4, 10, 10);
        }
        _handle = bmp.GetHicon();
        _image = Icon.FromHandle(_handle);

        _icon = new Forms.NotifyIcon { Icon = _image, Text = "RoDeck", Visible = false };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        _icon.BalloonTipClicked += (_, _) => BalloonClicked?.Invoke();
        _icon.MouseClick += (_, e) => { if (e.Button is Forms.MouseButtons.Left or Forms.MouseButtons.Right) PanelRequested?.Invoke(); };
    }

    public bool Visible
    {
        get => _icon.Visible;
        set => _icon.Visible = value;
    }

    public void SetTooltip(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;   // Windows allows 63 characters

    public void Balloon(string title, string text)
    {
        if (!_icon.Visible) return;
        _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
        DestroyIcon(_handle);
    }
}
