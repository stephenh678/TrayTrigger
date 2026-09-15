using System.ComponentModel;
using System.IO;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>How a tool is started: working folder, elevation verb, and a declined admin prompt treated as a cancel.</summary>
public class ToolLauncherServiceTests
{
    [Fact]
    public void DeclinedAdminPrompt_IsACancel_NotAFailure()
    {
        Assert.Equal(ToolLaunchOutcome.Cancelled, ToolLauncherService.ClassifyStartFailure(new Win32Exception(1223)));
        Assert.Equal(ToolLaunchOutcome.Failed, ToolLauncherService.ClassifyStartFailure(new Win32Exception(2)));
        Assert.Equal(ToolLaunchOutcome.Failed, ToolLauncherService.ClassifyStartFailure(new InvalidOperationException("x")));
    }

    [Fact]
    public void RunAsAdmin_UsesTheRunasVerb_OtherwiseNoVerb()
    {
        var tool = new ToolEntry { TargetPath = @"C:\Tools\DLSS Swapper.exe", Arguments = "--silent" };
        var normal = ToolLauncherService.BuildStartInfo(tool, _ => false);
        Assert.True(normal.UseShellExecute);
        Assert.Equal(string.Empty, normal.Verb);
        Assert.Equal("--silent", normal.Arguments);

        tool.RunAsAdmin = true;
        Assert.Equal("runas", ToolLauncherService.BuildStartInfo(tool, _ => false).Verb);
    }

    [Fact]
    public void WorkingFolder_IsTheToolsOwn_WhenItExists_ElseTheProgramsFolder()
    {
        var tool = new ToolEntry { TargetPath = @"C:\Tools\Vortex\Vortex.exe", WorkingDirectory = @"D:\Mods" };
        Assert.Equal(@"D:\Mods", ToolLauncherService.ResolveWorkingDirectory(tool, _ => true));
        Assert.Equal(@"C:\Tools\Vortex", ToolLauncherService.ResolveWorkingDirectory(tool, _ => false));

        tool.WorkingDirectory = "";
        Assert.Equal(@"C:\Tools\Vortex", ToolLauncherService.ResolveWorkingDirectory(tool, _ => true));
    }

    [Fact]
    public void AlreadyRunning_IsAnyCopyOfTheExe_OnlyForAToolWithoutArguments()
    {
        Assert.True(ToolLauncherService.ChecksAnyRunningCopy(new ToolEntry { TargetPath = @"C:\Tools\Vortex.exe" }));
        Assert.True(ToolLauncherService.ChecksAnyRunningCopy(new ToolEntry { TargetPath = @"C:\Tools\Vortex.exe", Arguments = "  " }));
        Assert.False(ToolLauncherService.ChecksAnyRunningCopy(new ToolEntry { TargetPath = @"C:\Windows\explorer.exe", Arguments = @"D:\Mods" }));
    }

    [Fact]
    public void Launch_MissingProgram_IsMissing()
    {
        var tool = new ToolEntry { Name = "Gone", TargetPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe") };
        Assert.Equal(ToolLaunchOutcome.Missing, new ToolLauncherService().Launch(tool).Outcome);
    }

    [Fact]
    public void Launch_ExistingNonExe_IsRefused_NotRunByAssociation()
    {
        // .bat, .cmd and .ps1 are script tools now; any other script type still has only its file association to run it.
        string script = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vbs");
        File.WriteAllText(script, "' nothing");
        try
        {
            var result = new ToolLauncherService().Launch(new ToolEntry { Name = "Script", TargetPath = script });
            Assert.Equal(ToolLaunchOutcome.Failed, result.Outcome);
            Assert.Contains(".exe", result.Error);
        }
        finally
        {
            File.Delete(script);
        }
    }
}
