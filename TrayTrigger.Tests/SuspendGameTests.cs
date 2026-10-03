using System.Diagnostics;
using System.IO;
using TrayTrigger.Models;
using TrayTrigger.Services;
using ThreadState = System.Diagnostics.ThreadState;

namespace TrayTrigger.Tests;

/// <summary>
/// Suspend and resume: the playtime clock leaves suspended time out, the tray says so, games with
/// anti-cheat are refused, and a process really is frozen and thawed - including one a previous
/// TrayTrigger left frozen when it stopped without exiting. The frozen process is a copy of ping.exe
/// under a unique name: harmless, windowless, and easy to clean up.
/// </summary>
public class SuspendGameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerSuspend", Guid.NewGuid().ToString("N"));
    private readonly List<Process> _started = new();

    public void Dispose()
    {
        foreach (var process in _started)
        {
            try
            {
                // A suspended process can still be killed; resume first anyway, for a tidy exit.
                ProcessSuspender.Resume(process.Id, process.StartTime.ToUniversalTime(), out _);
                if (!process.HasExited) process.Kill();
                process.WaitForExit(5000);
            }
            catch { }
            process.Dispose();
        }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private Process StartPing()
    {
        Directory.CreateDirectory(_root);
        string exe = Path.Combine(_root, "TTSuspend" + Guid.NewGuid().ToString("N")[..8] + ".exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe);
        var process = Process.Start(new ProcessStartInfo(exe, "-n 120 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
        _started.Add(process);
        return process;
    }

    private static bool IsFrozen(Process process)
    {
        process.Refresh();
        var threads = process.Threads.Cast<ProcessThread>().ToList();
        return threads.Count > 0 && threads.All(t => t.ThreadState == ThreadState.Wait && t.WaitReason == ThreadWaitReason.Suspended);
    }

    private static ActiveGameSession Session(string name = "Elden Ring") => new(new GameEntry { Name = name }, LaunchRoute.DirectExe);

    // ------------------------------------------------------------------ playtime and the tray

    [Fact]
    public void PlayedTime_LeavesSuspendedTimeOut_TheCurrentSuspensionIncluded()
    {
        var session = Session();
        var now = DateTime.UtcNow;
        session.GameStarted = true;
        session.StartedAt = now.AddMinutes(-60).ToLocalTime();
        session.SuspendedTotal = TimeSpan.FromMinutes(10);

        Assert.Equal(50, Math.Round(session.PlayedTime(now).TotalMinutes));

        session.IsSuspended = true;
        session.SuspendedAtUtc = now.AddMinutes(-15);
        Assert.Equal(35, Math.Round(session.PlayedTime(now).TotalMinutes));
    }

    [Fact]
    public void PlayedTime_IsZeroBeforeTheGameStarts()
    {
        Assert.Equal(TimeSpan.Zero, Session().PlayedTime(DateTime.UtcNow));
        Assert.Null(Session().PlaytimeStartUtc());
    }

    [Fact]
    public void ThePostExitScriptsStartTime_MovesLaterByTheTimeSuspended()
    {
        // A post-exit script run at TrayTrigger's shutdown works its playtime out from this.
        var session = Session();
        var now = DateTime.UtcNow;
        session.GameStarted = true;
        session.StartedAt = now.AddMinutes(-60).ToLocalTime();
        session.SuspendedTotal = TimeSpan.FromMinutes(20);

        var start = session.PlaytimeStartUtc()!.Value;
        Assert.Equal(40, Math.Round((DateTime.UtcNow - start).TotalMinutes));
    }

    [Fact]
    public void TheTrayTooltip_SaysSuspended_WithThePlaytimeSoFar()
    {
        var session = Session();
        var now = DateTime.Now;
        session.GameStarted = true;
        session.StartedAt = now.AddMinutes(-95);
        session.IsSuspended = true;
        session.SuspendedAtUtc = now.ToUniversalTime().AddMinutes(-20);

        Assert.Equal("Suspended: Elden Ring · 1h 15m", App.BuildTrayToolTipText([session], now));
    }

    // ------------------------------------------------------------------ anti-cheat

    [Theory]
    [InlineData("EasyAntiCheat", null, "Easy Anti-Cheat")]
    [InlineData("BattlEye", null, "BattlEye")]
    [InlineData(null, "start_protected_game.exe", "Easy Anti-Cheat")]
    [InlineData(null, "BEClient_x64.dll", "BattlEye")]
    public void AGameWithAntiCheatInItsFolder_IsFound(string? folder, string? file, string expected)
    {
        string game = Path.Combine(_root, "Game");
        Directory.CreateDirectory(game);
        if (folder != null) Directory.CreateDirectory(Path.Combine(game, folder));
        if (file != null) File.WriteAllText(Path.Combine(game, file), "");

        Assert.Equal(expected, AntiCheatDetector.Find(game, Path.Combine(game, "game.exe"), DateTime.UtcNow, () => []));
    }

    [Fact]
    public void AnUnrealGamesAntiCheat_AtItsRoot_IsFound_ThoughTheExeIsTwoFoldersDown()
    {
        string root = Path.Combine(_root, "Game");
        string binaries = Path.Combine(root, "Project", "Binaries", "Win64");
        Directory.CreateDirectory(binaries);
        Directory.CreateDirectory(Path.Combine(root, "Project", "EasyAntiCheat"));

        Assert.Equal("Easy Anti-Cheat", AntiCheatDetector.Find(root, Path.Combine(binaries, "Game-Win64-Shipping.exe"), DateTime.UtcNow, () => []));
    }

    [Fact]
    public void AnAntiCheatService_CountsOnlyWhenItStartedWithThisGame()
    {
        string game = Path.Combine(_root, "Game");
        Directory.CreateDirectory(game);
        var launched = DateTime.UtcNow.AddMinutes(-5);

        Assert.Equal("BattlEye", AntiCheatDetector.Find(game, null, launched, () => [("BEService", launched.AddSeconds(20))]));
        // Left running by a game played an hour ago, before this one was launched.
        Assert.Null(AntiCheatDetector.Find(game, null, launched, () => [("BEService", launched.AddHours(-1))]));
    }

    [Fact]
    public void AGameWithNoAntiCheat_IsNotFlagged()
    {
        string game = Path.Combine(_root, "Game");
        Directory.CreateDirectory(Path.Combine(game, "Data"));
        Assert.Null(AntiCheatDetector.Find(game, Path.Combine(game, "game.exe"), DateTime.UtcNow, () => []));
    }

    // ------------------------------------------------------------------ the real thing

    [Fact]
    public void AProcess_IsFrozen_ThenThawed()
    {
        var ping = StartPing();
        var started = ping.StartTime.ToUniversalTime();

        Assert.Equal(ProcessSuspender.Outcome.Done, ProcessSuspender.Suspend(ping.Id, started, out _));
        Assert.True(IsFrozen(ping));

        Assert.Equal(ProcessSuspender.Outcome.Done, ProcessSuspender.Resume(ping.Id, started, out _));
        Assert.False(IsFrozen(ping));
    }

    [Fact]
    public void AProcessThatIsNotTheOneSuspended_IsNotTouched()
    {
        // The same id, a different start time: a newer process that was given the old one's id.
        var ping = StartPing();
        Assert.Equal(ProcessSuspender.Outcome.Gone, ProcessSuspender.Suspend(ping.Id, ping.StartTime.ToUniversalTime().AddMinutes(-10), out _));
        Assert.False(IsFrozen(ping));
    }

    [Fact]
    public void AProcessKilledWhileSuspended_IsGone_NotRefused()
    {
        // Force Close on a suspended game: the session's end resumes what it froze a moment after
        // the kill. Windows refuses a suspend/resume handle to a process on its way out, which is
        // "gone", not a refusal worth a warning in the log.
        for (int i = 0; i < 5; i++)
        {
            var ping = StartPing();
            var started = ping.StartTime.ToUniversalTime();
            Assert.Equal(ProcessSuspender.Outcome.Done, ProcessSuspender.Suspend(ping.Id, started, out _));

            ping.Kill();
            var atOnce = ProcessSuspender.Resume(ping.Id, started, out string? error);
            Assert.True(atOnce != ProcessSuspender.Outcome.Refused, $"Resume right after the kill was refused: {error}");

            Assert.True(ping.WaitForExit(5000));
            var after = ProcessSuspender.Resume(ping.Id, started, out error);
            Assert.True(after == ProcessSuspender.Outcome.Gone, $"Resume after the exit said {after}: {error}");
        }
    }

    private ProcessLauncherService Launcher(StorageService storage) => new(
        storage, new PerformanceProfileService(storage), new GameScriptService(),
        new SteamScannerService(), new GogScannerService(), new EaScannerService(),
        new EpicScannerService(), new UbisoftScannerService(), new XboxScannerService(), new BattleNetScannerService());

    [Fact]
    public void AGameLeftSuspended_ByATrayTriggerThatStopped_IsResumedAtTheNextStart()
    {
        var ping = StartPing();
        var started = ping.StartTime.ToUniversalTime();
        Assert.Equal(ProcessSuspender.Outcome.Done, ProcessSuspender.Suspend(ping.Id, started, out _));

        var storage = new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"));
        storage.SaveSuspendedProcesses([new SuspendedProcessRecord { GameId = "g1", GameName = "Elden Ring", ProcessId = ping.Id, StartedUtc = started }]);

        var resumed = Launcher(storage).ResumeLeftSuspended();

        Assert.Equal(["Elden Ring"], resumed);
        Assert.False(IsFrozen(ping));
        Assert.Empty(storage.LoadSuspendedProcesses());
        Assert.False(File.Exists(Path.Combine(storage.BaseDirectory, "suspended-games.json")));
    }

    [Fact]
    public void SuspendOrResume_WithNoSession_SaysSo()
    {
        var storage = new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"));
        var launcher = Launcher(storage);

        Assert.Equal(ProcessLauncherService.SuspendResult.NoSession, launcher.SuspendGame("nope").Result);
        Assert.Equal(ProcessLauncherService.SuspendResult.NoSession, launcher.ResumeGame("nope").Result);
        Assert.Equal(ProcessLauncherService.SuspendResult.NoSession, launcher.ToggleSuspendFromHotkey().Result);
        Assert.False(launcher.IsGameSuspended("nope"));
    }
}
