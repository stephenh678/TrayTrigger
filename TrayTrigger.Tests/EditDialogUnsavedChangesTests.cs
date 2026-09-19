using System.IO;
using System.Runtime.ExceptionServices;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// <see cref="GameEditViewModel.HasUnsavedChanges"/> and <see cref="ToolEditViewModel.HasUnsavedChanges"/>:
/// Esc in Edit Game and Edit Tool closes at once when nothing changed and asks first otherwise,
/// so the flag must stay false on open and turn true for any field Save writes back.
/// </summary>
public class EditDialogUnsavedChangesTests : IDisposable
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

    private IconExtractorService Icons() =>
        new(new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local")));

    private static GameEntry SampleGame() => new()
    {
        Name = "DOOM Eternal",
        Category = "Action",
        ExecutablePath = @"C:\Games\DOOM Eternal\DOOMEternalx64tk.exe",
        WorkingDirectory = @"C:\Games\DOOM Eternal",
        PreLaunchScriptPath = @"C:\Scripts\pre.ps1",
        PostExitScriptPath = @"C:\Scripts\post.ps1",
    };

    [Fact]
    public void GameEdit_NothingChanged_IsClean() => Sta(() =>
    {
        var vm = new GameEditViewModel(SampleGame(), new[] { "Action", "RPG" }, Icons());
        Assert.False(vm.HasUnsavedChanges);
    });

    [Theory]
    [InlineData("name")]
    [InlineData("category")]
    [InlineData("arguments")]
    [InlineData("hidden")]
    [InlineData("profile")]
    [InlineData("script")]
    [InlineData("timeout")]
    public void GameEdit_AnyEditedField_IsDirty(string field) => Sta(() =>
    {
        var vm = new GameEditViewModel(SampleGame(), new[] { "Action", "RPG" }, Icons());
        switch (field)
        {
            case "name": vm.Name = "DOOM"; break;
            case "category": vm.Category = "RPG"; break;
            case "arguments": vm.Arguments = "-skipintro"; break;
            case "hidden": vm.IsHidden = true; break;
            case "profile": vm.PerformanceProfile = PerformanceProfileMode.Optimized; break;
            case "script": vm.PostExitScriptPath = @"C:\Scripts\other.ps1"; break;
            case "timeout": vm.PreLaunchScriptTimeoutSeconds = "45"; break;
        }
        Assert.True(vm.HasUnsavedChanges);
    });

    [Fact]
    public void GameEdit_ChangedBack_IsCleanAgain() => Sta(() =>
    {
        var vm = new GameEditViewModel(SampleGame(), new[] { "Action" }, Icons());
        vm.Name = "Something else";
        vm.Name = "DOOM Eternal";
        Assert.False(vm.HasUnsavedChanges);
    });

    [Fact]
    public void ToolEdit_NothingChanged_IsClean_AndEditsAreDirty() => Sta(() =>
    {
        var tool = new ToolEntry { Name = "Vortex", Category = "Mods", TargetPath = @"C:\Tools\Vortex.exe" };
        var vm = new ToolEditViewModel(tool, new[] { "Mods" }, Icons());
        Assert.False(vm.HasUnsavedChanges);

        vm.IsFavorite = true;
        Assert.True(vm.HasUnsavedChanges);

        vm.IsFavorite = false;
        vm.Arguments = "--safe";
        Assert.True(vm.HasUnsavedChanges);
    });
}
