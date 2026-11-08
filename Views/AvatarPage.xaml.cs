using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RoDeck.Models;
using RoDeck.ViewModels;

namespace RoDeck.Views;

/// <summary>Avatar: the look of the account top right, its wardrobe, body and outfits.</summary>
public partial class AvatarPage : UserControl
{
    public AvatarPage() { InitializeComponent(); }

    private MainViewModel VM => (MainViewModel)DataContext;

    /// <summary>"Copy look to…": a menu of your other accounts. The look goes on that account right away.</summary>
    private void CopyLook_Click(object sender, RoutedEventArgs e)
    {
        var me = VM.CurrentAccount;
        var others = VM.Accounts.Where(a => a != me && a.StatusKind != "signedout").OrderBy(a => a.Label, StringComparer.OrdinalIgnoreCase).ToList();
        if (others.Count == 0) { VM.StatusMessage = "Add another account first."; return; }
        MenuHelper.Show(CopyBtn, others.Select(a => ((string)$"Copy to {a.Label}", (Action)(() => _ = VM.CopyLookToAsync(a)))).ToArray());
    }

    /// <summary>Right-click an outfit: rename or delete (delete asks once more).</summary>
    private void Outfit_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not OutfitInfo o) return;
        var target = (FrameworkElement)sender;
        MenuHelper.Show(target,
            ($"Wear {o.Name}", () => VM.WearOutfitCommand.Execute(o)),
            ("Rename…", () =>
            {
                var name = PromptDialog.Ask(Window.GetWindow(this), "Rename outfit", "New name:", o.Name);
                if (name != null) _ = VM.RenameOutfitAsync(o, name);
            }),
            ("Delete…", () => MenuHelper.Show(target,
                ($"Yes, delete {o.Name}", () => _ = VM.DeleteOutfitAsync(o)),
                ("Cancel", () => { }))));
        e.Handled = true;
    }
}
