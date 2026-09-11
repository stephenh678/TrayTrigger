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
        // Every argument force-quoted and wrapped for /s - see BuildStartInfo's cmd.exe comment.
        // Argument 5 (playtime) is an empty quoted slot on pre-launch so %5 is stable across phases.
        Assert.Equal($"/d /s /c \"\"{path}\" \"prelaunch\" \"Test Game\" \"C:\\Games\\Test\\game.exe\" \"abc123\" \"\"\"", psi.Arguments);
        Assert.Empty(psi.ArgumentList);
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.CreateNoWindow);
        Assert.Equal(_dir, psi.WorkingDirectory);
    }

    [Fact]
    public void BatchScripts_QuoteShellMetacharactersInGameName()
    {
        string path = MakeScript("pre.bat");
        var game = new GameEntry { Name = "Portal & calc | \"quoted\"", ExecutablePath = @"C:\Games\P\p.exe" };
        var psi = GameScriptService.BuildStartInfo(path, game, GameScriptService.PhasePreLaunch, hidden: true, elevated: false, playedMinutes: null)!;

        // The name stays inside one quoted span (so & and | are literal to cmd.exe) and any
        // embedded quote is neutralised rather than allowed to close that span early.
        Assert.Contains("\"Portal & calc | 'quoted'\"", psi.Arguments);
        Assert.DoesNotContain("\"quoted\"", psi.Arguments);
    }

    [Fact]
    public void PowerShellScripts_BypassExecutionPolicy_AndHideWindow()
    {
        string path = MakeScript("pre.ps1");
        var psi = GameScriptService.BuildStartInfo(path, _game, GameScriptService.PhasePreLaunch, hidden: true, elevated: false, playedMinutes: null)!;

        Assert.Equal("powershell.exe", psi.FileName);
        Assert.Equal(
            new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", path, "prelaunch", "Test Game", @"C:\Games\Test\game.exe", "abc123", "" },
            psi.ArgumentList);
    }

    [Fact]
    public void BatchScripts_PostExit_PassesPlaytimeAsArgument5()
    {
        string path = MakeScript("post.bat");
        var psi = GameScriptService.BuildStartInfo(path, _game, GameScriptService.PhasePostExit, hidden: true, elevated: false, playedMinutes: 42)!;

        Assert.EndsWith(" \"abc123\" \"42\"\"", psi.Arguments);
    }

    [Fact]
    public void Elevated_StillGetsGameIdAndPlaytime_AsArguments()
    {
        // No environment block through ShellExecute/runas, so the arguments are the only channel.
        string path = MakeScript("post.ps1");
        var psi = GameScriptService.BuildStartInfo(path, _game, GameScriptService.PhasePostExit, hidden: true, elevated: true, playedMinutes: 7)!;

        Assert.True(psi.UseShellExecute);
        Assert.Equal(new[] { "abc123", "7" }, psi.ArgumentList.TakeLast(2));
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
        Assert.Equal(new[] { "postexit", "Test Game", @"C:\Games\Test\game.exe", "abc123", "42" }, psi.ArgumentList);
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
        Assert.Contains("\"prelaunch\"", psi.Arguments);
        Assert.Contains("\"Test Game\"", psi.Arguments);
    }

    [Fact]
    public void QuotedPath_IsUnwrapped()
    {
        string path = MakeScript("pre.bat");
        var psi = GameScriptService.BuildStartInfo($"\"{path}\"", _game, GameScriptService.PhasePreLaunch, hidden: true, elevated: false, playedMinutes: null);

        Assert.NotNull(psi);
        // The surrounding quotes are stripped before the path is re-quoted exactly once.
        Assert.StartsWith($"/d /s /c \"\"{path}\" ", psi!.Arguments);
    }

    [Fact]
    public void FeatureSwitch_Off_SkipsScripts()
    {
        string path = MakeScript("pre.bat");
        var game = new GameEntry { Name = "Test", ExecutablePath = @"C:\Games\T\t.exe", PreLaunchScriptPath = path, PostExitScriptPath = path };

        // With the Settings switch off nothing must run - so no exception, no process, and
        // (observable here) no post-exit tracking either.
        var svc = new GameScriptService(() => false);
        svc.RunPreLaunch(game);
        svc.RunPostExit(game, 1);
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
        // %~5 is the (empty) playtime slot on pre-launch; it must exist so positions don't shift.
        File.WriteAllText(script, $"@echo %~1;%~2;%~4;[%~5];%TRAYTRIGGER_GAME_ID%> \"{marker}\"\r\n");

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
        Assert.Equal("prelaunch;E2E Game;e2e;[];e2e", File.ReadAllText(marker).Trim());
    }

    [Fact]
    public void TestRun_CapturesOutputAndExitCode_AndPassesPositionalArgs()
    {
        string script = Path.Combine(_dir, "t.bat");
        File.WriteAllText(script, "@echo hello %~1 %~4 [%~5]\r\n@echo oops 1>&2\r\n@exit /b 3\r\n");

        var r = GameScriptService.TestRun(script, _game, GameScriptService.PhasePreLaunch, playedMinutes: null);

        Assert.True(r.Started);
        Assert.False(r.TimedOut);
        Assert.Equal(3, r.ExitCode);
        Assert.False(r.Succeeded);
        Assert.Contains("hello prelaunch abc123 []", r.Output);
        Assert.Contains("[stderr] oops", r.Output);
    }

    [Fact]
    public void TestRun_PostExit_PassesPlaytimeZero()
    {
        string script = Path.Combine(_dir, "t.bat");
        File.WriteAllText(script, "@echo %~1;%~5\r\n");

        var r = GameScriptService.TestRun(script, _game, GameScriptService.PhasePostExit, playedMinutes: 0);

        Assert.True(r.Succeeded);
        Assert.Equal("postexit;0", r.Output.Trim());
    }

    [Fact]
    public void TestRun_TimesOut_AndKillsTheScript()
    {
        string script = Path.Combine(_dir, "slow.bat");
        File.WriteAllText(script, "@ping -n 30 127.0.0.1 >nul\r\n");

        var r = GameScriptService.TestRun(script, _game, GameScriptService.PhasePreLaunch, playedMinutes: null, timeout: TimeSpan.FromSeconds(1));

        Assert.True(r.Started);
        Assert.True(r.TimedOut);
        Assert.Null(r.ExitCode);
        Assert.False(r.Succeeded);
        // Killed, not left to run out its 30 s ping.
        Assert.True(r.Elapsed < TimeSpan.FromSeconds(10), $"took {r.Elapsed}");
    }

    [Fact]
    public void TestRun_MissingOrUnsupportedScript_ReportsWithoutStarting()
    {
        var missing = GameScriptService.TestRun(Path.Combine(_dir, "nope.bat"), _game, GameScriptService.PhasePreLaunch, null);
        Assert.False(missing.Started);
        Assert.NotNull(missing.Error);

        string py = MakeScript("tool.py");
        var unsupported = GameScriptService.TestRun(py, _game, GameScriptService.PhasePreLaunch, null);
        Assert.False(unsupported.Started);
        Assert.Contains("Unsupported", unsupported.Error);
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
