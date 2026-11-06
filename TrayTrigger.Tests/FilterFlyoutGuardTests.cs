using Xunit;
using static TrayTrigger.Tests.SourceTree;

namespace TrayTrigger.Tests;

/// <summary>
/// The Library and the Tools page have one filter flyout, drawn in one place, so they look and behave alike.
/// A page that draws its own copy would drift from the other the first time one is changed.
/// </summary>
public class FilterFlyoutGuardTests
{
    [Theory]
    [InlineData("Views/LibraryView.xaml", "library/filtering")]
    [InlineData("Views/ToolsView.xaml", "tools/filtering")]
    public void ThePage_UsesTheSharedFlyout_AndDrawsNoFilterPopupOfItsOwn(string view, string helpTopic)
    {
        string xaml = Read(view);

        Assert.Contains("<views:FilterFlyout", xaml);
        Assert.Contains($"HelpTopic=\"{helpTopic}\"", xaml);
        Assert.DoesNotContain("FilterOptionToggle", xaml);
        Assert.DoesNotContain("<Popup x:Name=\"LibraryFilterPopup\"", xaml);
    }

    [Fact]
    public void BothFiltersAreBuiltOnTheSharedViewModel()
    {
        Assert.Contains(": FilterFlyoutViewModel", Read("ViewModels/LibraryFilterViewModel.cs"));
        Assert.Contains(": FilterFlyoutViewModel", Read("ViewModels/ToolFilterViewModel.cs"));
    }
}
