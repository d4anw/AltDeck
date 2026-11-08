using System.Windows;
using System.Windows.Threading;
using RoDeck.Services;

namespace RoDeck;

public partial class App : Application
{
    // Only one RoDeck at a time: two copies would fight over the vault, Roblox's lock and the hotkeys.
    private const string InstanceName = @"Local\RoDeck.SingleInstance";
    private const string ShowSignalName = @"Local\RoDeck.ShowWindow";
    private static Mutex? _instance;
    private static EventWaitHandle? _showSignal;

    protected override async void OnStartup(StartupEventArgs e)
    {
        Updater.WaitForPreviousCopy(e.Args);   // started by an update: let the old copy finish closing first
        DataMigration.FromAltDeck();           // RoDeck used to be AltDeck: bring its accounts and settings over (once)

        // the old AltDeck.exe still running would fight RoDeck over Roblox's lock, the clients and the hotkeys
        if (Mutex.TryOpenExisting(@"Local\AltDeck.SingleInstance", out var oldApp))
        {
            oldApp.Dispose();
            MessageBox.Show("AltDeck (the old name of RoDeck) is still running. Close it first (right-click its tray icon → Quit), then open RoDeck again.",
                            "RoDeck", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        _instance = new Mutex(true, InstanceName, out bool firstCopy);
        if (!firstCopy)
        {
            // RoDeck is already running (maybe hidden in the tray): bring that one to the front instead.
            try { using var signal = EventWaitHandle.OpenExisting(ShowSignalName); signal.Set(); } catch { }
            Shutdown();
            return;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        new Thread(() =>
        {
            while (_showSignal.WaitOne())
                Dispatcher.BeginInvoke(new Action(() => (MainWindow as MainWindow)?.BringToFront()));
        }) { IsBackground = true, Name = "RoDeck show signal" }.Start();

        // Show errors instead of closing silently.
        DispatcherUnhandledException += (_, ex) =>
        {
            ActivityLog.Shared.Add(ActivityLog.Error, "Unexpected error: " + ex.Exception.Message);
            MessageBox.Show(ex.Exception.ToString(), "RoDeck error");
            ex.Handled = true;
        };
        base.OnStartup(e);
        TitleBar.HookAllWindows();

        try
        {
            // The unlock dialog and the splash come before the main window, so closing them must not end the app by itself.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var settings = new SettingsStore().Load();      // no secrets in there; read now so the first windows match the theme
            ThemeManager.Apply(settings.Theme, settings.Accent);

            // With a master password the vault must be unlocked before the main window (and its view-model) loads the accounts.
            if (Vault.Shared.IsPasswordProtected)
            {
                var typed = PasswordDialog.Ask(
                    null, "Unlock RoDeck",
                    "Enter your master password to open the vault.\n\nForgot it? It can't be recovered. Close RoDeck, delete vault.dat in %AppData%\\RoDeck, and add your accounts again.",
                    new[] { "Master password" },
                    v => Vault.Shared.TryUnlock(v[0]) ? null : "That password isn't right.",
                    cancelText: "Quit");
                if (typed == null)
                {
                    Shutdown();
                    return;
                }
            }

            SplashWindow? splash = null;
            Task? intro = null;
            if (!settings.SkipSplash)
            {
                splash = new SplashWindow();
                splash.Show();
                await Dispatcher.Yield(DispatcherPriority.Background);   // let the splash draw before the heavy work starts
                intro = splash.PlayAsync();
            }

            // Build the main window while the splash plays (it loads the vault and checks sign-ins).
            var main = new MainWindow();
            MainWindow = main;

            if (intro != null) await intro;

            main.Show();
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            if (splash != null) await splash.FadeOutAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "RoDeck couldn't start");
            Shutdown();
        }
    }
}
