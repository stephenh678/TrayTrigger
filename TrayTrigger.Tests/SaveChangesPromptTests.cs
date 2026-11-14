using System.Windows;
using TrayTrigger.Views;

namespace TrayTrigger.Tests;

/// <summary>
/// Leaving an editor with unsaved changes asks the standard question: Save (the highlighted button, Enter),
/// Don't Save, Cancel (Esc). It used to offer only Discard (highlighted) or Keep Editing, with no way to save.
/// </summary>
public class SaveChangesPromptTests
{
    [Fact]
    public void ThePrompt_NamesWhatIsUnsaved_AndOffersSaveFirst() => StartupSmokeTests.WithAppResources(_ =>
    {
        var dialog = ModernDialog.CreateSaveChangesPrompt("Red Dead Redemption 2");
        try
        {
            Assert.Equal("Unsaved Changes", dialog.Title);
            Assert.Equal("Save your changes to Red Dead Redemption 2?", dialog.PrimaryMessageText.Text);

            Assert.Equal("Save", dialog.ConfirmBtn.Content);
            Assert.True(dialog.ConfirmBtn.IsDefault);              // Enter saves
            Assert.Equal("Don't Save", dialog.SecondaryBtn.Content);
            Assert.Equal(Visibility.Visible, dialog.SecondaryBtn.Visibility);
            Assert.False(dialog.SecondaryBtn.IsDefault);
            Assert.Equal("Cancel", dialog.CancelBtn.Content);
            Assert.True(dialog.CancelBtn.IsCancel);                // Esc goes back to editing
            Assert.Equal(DialogResultOption.Cancel, dialog.ChoiceResult); // closing it any other way keeps the edits
        }
        finally
        {
            dialog.Close();
        }
    });

    [Fact]
    public void WithNoName_ItStillAsks() => StartupSmokeTests.WithAppResources(_ =>
    {
        var dialog = ModernDialog.CreateSaveChangesPrompt("  ");
        try { Assert.Equal("Save your changes?", dialog.PrimaryMessageText.Text); }
        finally { dialog.Close(); }
    });

    // Every editor that guards unsaved changes asks this one question; the old Discard / Keep Editing prompt is gone.
    [Fact]
    public void NoEditorAsksTheOldDiscardQuestion()
    {
        var offenders = SourceTree.Sources("*.cs").Where(f => SourceTree.Read(f).Contains("Discard your changes?")).ToList();
        Assert.Empty(offenders);
        foreach (var editor in new[] { "Views/GameDetailsDialog.xaml.cs", "Views/ToolEditDialog.xaml.cs", "Views/ThemeEditDialog.xaml.cs" })
            Assert.Contains("PromptSaveChanges", SourceTree.Read(editor));
    }
}
