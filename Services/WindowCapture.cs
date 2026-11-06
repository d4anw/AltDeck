using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Imaging = System.Drawing.Imaging;

namespace AltDeck.Services;

/// <summary>Takes a picture of another program's window, so the Home cards can show what each client is doing.</summary>
internal static class WindowCapture
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Pt { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref Pt point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Pt point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);

    private const uint PwClientOnly = 0x1;
    private const uint PwRenderFullContent = 0x2;   // also gets GPU-drawn content (Windows 8.1+)
    private const uint GaRoot = 2;

    /// <summary>
    /// A frame of the window's inside, scaled down to at most maxWidth wide, or null when it can't be captured
    /// right now (minimized, hidden, too small, or Windows only gave a black picture). Safe to call from any thread.
    /// </summary>
    public static BitmapSource? Capture(IntPtr hwnd, int maxWidth)
    {
        try
        {
            if (hwnd == IntPtr.Zero || IsIconic(hwnd) || !IsWindowVisible(hwnd)) return null;
            if (!GetClientRect(hwnd, out var rc)) return null;
            int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
            if (w < 40 || h < 40) return null;

            using var full = new Drawing.Bitmap(w, h, Imaging.PixelFormat.Format32bppRgb);

            // 1) ask the window to draw itself into our bitmap (works even when other windows cover it)
            using (var g = Drawing.Graphics.FromImage(full))
            {
                var hdc = g.GetHdc();
                try { PrintWindow(hwnd, hdc, PwClientOnly | PwRenderFullContent); }
                finally { g.ReleaseHdc(hdc); }
            }

            // 2) some GPU-drawn windows come back black: copy from the screen instead, but only if the window is
            //    really on top at that spot (otherwise we would photograph whatever covers it)
            if (LooksBlank(full) && IsOnTop(hwnd, w, h, out var origin))
            {
                using var g = Drawing.Graphics.FromImage(full);
                g.CopyFromScreen(origin.X, origin.Y, 0, 0, new Drawing.Size(w, h));
            }
            if (LooksBlank(full)) return null;

            return ToBitmapSource(full, maxWidth);
        }
        catch { return null; }
    }

    private static bool IsOnTop(IntPtr hwnd, int w, int h, out Pt origin)
    {
        origin = default;
        var tl = new Pt();
        if (!ClientToScreen(hwnd, ref tl)) return false;
        origin = tl;
        var root = GetAncestor(hwnd, GaRoot);
        foreach (var (fx, fy) in new[] { (0.5, 0.5), (0.2, 0.2), (0.8, 0.8) })
        {
            var p = new Pt { X = tl.X + (int)(w * fx), Y = tl.Y + (int)(h * fy) };
            var top = GetAncestor(WindowFromPoint(p), GaRoot);
            if (top != root) return false;
        }
        return true;
    }

    /// <summary>True when a grid of sample points is all (nearly) black: the usual sign of a failed capture.</summary>
    private static bool LooksBlank(Drawing.Bitmap bmp)
    {
        var data = bmp.LockBits(new Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
                                Imaging.ImageLockMode.ReadOnly, Imaging.PixelFormat.Format32bppRgb);
        try
        {
            var row = new byte[4];
            for (int gy = 1; gy <= 8; gy++)
            for (int gx = 1; gx <= 12; gx++)
            {
                int x = bmp.Width * gx / 13, y = bmp.Height * gy / 9;
                Marshal.Copy(data.Scan0 + y * data.Stride + x * 4, row, 0, 4);
                if (row[0] > 6 || row[1] > 6 || row[2] > 6) return false;
            }
            return true;
        }
        finally { bmp.UnlockBits(data); }
    }

    private static BitmapSource ToBitmapSource(Drawing.Bitmap src, int maxWidth)
    {
        int w = src.Width, h = src.Height;
        Drawing.Bitmap? scaled = null;
        try
        {
            var bmp = src;
            if (w > maxWidth)
            {
                int nh = Math.Max(1, (int)Math.Round(h * (maxWidth / (double)w)));
                scaled = new Drawing.Bitmap(maxWidth, nh, Imaging.PixelFormat.Format32bppRgb);
                using (var g = Drawing.Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                    g.DrawImage(src, 0, 0, maxWidth, nh);
                }
                bmp = scaled;
            }

            var data = bmp.LockBits(new Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
                                    Imaging.ImageLockMode.ReadOnly, Imaging.PixelFormat.Format32bppRgb);
            try
            {
                var image = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, PixelFormats.Bgr32, null,
                                                data.Scan0, data.Stride * bmp.Height, data.Stride);
                image.Freeze();   // frozen = safe to hand to the UI thread
                return image;
            }
            finally { bmp.UnlockBits(data); }
        }
        finally { scaled?.Dispose(); }
    }
}
