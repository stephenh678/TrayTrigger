using TrayTrigger.Views;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>How wide the theme editor becomes when its live preview slides out, and what it takes back when the preview closes.</summary>
public class ThemeEditPreviewWidthTests
{
    [Fact]
    public void WithRoomOnTheScreen_TheWindowGrowsByTheWholePanel_AndTakesItBackOnClose()
    {
        var open = ThemeEditDialog.PlanPreview(open: true, width: 940, alreadyAdded: 0, previewWidth: 680, workWidth: 1920, minWidth: 820);
        Assert.Equal((1620.0, 680.0), open);

        var closed = ThemeEditDialog.PlanPreview(open: false, width: 1620, alreadyAdded: 680, previewWidth: 680, workWidth: 1920, minWidth: 820);
        Assert.Equal((940.0, 0.0), closed);
    }

    [Fact]
    public void OnANarrowScreen_ThePanelGetsWhatIsLeft_AndClosingGivesJustThatBack()
    {
        var open = ThemeEditDialog.PlanPreview(open: true, width: 940, alreadyAdded: 0, previewWidth: 680, workWidth: 1200, minWidth: 820);
        Assert.Equal((1200.0, 260.0), open);

        var closed = ThemeEditDialog.PlanPreview(open: false, width: 1200, alreadyAdded: 260, previewWidth: 680, workWidth: 1200, minWidth: 820);
        Assert.Equal((940.0, 0.0), closed);
    }

    [Theory]
    [InlineData(911)]   // a 1366 x 768 screen at 150%: the editor (940) is already wider than the work area
    [InlineData(853)]
    [InlineData(500)]   // narrower than the editor's own minimum
    public void WhenTheEditorAlreadyFillsTheScreen_ThePanelGetsNothing_NotANegativeWidth(double workWidth)
    {
        var (target, added) = ThemeEditDialog.PlanPreview(open: true, width: 940, alreadyAdded: 0, previewWidth: 680, workWidth: workWidth, minWidth: 820);

        // A negative width would throw when it is made into a column width, and the editor would not open.
        Assert.True(added >= 0, $"The panel was given {added} px.");
        Assert.True(target >= 820);
        Assert.Equal(0, added);
    }
}
