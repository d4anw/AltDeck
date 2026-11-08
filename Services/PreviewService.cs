using System.Windows;
using System.Windows.Media.Imaging;

namespace RoDeck.Services;

/// <summary>
/// Keeps the live picture on each client card fresh. The capture runs on a background thread and only the finished
/// picture is handed to the UI. A minimized window can't be photographed, so its last picture stays up, marked as paused.
/// </summary>
public sealed class PreviewService
{
    private const int MaxWidth = 640;
    private int _busy;

    /// <summary>Call about once a second, on the UI thread, with the clients whose cards are on screen.</summary>
    public void Refresh(IReadOnlyList<ClientInfo> clients)
    {
        if (clients.Count == 0) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;   // the previous round is still running

        var jobs = clients.Select(c => (Client: c, c.Pid)).ToList();
        Task.Run(() =>
        {
            var results = new List<(ClientInfo Client, BitmapSource? Image, string Note)>();
            try
            {
                foreach (var (client, pid) in jobs)
                {
                    var hwnd = Win32.MainWindowOf(pid);
                    if (hwnd == IntPtr.Zero) { results.Add((client, null, "Waiting for the window…")); continue; }
                    if (Win32.IsMinimized(hwnd)) { results.Add((client, null, "Minimized · preview paused")); continue; }

                    var image = WindowCapture.Capture(hwnd, MaxWidth);
                    results.Add((client, image, image == null ? "Can't see this window right now" : ""));
                }
            }
            catch { /* a failed round just means the old pictures stay */ }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) { Interlocked.Exchange(ref _busy, 0); return; }
            dispatcher.InvokeAsync(() =>
            {
                try
                {
                    foreach (var (client, image, note) in results)
                    {
                        if (client.IsClosed) continue;
                        if (image != null) client.Preview = image;
                        client.PreviewNote = note;
                    }
                }
                finally { Interlocked.Exchange(ref _busy, 0); }
            });
        });
    }
}
