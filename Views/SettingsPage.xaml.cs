using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AltDeck.ViewModels;

namespace AltDeck.Views;

public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.ScrollToSection -= OnScrollTo;
                vm.ScrollToSection += OnScrollTo;
            }
        };
    }

    private void OnScrollTo(string section)
    {
        FrameworkElement? target = section switch
        {
            "General" => SecGeneral,
            "Automation" => SecAutomation,
            "Marketplace" => SecMarket,
            "Security" => SecSecurity,
            "Data" => SecData,
            "About" => SecAbout,
            "Launching" => SecLaunching,
            "Shortcuts" => SecShortcuts,
            _ => null
        };
        // let the page become visible first, then scroll
        Dispatcher.BeginInvoke(new Action(() => target?.BringIntoView()), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>The hotkey box records the keys you press instead of letting you type, so typos can't happen.</summary>
    private void Hotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab) return;                      // keep Tab for moving between controls
        e.Handled = true;
        if (DataContext is not MainViewModel vm) return;

        var text = AltDeck.Services.HotkeyService.FromKeyPress(key, Keyboard.Modifiers);
        if (text == null) return;                         // a lone modifier: wait for the real key
        vm.Settings.PanicHotkey = text.Length == 0 ? "off" : text;
    }

    private void ChooseBg_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp",
            CheckFileExists = true
        };
        if (dlg.ShowDialog() == true && DataContext is MainViewModel vm)
            vm.Settings.BackgroundImage = dlg.FileName;
    }

    private void ClearBg_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.Settings.BackgroundImage = "";
    }
}
