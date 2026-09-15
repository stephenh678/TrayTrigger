using System.IO;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>Script tools: which scripts are accepted, and how each is started - by its interpreter, visible, hidden or elevated.</summary>
public class ScriptToolTests
{
    private static readonly string CmdPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    private static readonly string PowerShellPath = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    [Theory]
    [InlineData(@"C:\Scripts\backup.bat")]
    [InlineData(@"C:\Scripts\Close Discord.CMD")]
    [InlineData(@"C:\Scripts\power plan.ps1")]
    [InlineData(@"C:\Scripts\100%.ps1")]
    public void ValidateTarget_AcceptsALocalScript(string path)
    {
        Assert.Null(ToolCatalog.ValidateTarget(path, _ => true, _ => false));
    }

    [Theory]
    [InlineData(@"C:\Scripts\run.vbs")]
    [InlineData(@"C:\Scripts\run.js")]
    [InlineData(@"C:\Scripts\run.py")]
    [InlineData(@"C:\Scripts\%TEMP%\run.bat")]
    [InlineData(@"C:\Scripts\50%off.cmd")]
    [InlineData(@"\\server\share\run.bat")]
    [InlineData(@"run.ps1")]
    public void ValidateTarget_RefusesOtherScriptsAndUnsafePaths(string path)
    {
        Assert.NotNull(ToolCatalog.ValidateTarget(path, _ => true, _ => false));
    }

    [Fact]
    public void IsScript_ForScriptTargetsOnly()
    {
        Assert.True(ToolCatalog.IsScript(new ToolEntry { TargetPath = @"C:\Scripts\backup.BAT" }));
        Assert.False(ToolCatalog.IsScript(new ToolEntry { TargetPath = @"C:\Tools\Vortex.exe" }));
        Assert.False(ToolCatalog.IsScript(new ToolEntry { AppId = "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", TargetPath = @"C:\x.bat" }));
    }

    [Fact]
    public void BatchScript_RunsInCommandPrompt_WithThePathQuotedAndArgumentsRaw()
    {
        var tool = new ToolEntry { TargetPath = @"C:\My Scripts\backup.bat", Arguments = "--full \"D:\\Save Games\"" };
        var start = ToolLauncherService.BuildStartInfo(tool, _ => false);

        Assert.Equal(CmdPath, start.FileName);
        Assert.Equal("/d /s /c \"\"C:\\My Scripts\\backup.bat\" --full \"D:\\Save Games\"\"", start.Arguments);
        Assert.Equal(@"C:\My Scripts", start.WorkingDirectory);
        Assert.True(start.UseShellExecute);
        Assert.False(start.CreateNoWindow);
        Assert.Equal(string.Empty, start.Verb);
    }

    [Fact]
    public void PowerShellScript_BypassesPolicy_AndSplitsArguments()
    {
        var tool = new ToolEntry { TargetPath = @"C:\Scripts\plan.ps1", Arguments = "-Plan \"High performance\"" };
        var start = ToolLauncherService.BuildStartInfo(tool, _ => false);

        Assert.Equal(PowerShellPath, start.FileName);
        Assert.Equal(["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", @"C:\Scripts\plan.ps1", "-Plan", "High performance"], start.ArgumentList);
    }

    [Fact]
    public void HiddenScript_HasNoWindow_AndItsOutputIsRead()
    {
        var tool = new ToolEntry { TargetPath = @"C:\Scripts\plan.ps1", HideWindow = true };
        var start = ToolLauncherService.BuildStartInfo(tool, _ => false);

        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
        Assert.Contains("Hidden", start.ArgumentList);
    }

    [Fact]
    public void ElevatedScript_GoesThroughRunas_EvenWhenHidden()
    {
        var tool = new ToolEntry { TargetPath = @"C:\Scripts\cleanup.cmd", HideWindow = true, RunAsAdmin = true };
        var start = ToolLauncherService.BuildStartInfo(tool, _ => false);

        Assert.Equal("runas", start.Verb);
        Assert.True(start.UseShellExecute);
        Assert.False(start.RedirectStandardOutput);
        Assert.Equal(System.Diagnostics.ProcessWindowStyle.Hidden, start.WindowStyle);
    }

    [Fact]
    public void HideWindow_DoesNothingForAProgram()
    {
        var start = ToolLauncherService.BuildStartInfo(new ToolEntry { TargetPath = @"C:\Tools\Vortex.exe", HideWindow = true }, _ => false);
        Assert.Equal(@"C:\Tools\Vortex.exe", start.FileName);
        Assert.True(start.UseShellExecute);
        Assert.False(start.CreateNoWindow);
    }

    [Fact]
    public void Script_OnlyCountsTheCopyItStarted_AsAlreadyRunning()
    {
        Assert.False(ToolLauncherService.ChecksAnyRunningCopy(new ToolEntry { TargetPath = @"C:\Scripts\backup.bat" }));
        Assert.True(ToolLauncherService.ChecksAnyRunningCopy(new ToolEntry { TargetPath = @"C:\Tools\Vortex.exe" }));
    }
}
