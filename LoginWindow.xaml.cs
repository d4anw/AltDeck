using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace RoDeck;

/// <summary>Isolated browser profile: sign in once, we capture the session cookie, then the profile is deleted.</summary>
public partial class LoginWindow : Window
{
    public string? Cookie { get; private set; }
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "rodeck-login-" + Guid.NewGuid().ToString("N"));
    private bool _done;
    private bool _closed;

    public LoginWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            try { await InitAsync(); }
            catch (Exception ex)
            {
                MessageBox.Show("Couldn't start the login browser (is the WebView2 Runtime installed?)\n\n" + ex.Message,
                                "RoDeck");
                Close();
            }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            try { Web.Dispose(); } catch { }
            var dir = _dataDir;
            Task.Run(async () =>
            {
                await Task.Delay(3000);   // give the browser processes time to let go of the files
                try { Directory.Delete(dir, true); } catch { }
            });
        };
    }

    private async Task InitAsync()
    {
        var env = await CoreWebView2Environment.CreateAsync(null, _dataDir);
        await Web.EnsureCoreWebView2Async(env);
        Web.CoreWebView2.NavigationCompleted += async (_, _) => await CheckCookieAsync();
        Web.CoreWebView2.Navigate("https://www.roblox.com/login");
    }

    private async Task CheckCookieAsync()
    {
        if (_done || _closed) return;
        var cookies = await Web.CoreWebView2.CookieManager.GetCookiesAsync("https://www.roblox.com");
        var c = cookies.FirstOrDefault(x => x.Name == ".ROBLOSECURITY");
        if (c == null || string.IsNullOrEmpty(c.Value) || _done || _closed) return;
        _done = true;
        Cookie = c.Value;
        DialogResult = true;
    }
}
