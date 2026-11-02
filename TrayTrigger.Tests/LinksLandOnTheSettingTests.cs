using System.Text.RegularExpressions;
using TrayTrigger.ViewModels;
using static TrayTrigger.Tests.SourceTree;

namespace TrayTrigger.Tests;

/// <summary>
/// The rule for links inside the app: a link that points at a setting lands on that setting. It switches to
/// the page and tab, scrolls to the card or row and outlines it, as choosing it in Search everything does. A
/// link that stops at the top of a tab leaves the person to hunt for what it promised. These guards keep each
/// kind of link to the rule: the jumps from About and the keys reminder (Settings cards), the "Your setup"
/// links and the hotkey list (Edit Game fields), and the tool hotkey.
/// </summary>
public class LinksLandOnTheSettingTests
{
    [Theory]
    [InlineData(MainViewModel.HotkeysCardTitle)]
    [InlineData(MainViewModel.DiagnosticsCardTitle)]
    [InlineData(MainViewModel.ArtSourcesCardTitle)]
    public void ASettingsCardAJumpLooksFor_IsHeadedWithThatTitle(string title)
    {
        string heading = title.Replace("&", "&amp;");
        Assert.Contains($"<TextBlock Text=\"{heading}\" FontSize=\"{{DynamicResource FontSizeSubhead}}\"", Read("Views/SettingsView.xaml"));
    }

    [Fact]
    public void EveryJumpToASettingsTab_AlsoShowsACard()
    {
        string source = Read("ViewModels/MainViewModel.cs");
        // The tab-strip buttons and the page reset use the All tab; any other tab set from code is a jump.
        int jumps = Regex.Matches(source, @"SettingsVM\.SelectedTab = SettingsCategoryTab\.(?!All\b)\w+;").Count;
        int jumpsThatShowACard = Regex.Matches(source,
            @"SettingsVM\.SelectedTab = SettingsCategoryTab\.(?!All\b)\w+;[^}]*?ShowSettingsCardRequested\?\.Invoke\(\w+CardTitle\)").Count;

        Assert.True(jumps > 0);
        Assert.Equal(jumps, jumpsThatShowACard);
    }

    [Fact]
    public void TheAboutLinks_ShowTheSetting_NotJustTheTab()
    {
        string about = Read("Views/AboutView.xaml");
        Assert.Contains("Command=\"{Binding ShowHotkeySettingsCommand}\"", about);
        Assert.Contains("Command=\"{Binding OpenDiagnosticsSettingsCommand}\"", about);
    }

    [Fact]
    public void TheYourSetupLinks_NameTheCardTheyEdit_AndEachCardIsNamedInTheEditor()
    {
        Assert.DoesNotContain("GameEditSection.", Read("ViewModels/GameSetupSummary.cs"));
        Assert.Contains("CommandParameter=\"{Binding Field}\"", Read("Views/GameDetailsDialog.xaml"));

        string panel = Read("Views/GameEditPanel.xaml");
        foreach (var field in new[]
                 {
                     GameEditViewModel.EditField.RunOptionsCard, GameEditViewModel.EditField.ProfileCard,
                     GameEditViewModel.EditField.DlssCard, GameEditViewModel.EditField.DisplayCard,
                     GameEditViewModel.EditField.ScriptsCard, GameEditViewModel.EditField.LaunchHotkey,
                 })
        {
            // The hotkey is a row (LaunchHotkeyRow); the others are cards named as the field is.
            string name = field == GameEditViewModel.EditField.LaunchHotkey ? "LaunchHotkeyRow" : field.ToString();
            Assert.Contains($"x:Name=\"{name}\"", panel);
        }
    }

    [Fact]
    public void TheHotkeyList_OpensTheEditorAtTheHotkey_ForGamesAndTools()
    {
        string main = Read("ViewModels/MainViewModel.cs");
        Assert.Contains("Library.ShowGameHotkey(g)", main);
        Assert.Contains("Tools.Edit(t, showHotkey: true)", main);
        Assert.Contains("FieldHighlight.Show(ToolHotkeyRow)", Read("Views/ToolEditDialog.xaml.cs"));
    }
}
