using System.Windows;
using System.Windows.Controls;

namespace RoDeck.Views;

public partial class CommunitiesPage : UserControl
{
    public CommunitiesPage()
    {
        InitializeComponent();
        // coming back to the page, or opening another community: from the top
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) { ListScroll.ScrollToTop(); PageScroll.ScrollToTop(); } };
        PageScroll.IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) PageScroll.ScrollToTop(); };
    }
}
