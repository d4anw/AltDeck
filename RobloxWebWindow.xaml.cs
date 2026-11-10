using System.Windows;
using Microsoft.Web.WebView2.Core;
using RoDeck.Models;

namespace RoDeck;

/// <summary>
/// A roblox.com page (Buy Robux, My Transactions, Redeem Roblox Codes) signed in as one of your accounts.
/// It runs in its own throwaway browser profile: the account's sign-in is put in for roblox.com only, and the whole
/// profile is deleted when the window closes. Signing out in it is blocked, because that would end the session
/// RoDeck uses for the account too.
/// </summary>
public partial class RobloxWebWindow : Window
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "rodeck-web-" + Guid.NewGuid().ToString("N"));
    private readonly Account _account;
    private readonly string _url;

    public RobloxWebWindow(Account account, string url, string title)
    {
        InitializeComponent();
        _account = account;
        _url = url;
        Title = $"{title} · {account.Label}";
        Loaded += async (_, _) =>
        {
            try { await InitAsync(); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't open the Roblox page (is the WebView2 Runtime installed?)\n\n" + ex.Message, "RoDeck");
                Close();
            }
        };
        Closed += (_, _) =>
        {
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
        var core = Web.CoreWebView2;

        var cookie = core.CookieManager.CreateCookie(".ROBLOSECURITY", _account.Cookie, ".roblox.com", "/");
        cookie.IsSecure = true;
        cookie.IsHttpOnly = true;
        core.CookieManager.AddOrUpdateCookie(cookie);

        // "Log out" here would sign the account out everywhere, RoDeck included: refuse it
        core.AddWebResourceRequestedFilter("https://auth.roblox.com/*logout*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
            e.Response = env.CreateWebResourceResponse(null, 403, "Blocked by RoDeck", "");

        core.Navigate(_url);
    }
}
