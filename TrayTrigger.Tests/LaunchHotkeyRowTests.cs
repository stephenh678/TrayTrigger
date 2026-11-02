using System.Windows.Input;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;
using static TrayTrigger.Tests.SourceTree;

namespace TrayTrigger.Tests;

/// <summary>About > Shortcuts' list of game and tool hotkeys: who is listed, in what order, and which are marked as not working.</summary>
public class LaunchHotkeyRowTests
{
    private static readonly ICommand NoEdit = new RelayCommand(() => { });

    private static (HotkeyBinding, ICommand) Game(string id, string name, string hotkey) =>
        (new HotkeyBinding(id, name, hotkey, HotkeyOwnerKind.Game), NoEdit);

    private static (HotkeyBinding, ICommand) Tool(string id, string name, string hotkey) =>
        (new HotkeyBinding(id, name, hotkey, HotkeyOwnerKind.Tool), NoEdit);

    [Fact]
    public void OnlyThoseWithAHotkeyAreListed_GamesFirstThenTools_EachByName()
    {
        var rows = LaunchHotkeyRow.Build(
            [Tool("t1", "Vortex", "Ctrl+Alt+V"), Game("g2", "Valheim", "Ctrl+Alt+H"), Game("g1", "Doom", "F9"),
             Game("g3", "Hades", ""), Tool("t2", "Afterburner", "  ")],
            new HashSet<string>());

        Assert.Equal(["Doom", "Valheim", "Vortex"], rows.Select(r => r.Name));
        Assert.Equal(["Game", "Game", "Tool"], rows.Select(r => r.KindLabel));
        Assert.Equal(["F9", "Ctrl+Alt+H", "Ctrl+Alt+V"], rows.Select(r => r.Hotkey));
    }

    [Fact]
    public void AHotkeyWindowsRefused_IsMarkedNotActive_AndTheOthersAreNot()
    {
        var rows = LaunchHotkeyRow.Build(
            [Game("g1", "Doom", "Ctrl+Alt+V"), Tool("t1", "Vortex", "Ctrl+Alt+V")],
            new HashSet<string> { "t1" });

        Assert.False(rows[0].HasNote);
        Assert.True(rows[1].HasNote);
        Assert.Equal(LaunchHotkeyRow.NotActiveNote, rows[1].Note);
    }

    [Fact]
    public void NoHotkeysAtAll_GivesAnEmptyList()
    {
        Assert.Empty(LaunchHotkeyRow.Build([Game("g1", "Doom", "")], new HashSet<string>()));
    }

    [Fact]
    public void TheAboutCard_NamesItsAppHotkeysButton_AndListsTheGameAndToolHotkeys()
    {
        string xaml = Read("Views/AboutView.xaml");

        Assert.Contains("Content=\"App Hotkeys...\"", xaml);
        Assert.DoesNotContain("Configure Hotkeys", xaml);
        Assert.Contains("ItemsSource=\"{Binding LaunchHotkeys}\"", xaml);
    }
}
