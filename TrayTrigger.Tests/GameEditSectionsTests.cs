using System.IO;
using System.Runtime.ExceptionServices;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// Edit Game's tabs (All + one per card): which cards each tab shows, Ctrl+Tab order, the Scripts
/// tab following the scripts card, the Advanced launch options default, and where a value Save
/// refuses is brought into view.
/// </summary>
public class GameEditSectionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void Sta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private GameEditViewModel Create(GameEntry? game = null, bool scriptsEnabled = true) =>
        new(game ?? new GameEntry { Name = "Hades", Category = "Action" }, new[] { "Action" },
            new IconExtractorService(new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"))),
            scriptsEnabled: scriptsEnabled);

    [Fact]
    public void OpensOnAll_WithEveryCardShowing() => Sta(() =>
    {
        var vm = Create();
        Assert.Equal(GameEditSection.All, vm.SelectedSection);
        Assert.True(vm.ShowIdentityCard && vm.ShowLaunchCard && vm.ShowPerformanceCard && vm.ShowScriptsCardOnTab);
    });

    [Fact]
    public void SectionTab_ShowsOnlyItsCard() => Sta(() =>
    {
        var vm = Create();
        vm.SelectedSection = GameEditSection.Launch;
        Assert.True(vm.ShowLaunchCard);
        Assert.False(vm.ShowIdentityCard || vm.ShowPerformanceCard || vm.ShowScriptsCardOnTab);
    });

    [Fact]
    public void EditsSurviveSwitchingTabs() => Sta(() =>
    {
        var vm = Create();
        vm.SelectedSection = GameEditSection.Identity;
        vm.Name = "Hades II";
        vm.SelectedSection = GameEditSection.Performance;
        vm.SelectedSection = GameEditSection.All;
        Assert.Equal("Hades II", vm.Name);
        Assert.True(vm.HasUnsavedChanges);
    });

    [Fact]
    public void CtrlTab_CyclesAndWraps() => Sta(() =>
    {
        var vm = Create();
        vm.MoveSection(1);
        Assert.Equal(GameEditSection.Identity, vm.SelectedSection);
        vm.MoveSection(-1);
        vm.MoveSection(-1);
        Assert.Equal(GameEditSection.Scripts, vm.SelectedSection);
        vm.MoveSection(1);
        Assert.Equal(GameEditSection.All, vm.SelectedSection);
    });

    [Fact]
    public void ScriptsOff_HidesTheScriptsTab() => Sta(() =>
    {
        var vm = Create(scriptsEnabled: false);
        Assert.DoesNotContain(GameEditSection.Scripts, vm.AvailableSections);
        vm.SelectedSection = GameEditSection.Scripts;
        Assert.Equal(GameEditSection.All, vm.SelectedSection);
        Assert.True(vm.ShowScriptsStubOnTab);
        vm.MoveSection(-1);
        Assert.Equal(GameEditSection.Performance, vm.SelectedSection);
        Assert.False(vm.ShowScriptsStubOnTab);
    });

    [Fact]
    public void RejectedSave_OnAnotherTab_SwitchesToTheFieldsTab() => Sta(() =>
    {
        var vm = Create();
        GameEditViewModel.EditField? reported = null;
        vm.ValidationFailed += f => reported = f;
        vm.SteamAppId = "abc";
        vm.SelectedSection = GameEditSection.Launch;
        vm.SaveCommand.Execute(null);
        Assert.Equal(GameEditSection.Identity, vm.SelectedSection);
        Assert.Equal(GameEditViewModel.EditField.SteamAppId, reported);
        Assert.False(string.IsNullOrEmpty(vm.StatusMessage));
    });

    [Fact]
    public void RejectedSave_OnAll_StaysOnAll() => Sta(() =>
    {
        var vm = Create();
        vm.PreLaunchScriptTimeoutSeconds = "0";
        vm.PreLaunchScriptPath = string.Empty;
        vm.SaveCommand.Execute(null);
        Assert.Equal(GameEditSection.All, vm.SelectedSection);
    });

    [Fact]
    public void MissingScript_IsReportedOnTheScriptsTab() => Sta(() =>
    {
        var vm = Create();
        vm.PreLaunchScriptPath = Path.Combine(_root, "missing.ps1");
        vm.SelectedSection = GameEditSection.Identity;
        vm.SaveCommand.Execute(null);
        Assert.Equal(GameEditSection.Scripts, vm.SelectedSection);
    });

    [Theory]
    [InlineData("", "", false)]
    [InlineData(@"C:\Games\Hades", "", true)]
    [InlineData("", "-windowed", true)]
    public void AdvancedLaunchOptions_StartOpenOnlyWhenSet(string workingDirectory, string arguments, bool expected) => Sta(() =>
    {
        var vm = Create(new GameEntry { Name = "Hades", WorkingDirectory = workingDirectory, Arguments = arguments });
        Assert.Equal(expected, vm.ShowAdvancedLaunchOptions);
        Assert.False(vm.HasUnsavedChanges);
    });
}
