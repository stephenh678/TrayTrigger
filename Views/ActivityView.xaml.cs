using System.Windows.Controls;
using System.Windows.Input;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Views;

/// <summary>Activity &amp; History. All the logic is in <see cref="ActivityViewModel"/>.</summary>
public partial class ActivityView : UserControl
{
    public ActivityView()
    {
        InitializeComponent();
    }

    /// <summary>Esc clears the search from anywhere on the page, as on the Library and Tools pages.</summary>
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not ActivityViewModel vm || vm.SearchText.Length == 0) return;
        vm.SearchText = string.Empty;
        e.Handled = true;
    }
}
