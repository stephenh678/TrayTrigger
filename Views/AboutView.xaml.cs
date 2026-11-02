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

    /// <summary>The panel of the page's sections, which the window's search reads (<see cref="CardSearch"/>).</summary>
    internal Panel CardsPanel => AboutCardsPanel;

    /// <summary>Back to the top, for when the page has been left.</summary>
    internal void ScrollToTop() => AboutScroll.ScrollToTop();
}
