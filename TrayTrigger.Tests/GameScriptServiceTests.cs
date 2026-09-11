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

    // --- Settings default scripts ---

    private static ScriptDefaults Defaults(string? pre = null, string? post = null) => new()
    {
        PreLaunchScriptPath = pre ?? string.Empty,
        PostExitScriptPath = post ?? string.Empty,
        WaitForPreLaunchScript = false,
        PreLaunchScriptTimeoutSeconds = 99,
        AbortLaunchOnScriptFailure = true,
        RunScriptsHidden = false,
        RunScriptsAsAdmin = true
    };

    [Fact]
    public void Resolve_GameOwnScriptWins_AndUsesGameOptions()
    {
        var game = new GameEntry { PreLaunchScriptPath = @"C:\g\own.bat", WaitForPreLaunchScript = true, PreLaunchScriptTimeoutSeconds = 12, RunScriptsHidden = true, RunScriptsAsAdmin = false };
        var pre = GameScriptService.ResolvePreLaunch(game, Defaults(pre: @"C:\d\def.bat"))!.Value;

        Assert.Equal(@"C:\g\own.bat", pre.Path);
        Assert.False(pre.IsDefault);
        Assert.True(pre.Wait);
        Assert.Equal(12, pre.TimeoutSeconds);
        Assert.False(pre.AbortOnFailure);
        Assert.True(pre.Hidden);
        Assert.False(pre.Elevated);
    }

    [Fact]
    public void Resolve_DefaultUsed_WhenGameHasNone_WithDefaultOptions()
    {
        var game = new GameEntry { RunScriptsHidden = true, RunScriptsAsAdmin = false };
        var pre = GameScriptService.ResolvePreLaunch(game, Defaults(pre: @"C:\d\def.bat"))!.Value;

        Assert.Equal(@"C:\d\def.bat", pre.Path);
        Assert.True(pre.IsDefault);
        Assert.False(pre.Wait);
        Assert.Equal(99, pre.TimeoutSeconds);
        Assert.True(pre.AbortOnFailure);
        Assert.False(pre.Hidden);
        Assert.True(pre.Elevated);
    }

    [Fact]
    public void Resolve_IsPerPhase()
    {
        // Own pre-launch, no own post-exit: the default post-exit still applies.
        var game = new GameEntry { PreLaunchScriptPath = @"C:\g\own.bat" };
        var defaults = Defaults(pre: @"C:\d\pre.bat", post: @"C:\d\post.bat");

        Assert.False(GameScriptService.ResolvePreLaunch(game, defaults)!.Value.IsDefault);
        var post = GameScriptService.ResolvePostExit(game, defaults);
        Assert.True(post!.Value.IsDefault);
        Assert.Equal(@"C:\d\post.bat", post.Value.Path);
    }

    [Fact]
    public void Resolve_SkipDefaultScripts_BlocksDefaultsOnly()
    {
        var game = new GameEntry { SkipDefaultScripts = true, PostExitScriptPath = @"C:\g\post.bat" };
        var defaults = Defaults(pre: @"C:\d\pre.bat", post: @"C:\d\post.bat");

        Assert.Null(GameScriptService.ResolvePreLaunch(game, defaults));
        Assert.Equal(@"C:\g\post.bat", GameScriptService.ResolvePostExit(game, defaults)!.Value.Path);
    }

    [Fact]
    public void Resolve_DisabledDefaults_AreIgnored_ButOwnScriptsStillRun()
    {
        var defaults = Defaults(pre: @"C:\d\pre.bat", post: @"C:\d\post.bat");
        defaults.Enabled = false;

        Assert.Null(GameScriptService.ResolvePreLaunch(new GameEntry(), defaults));
        Assert.Null(GameScriptService.ResolvePostExit(new GameEntry(), defaults));
        Assert.Equal(@"C:\g\own.bat", GameScriptService.ResolvePreLaunch(new GameEntry { PreLaunchScriptPath = @"C:\g\own.bat" }, defaults)!.Value.Path);
    }

    [Fact]
    public void Resolve_NoDefaultsAndNoOwn_IsNull()
    {
        var game = new GameEntry();
        Assert.Null(GameScriptService.ResolvePreLaunch(game, null));
        Assert.Null(GameScriptService.ResolvePreLaunch(game, Defaults()));
        Assert.Null(GameScriptService.ResolvePostExit(game, Defaults()));
    }

    [Fact]
    public void RunPreLaunch_RunsDefaultScript_WithGameArguments_WhenGameHasNone()
    {
        string marker = Path.Combine(_dir, "def.txt");
        string script = Path.Combine(_dir, "def.bat");
        File.WriteAllText(script, $"@echo %~1;%~2;%~4;%~6> \"{marker}\"\r\n");

        var defaults = new ScriptDefaults { PreLaunchScriptPath = script, WaitForPreLaunchScript = true, RunScriptsHidden = true };
        var game = new GameEntry { Id = "g1", Name = "Game One", ExecutablePath = @"C:\x\y.exe", ScriptArguments = "Gaming" };

        var svc = new GameScriptService(defaults: () => defaults);
        var result = svc.RunPreLaunch(game);

        Assert.True(result.ProceedWithLaunch);
        Assert.True(File.Exists(marker), "default script did not run");
        Assert.Equal("prelaunch;Game One;g1;Gaming", File.ReadAllText(marker).Trim());
    }

    [Fact]
    public void RunPreLaunch_DefaultAbortOnFailure_CancelsLaunch()
    {
        string script = Path.Combine(_dir, "fail.bat");
        File.WriteAllText(script, "@exit /b 5\r\n");
        var defaults = new ScriptDefaults { PreLaunchScriptPath = script, AbortLaunchOnScriptFailure = true };

        var result = new GameScriptService(defaults: () => defaults).RunPreLaunch(new GameEntry { Name = "G" });

        Assert.False(result.ProceedWithLaunch);
        Assert.Contains("exited with code 5", result.AbortReason);
    }

    [Fact]
    public void TrackPostExit_TracksDefaultPostExit_AndRunsItOnShutdown()
    {
        string marker = Path.Combine(_dir, "defpost.txt");
        string script = Path.Combine(_dir, "defpost.bat");
        File.WriteAllText(script, $"@echo %~1;[%~5]> \"{marker}\"\r\n");
        var defaults = new ScriptDefaults { PostExitScriptPath = script };

        var svc = new GameScriptService(defaults: () => defaults);
        svc.TrackPostExit(new GameEntry { Id = "t", Name = "T" });
        svc.RunPendingPostExitScriptsOnShutdown();

        // Fire-and-forget: wait for cmd.exe to both create and finish writing the marker.
        string content = string.Empty;
        for (int i = 0; i < 50 && content.Length == 0; i++)
        {
            Thread.Sleep(100);
            if (File.Exists(marker)) content = File.ReadAllText(marker).Trim();
        }
        Assert.Equal("postexit;[0]", content);
    }

    [Fact]
    public void TrackPostExit_IgnoresGame_ThatOptedOutOfDefaults()
    {
        string marker = Path.Combine(_dir, "skip.txt");
        string script = Path.Combine(_dir, "skip.bat");
        File.WriteAllText(script, $"@echo ran> \"{marker}\"\r\n");
        var defaults = new ScriptDefaults { PostExitScriptPath = script };

        var svc = new GameScriptService(defaults: () => defaults);
        svc.TrackPostExit(new GameEntry { Id = "s", Name = "S", SkipDefaultScripts = true });
        svc.RunPendingPostExitScriptsOnShutdown();

        Thread.Sleep(500);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void ScriptArguments_Batch_AppendedRaw_AfterPositionalArgs()
    {
        string path = MakeScript("pre.bat");
        var game = new GameEntry { Id = "id1", Name = "G", ExecutablePath = @"C:\g\g.exe", ScriptArguments = "\"C:\\My Saves\" Gaming & extra" };
        var psi = GameScriptService.BuildStartInfo(path, game, GameScriptService.PhasePreLaunch, hidden: true, elevated: false, playedMinutes: null)!;

        // Raw: the user's own quoting and metacharacters reach cmd.exe untouched, inside the /s wrapper.
        Assert.EndsWith(" \"id1\" \"\" \"C:\\My Saves\" Gaming & extra\"", psi.Arguments);
    }

    [Fact]
    public void ScriptArguments_PowerShell_Tokenised_AfterPositionalArgs()
    {
        string path = MakeScript("pre.ps1");
        var game = new GameEntry { Id = "id1", Name = "G", ExecutablePath = @"C:\g\g.exe", ScriptArguments = "\"C:\\My Saves\" Gaming" };
        var psi = GameScriptService.BuildStartInfo(path, game, GameScriptService.PhasePreLaunch, hidden: true, elevated: false, playedMinutes: null)!;

        Assert.Equal(new[] { "id1", "", @"C:\My Saves", "Gaming" }, psi.ArgumentList.TakeLast(4));
    }

    [Fact]
    public void ScriptArguments_Empty_AddsNothing()
    {
        string path = MakeScript("pre.ps1");
        var game = new GameEntry { Id = "id1", Name = "G", ExecutablePath = @"C:\g\g.exe", ScriptArguments = "   " };
        var psi = GameScriptService.BuildStartInfo(path, game, GameScriptService.PhasePreLaunch, hidden: true, elevated: false, playedMinutes: null)!;

        Assert.Equal("", psi.ArgumentList[^1]);
        Assert.Equal("id1", psi.ArgumentList[^2]);

        string bat = MakeScript("pre.bat");
        var batPsi = GameScriptService.BuildStartInfo(bat, game, GameScriptService.PhasePreLaunch, hidden: true, elevated: false, playedMinutes: null)!;
        Assert.EndsWith(" \"id1\" \"\"\"", batPsi.Arguments);
    }

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("", new string[0])]
    [InlineData("a b", new[] { "a", "b" })]
    [InlineData("  a   b  ", new[] { "a", "b" })]
    [InlineData("\"C:\\My Saves\" Gaming", new[] { @"C:\My Saves", "Gaming" })]
    [InlineData("say \\\"hi\\\"", new[] { "say", "\"hi\"" })]
    [InlineData("C:\\path\\ trailing", new[] { @"C:\path\", "trailing" })]
    public void SplitScriptArguments_FollowsWindowsRules(string? input, string[] expected)
    {
        Assert.Equal(expected, GameScriptService.SplitScriptArguments(input));
    }

    [Fact]
    public void TestRun_PassesScriptArguments_ToBatch()
    {
        string script = Path.Combine(_dir, "args.bat");
        File.WriteAllText(script, "@echo [%~6] [%~7]\r\n");
        var game = new GameEntry { Id = "id1", Name = "G", ExecutablePath = @"C:\g\g.exe", ScriptArguments = "\"C:\\My Saves\" Gaming" };

        var r = GameScriptService.TestRun(script, game, GameScriptService.PhasePreLaunch, playedMinutes: null);

        Assert.True(r.Succeeded);
        Assert.Equal(@"[C:\My Saves] [Gaming]", r.Output.Trim());
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
