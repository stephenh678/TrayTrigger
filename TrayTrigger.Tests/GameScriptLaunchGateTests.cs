using System.IO;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>The "cancel the launch if the pre-launch script fails" gate, the per-game timeout, and
/// the cmd.exe percent-sign hardening.</summary>
public class GameScriptLaunchGateTests : IDisposable
{
    private readonly string _dir;

    public GameScriptLaunchGateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TrayTriggerGateTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        // Timeout_WithAbort_CancelsLaunch deliberately leaves its script running (the service
        // abandons a timed-out script rather than killing it), and that cmd.exe/ping.exe pair
        // keeps _dir as its working directory for a few more seconds. Retry until it lets go
        // instead of leaving an empty TrayTriggerGateTests_* folder in %TEMP% every run.
        for (int attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
                return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Thread.Sleep(250);
        }
    }

    private string Script(string name, string body)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, body);
        return path;
    }

    private GameEntry GameWith(string script, bool abort, bool wait = true, int timeoutSeconds = 30) => new()
    {
        Id = "g",
        Name = "Gate Game",
        ExecutablePath = @"C:\x\y.exe",
        PreLaunchScriptPath = script,
        WaitForPreLaunchScript = wait,
        AbortLaunchOnScriptFailure = abort,
        PreLaunchScriptTimeoutSeconds = timeoutSeconds,
        RunScriptsHidden = true
    };

    [Fact]
    public void NonZeroExit_WithoutAbort_StillProceeds()
    {
        var result = new GameScriptService().RunPreLaunch(GameWith(Script("fail.bat", "@exit /b 3\r\n"), abort: false));
        Assert.True(result.ProceedWithLaunch);
    }

    [Fact]
    public void NonZeroExit_WithAbort_CancelsLaunch()
    {
        var result = new GameScriptService().RunPreLaunch(GameWith(Script("fail.bat", "@exit /b 3\r\n"), abort: true));
        Assert.False(result.ProceedWithLaunch);
        Assert.Contains("exited with code 3", result.AbortReason);
    }

    [Fact]
    public void ZeroExit_WithAbort_Proceeds()
    {
        var result = new GameScriptService().RunPreLaunch(GameWith(Script("ok.bat", "@exit /b 0\r\n"), abort: true));
        Assert.True(result.ProceedWithLaunch);
    }

    [Fact]
    public void Abort_ImpliesWaiting_EvenWhenWaitIsOff()
    {
        var result = new GameScriptService().RunPreLaunch(GameWith(Script("fail.bat", "@exit /b 9\r\n"), abort: true, wait: false));
        Assert.False(result.ProceedWithLaunch);
    }

    [Fact]
    public void MissingScript_WithAbort_CancelsLaunch()
    {
        var result = new GameScriptService().RunPreLaunch(GameWith(Path.Combine(_dir, "nope.bat"), abort: true));
        Assert.False(result.ProceedWithLaunch);
        Assert.Contains("not found", result.AbortReason);
    }

    [Fact]
    public void Timeout_WithAbort_CancelsLaunch()
    {
        // A script that outlives a 1-second window.
        var script = Script("slow.bat", "@ping -n 6 127.0.0.1 > nul\r\n");
        var result = new GameScriptService().RunPreLaunch(GameWith(script, abort: true, timeoutSeconds: 1));
        Assert.False(result.ProceedWithLaunch);
        Assert.Contains("did not finish", result.AbortReason);
    }

    [Fact]
    public void FeatureOff_NeverAborts()
    {
        var result = new GameScriptService(() => false).RunPreLaunch(GameWith(Script("fail.bat", "@exit /b 3\r\n"), abort: true));
        Assert.True(result.ProceedWithLaunch);
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(-5, 30)]
    [InlineData(601, 30)]
    [InlineData(1, 1)]
    [InlineData(600, 600)]
    [InlineData(45, 45)]
    public void EffectiveTimeout_ClampsToRange(int configured, int expected)
    {
        var game = new GameEntry { PreLaunchScriptTimeoutSeconds = configured };
        Assert.Equal(TimeSpan.FromSeconds(expected), GameScriptService.EffectivePreLaunchTimeout(game));
    }

    [Fact]
    public void BatchScripts_StripPercentFromGameName_ButNotEnvironment()
    {
        var script = Script("pre.bat", "rem");
        var game = new GameEntry { Name = "%TEMP% Quest", ExecutablePath = @"C:\Games\q\q.exe" };
        var psi = GameScriptService.BuildStartInfo(script, game, GameScriptService.PhasePreLaunch, hidden: true, elevated: false, playedMinutes: null)!;

        Assert.Contains("\"TEMP Quest\"", psi.Arguments);
        Assert.DoesNotContain("%TEMP%", psi.Arguments);
        Assert.Equal("%TEMP% Quest", psi.Environment["TRAYTRIGGER_GAME_NAME"]);
    }

    [Fact]
    public void HiddenNonElevated_CapturesOutput_VisibleDoesNot()
    {
        var script = Script("pre.bat", "rem");
        var game = new GameEntry { Name = "G", ExecutablePath = @"C:\g\g.exe" };

        var hidden = GameScriptService.BuildStartInfo(script, game, GameScriptService.PhasePreLaunch, hidden: true, elevated: false, playedMinutes: null)!;
        Assert.True(hidden.RedirectStandardOutput);
        Assert.True(hidden.RedirectStandardError);

        var visible = GameScriptService.BuildStartInfo(script, game, GameScriptService.PhasePreLaunch, hidden: false, elevated: false, playedMinutes: null)!;
        Assert.False(visible.RedirectStandardOutput);

        // ShellExecute (needed for runas) can't redirect, so elevated never captures.
        var elevated = GameScriptService.BuildStartInfo(script, game, GameScriptService.PhasePreLaunch, hidden: true, elevated: true, playedMinutes: null)!;
        Assert.False(elevated.RedirectStandardOutput);
    }

    [Fact]
    public void HiddenScriptOutput_DoesNotBreakCompletion()
    {
        // A chatty script must still run to completion with its pipes drained.
        string marker = Path.Combine(_dir, "done.txt");
        var script = Script("chatty.bat", "@for /L %%i in (1,1,200) do @echo line %%i\r\n@echo done> \"" + marker + "\"\r\n");
        var result = new GameScriptService().RunPreLaunch(GameWith(script, abort: true));
        Assert.True(result.ProceedWithLaunch);
        Assert.True(File.Exists(marker));
    }
}
