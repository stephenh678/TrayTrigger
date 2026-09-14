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
}
