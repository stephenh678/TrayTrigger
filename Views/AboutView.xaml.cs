using System.Windows.Controls;

namespace TrayTrigger.Views;

/// <summary>
/// The About page: overview, key features, keyboard shortcuts and support links, with a card
/// search across them. It inherits the window's MainViewModel as its DataContext.
/// </summary>
public partial class AboutView : UserControl
{
    public AboutView()
    {
        InitializeComponent();
    }

    /// <summary>The page's card search box, for the window's Ctrl+F / Escape handling.</summary>
    public SearchBox PageSearchBox => AboutSearchBox;
}
