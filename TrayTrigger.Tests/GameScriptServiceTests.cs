using System.Diagnostics;
using System.IO;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

public class GameScriptServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly GameEntry _game = new()
    {
        Id = "abc123",
        Name = "Test Game",
        ExecutablePath = @"C:\Games\Test\game.exe"
    };

    public GameScriptServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TrayTriggerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string MakeScript(string name)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, "rem test");
        return path;
    }

    [Fact]
    public void ReturnsNull_WhenScriptMissing()
    {
        var psi = GameScriptService.BuildStartInfo(Path.Combine(_dir, "nope.bat"), _game, GameScriptService.PhasePreLaunch, hidden: true, elevated: false, playedMinutes: null);
        Assert.Null(psi);
    }

    [Theory]
    [InlineData("pre.bat")]
    [InlineData("pre.cmd")]
    public void BatchScripts_RunViaCmd(string fileName)
    {
        string path = MakeScript(fileName);
        var psi = GameScriptService.BuildStartInfo(path, _game, GameScriptService.PhasePreLaunch, hidden: true, elevated: false, playedMinutes: null)!;

        Assert.Equal("cmd.exe", psi.FileName);
        Assert.Equal(new[] { "/c", path, "prelaunch", "Test Game", @"C:\Games\Test\game.exe" }, psi.ArgumentList);
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.CreateNoWindow);
        Assert.Equal(_dir, psi.WorkingDirectory);
    }

    [Fact]
    public void PowerShellScripts_BypassExecutionPolicy_AndHideWindow()
    {
        string path = MakeScript("pre.ps1");
        var psi = GameScriptService.BuildStartInfo(path, _game, GameScriptService.PhasePreLaunch, hidden: true, elevated: false, playedMinutes: null)!;

        Assert.Equal("powershell.exe", psi.FileName);
        Assert.Equal(
            new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", path, "prelaunch", "Test Game", @"C:\Games\Test\game.exe" },
            psi.ArgumentList);
    }

    [Fact]
    public void PowerShellScripts_Visible_OmitWindowStyle()
    {
        string path = MakeScript("pre.ps1");
        var psi = GameScriptService.BuildStartInfo(path, _game, GameScriptService.PhasePreLaunch, hidden: false, elevated: false, playedMinutes: null)!;

        Assert.DoesNotContain("-WindowStyle", psi.ArgumentList);
        Assert.False(psi.CreateNoWindow);
        Assert.Equal(ProcessWindowStyle.Normal, psi.WindowStyle);
    }

    [Fact]
    public void Executables_RunDirectly()
    {
        string path = MakeScript("tool.exe");
        var psi = GameScriptService.BuildStartInfo(path, _game, GameScriptService.PhasePostExit, hidden: true, elevated: false, playedMinutes: 42)!;

        Assert.Equal(path, psi.FileName);
        Assert.Equal(new[] { "postexit", "Test Game", @"C:\Games\Test\game.exe" }, psi.ArgumentList);
    }

    [Fact]
    public void Environment_CarriesGameContext_WhenNotElevated()
    {
        string path = MakeScript("post.bat");
        var psi = GameScriptService.BuildStartInfo(path, _game, GameScriptService.PhasePostExit, hidden: true, elevated: false, playedMinutes: 42)!;

        Assert.Equal("postexit", psi.Environment["TRAYTRIGGER_PHASE"]);
        Assert.Equal("Test Game", psi.Environment["TRAYTRIGGER_GAME_NAME"]);
        Assert.Equal("abc123", psi.Environment["TRAYTRIGGER_GAME_ID"]);
        Assert.Equal(@"C:\Games\Test\game.exe", psi.Environment["TRAYTRIGGER_GAME_EXE"]);
        Assert.Equal("42", psi.Environment["TRAYTRIGGER_PLAYTIME_MINUTES"]);
    }

    [Fact]
    public void PreLaunch_HasNoPlaytimeVariable()
    {
        string path = MakeScript("pre.bat");
        var psi = GameScriptService.BuildStartInfo(path, _game, GameScriptService.PhasePreLaunch, hidden: true, elevated: false, playedMinutes: null)!;

        Assert.False(psi.Environment.ContainsKey("TRAYTRIGGER_PLAYTIME_MINUTES"));
    }

    [Fact]
    public void Elevated_UsesShellExecuteRunas_AndStillPassesArguments()
    {
        string path = MakeScript("pre.bat");
        var psi = GameScriptService.BuildStartInfo(path, _game, GameScriptService.PhasePreLaunch, hidden: true, elevated: true, playedMinutes: null)!;

        Assert.True(psi.UseShellExecute);
        Assert.Equal("runas", psi.Verb);
        Assert.False(psi.CreateNoWindow); // not honoured by ShellExecute; WindowStyle is used instead
        Assert.Equal(ProcessWindowStyle.Hidden, psi.WindowStyle);
        Assert.Equal("cmd.exe", psi.FileName);
        Assert.Contains("prelaunch", psi.ArgumentList);
        Assert.Contains("Test Game", psi.ArgumentList);
    }

    [Fact]
    public void QuotedPath_IsUnwrapped()
    {
        string path = MakeScript("pre.bat");
        var psi = GameScriptService.BuildStartInfo($"\"{path}\"", _game, GameScriptService.PhasePreLaunch, hidden: true, elevated: false, playedMinutes: null);

        Assert.NotNull(psi);
        Assert.Equal(path, psi!.ArgumentList[1]);
    }

    [Theory]
    [InlineData("macro.ahk")]
    [InlineData("tool.py")]
    [InlineData("noext")]
    public void UnsupportedExtension_IsRefused(string fileName)
    {
        string path = MakeScript(fileName);
        Assert.False(GameScriptService.IsSupportedScript(path));
        Assert.Null(GameScriptService.BuildStartInfo(path, _game, GameScriptService.PhasePreLaunch, hidden: true, elevated: false, playedMinutes: null));
    }

    [Theory]
    [InlineData(@"C:\s\a.bat", true)]
    [InlineData(@"C:\s\a.CMD", true)]
    [InlineData(@"C:\s\a.ps1", true)]
    [InlineData(@"C:\s\a.exe", true)]
    [InlineData("\"C:\\s\\a.exe\"", true)]
    [InlineData(@"C:\s\a.vbs", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSupportedScript_ChecksExtensionOnly(string? path, bool expected)
    {
        Assert.Equal(expected, GameScriptService.IsSupportedScript(path));
    }

    [Fact]
    public void FilterPattern_ListsEverySupportedExtension()
    {
        Assert.Equal("*.bat;*.cmd;*.ps1;*.exe;*.com", GameScriptService.SupportedExtensionsFilterPattern);
    }

    [Fact]
    public void RunPreLaunch_WithWait_ActuallyRunsBatchToCompletion()
    {
        // End-to-end: a real cmd.exe script writes its arguments and env to a file.
        string marker = Path.Combine(_dir, "marker.txt");
        string script = Path.Combine(_dir, "pre.bat");
        File.WriteAllText(script, $"@echo %~1;%~2;%TRAYTRIGGER_GAME_ID%> \"{marker}\"\r\n");

        var game = new GameEntry
        {
            Id = "e2e",
            Name = "E2E Game",
            ExecutablePath = @"C:\x\y.exe",
            PreLaunchScriptPath = script,
            WaitForPreLaunchScript = true,
            RunScriptsHidden = true
        };

        new GameScriptService().RunPreLaunch(game);

        Assert.True(File.Exists(marker), "script did not run to completion before RunPreLaunch returned");
        Assert.Equal("prelaunch;E2E Game;e2e", File.ReadAllText(marker).Trim());
    }

    [Fact]
    public void PendingPostExit_RunsOnShutdown_OnlyForTrackedGames()
    {
        string markerA = Path.Combine(_dir, "a.txt");
        string scriptA = Path.Combine(_dir, "postA.bat");
        File.WriteAllText(scriptA, $"@echo done> \"{markerA}\"\r\n");

        string markerB = Path.Combine(_dir, "b.txt");
        string scriptB = Path.Combine(_dir, "postB.bat");
        File.WriteAllText(scriptB, $"@echo done> \"{markerB}\"\r\n");

        var tracked = new GameEntry { Id = "a", Name = "A", PostExitScriptPath = scriptA };
        var untracked = new GameEntry { Id = "b", Name = "B", PostExitScriptPath = scriptB };

        var svc = new GameScriptService();
        svc.TrackPostExit(tracked);
        // 'untracked' was never registered (e.g. protocol launch) so must not fire.

        svc.RunPendingPostExitScriptsOnShutdown();

        // Post-exit is fire-and-forget; give cmd.exe a moment.
        for (int i = 0; i < 50 && !File.Exists(markerA); i++) Thread.Sleep(100);

        Assert.True(File.Exists(markerA));
        Assert.False(File.Exists(markerB));

        // Running again is a no-op: the pending list was cleared.
        File.Delete(markerA);
        svc.RunPendingPostExitScriptsOnShutdown();
        Thread.Sleep(300);
        Assert.False(File.Exists(markerA));
    }
}
