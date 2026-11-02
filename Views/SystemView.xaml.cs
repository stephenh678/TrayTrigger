using System.Windows.Controls;

namespace TrayTrigger.Views;

public partial class SystemView : UserControl
{
    public SystemView()
    {
        InitializeComponent();
    }

    /// <summary>The page's card search box, for the window's Ctrl+F / Escape handling.</summary>
    public SearchBox PageSearchBox => SystemSearchBox;

    /// <summary>The panel of the page's sections, which the window's search reads (<see cref="CardSearch"/>).</summary>
    internal Panel CardsPanel => SystemCardsPanel;

    /// <summary>Back to the top, for when the page has been left.</summary>
    internal void ScrollToTop() => ContentScroll.ScrollToTop();
}
