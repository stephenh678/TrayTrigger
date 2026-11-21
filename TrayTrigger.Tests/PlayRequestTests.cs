using System.IO;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>"TrayTrigger.exe --play Game": reading the command line, finding the game, and the pipe that carries it to the running copy.</summary>
public class PlayRequestTests
{
    [Fact]
    public void NoPlaySwitch_IsNoRequest()
    {
        Assert.Null(PlayRequest.FromArguments([]));
        Assert.Null(PlayRequest.FromArguments(["--minimized"]));
        Assert.Null(PlayRequest.FromArguments(["--no-changes"]));
    }

    [Theory]
    [InlineData("--play", "Hades", "Hades", false)]
    [InlineData("--PLAY", "Hades", "Hades", false)]
    [InlineData("--play=Hades", null, "Hades", false)]
    [InlineData("--play", "\"Cyberpunk 2077\"", "Cyberpunk 2077", false)]
    public void ReadsTheGameName(string first, string? second, string expected, bool withoutChanges)
    {
        var args = second == null ? new[] { first } : new[] { first, second };
        var request = PlayRequest.FromArguments(args);
        Assert.NotNull(request);
        Assert.Equal(expected, request!.Query);
        Assert.Equal(withoutChanges, request.WithoutChanges);
    }

    [Fact]
    public void NoChanges_CanComeBeforeOrAfterTheGame()
    {
        Assert.True(PlayRequest.FromArguments(["--play", "Hades", "--no-changes"])!.WithoutChanges);
        Assert.True(PlayRequest.FromArguments(["--no-changes", "--play", "Hades"])!.WithoutChanges);
        Assert.False(PlayRequest.FromArguments(["--play", "Hades"])!.WithoutChanges);
    }

    [Fact]
    public void PlayWithNoName_IsARequestForNothing_NotAnotherSwitchTakenAsAName()
    {
        Assert.Equal(string.Empty, PlayRequest.FromArguments(["--play"])!.Query);
        Assert.Equal(string.Empty, PlayRequest.FromArguments(["--play", "--no-changes"])!.Query);
        Assert.True(PlayRequest.FromArguments(["--play", "--no-changes"])!.WithoutChanges);
    }

    [Fact]
    public void ArgumentsForAShortcut_ReadBackAsTheSameRequest()
    {
        string id = Guid.NewGuid().ToString("N");
        var plain = PlayRequest.FromArguments(PlayRequest.ArgumentsFor(id).Split(' '))!;
        Assert.Equal(id, plain.Query);
        Assert.False(plain.WithoutChanges);
        Assert.True(PlayRequest.FromArguments(PlayRequest.ArgumentsFor(id, withoutChanges: true).Split(' '))!.WithoutChanges);
    }

    [Fact]
    public void ControlCharactersAndLength_AreCleanedOffTheQuery()
    {
        Assert.Equal("Hades", PlayRequest.FromArguments(["--play", "Ha\r\ndes"])!.Query);
        Assert.DoesNotContain('\n', new PlayRequest("a\nb", false).ToLine());
        Assert.True(PlayRequest.FromArguments(["--play", new string('x', 5000)])!.Query.Length <= PlayRequest.MaxQueryLength);
    }

    [Fact]
    public void TheLine_RoundTrips_AndRefusesAnythingElse()
    {
        Assert.True(PlayRequest.TryParseLine(new PlayRequest("Hades", true).ToLine(), out var a));
        Assert.Equal(new PlayRequest("Hades", true), a);
        Assert.True(PlayRequest.TryParseLine("0|Game | With Bars", out var b));
        Assert.Equal("Game | With Bars", b.Query);
        Assert.False(PlayRequest.TryParseLine(null, out _));
        Assert.False(PlayRequest.TryParseLine("", out _));
        Assert.False(PlayRequest.TryParseLine("2|Hades", out _));
        Assert.False(PlayRequest.TryParseLine("Hades", out _));
    }

    private static readonly (string Id, string Name)[] Library =
    [
        ("aaa", "Hades"), ("bbb", "Hades II"), ("ccc", "Cyberpunk 2077"), ("ddd", "The Witcher 3: Wild Hunt"),
        ("eee", "Doom"), ("fff", "Doom Eternal"),
    ];

    [Fact]
    public void FindsByIdFirst_ThenExactName_IgnoringCase()
    {
        Assert.Equal("ccc", PlayMatch.Find("CCC", Library).Id);
        Assert.Equal("aaa", PlayMatch.Find("hades", Library).Id);       // exact beats "Hades II"
        Assert.Equal("eee", PlayMatch.Find("DOOM", Library).Id);        // exact beats "Doom Eternal"
    }

    [Fact]
    public void FindsAUniquePrefixOrPart_ButNeverGuessesBetweenTwo()
    {
        Assert.Equal("ccc", PlayMatch.Find("cyber", Library).Id);
        Assert.Equal("ddd", PlayMatch.Find("witcher", Library).Id);

        Assert.Equal("fff", PlayMatch.Find("doom e", Library).Id);      // only "Doom Eternal" starts with it
        Assert.Equal("bbb", PlayMatch.Find("hades i", Library).Id);

        var ambiguous = PlayMatch.Find("hollow", new[] { ("1", "Hollow Mage"), ("2", "Hollow Knight") });
        Assert.Equal(PlayMatchKind.Several, ambiguous.Kind);
        Assert.Null(ambiguous.Id);
        Assert.Equal(["Hollow Knight", "Hollow Mage"], ambiguous.Names);
    }

    [Fact]
    public void NothingFound_AndAnEmptyQuery_AreNone()
    {
        Assert.Equal(PlayMatchKind.None, PlayMatch.Find("Elden Ring", Library).Kind);
        Assert.Equal(PlayMatchKind.None, PlayMatch.Find("   ", Library).Kind);
        Assert.Equal(PlayMatchKind.None, PlayMatch.Find("Hades", []).Kind);
    }

    [Fact]
    public async Task ThePipe_CarriesARequestToTheListener()
    {
        string pipe = "TrayTrigger.Play.Test." + Guid.NewGuid().ToString("N");
        using var channel = new PlayRequestChannel(pipe);
        var received = new TaskCompletionSource<PlayRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Received += r => received.TrySetResult(r);
        channel.Start();

        Assert.True(PlayRequestChannel.TrySend(new PlayRequest("Hades", true), TimeSpan.FromSeconds(5), pipe));
        var request = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new PlayRequest("Hades", true), request);

        // And again: the listener goes back to waiting after each one.
        received = new TaskCompletionSource<PlayRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(PlayRequestChannel.TrySend(new PlayRequest("Doom", false), TimeSpan.FromSeconds(5), pipe));
        Assert.Equal("Doom", (await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).Query);

        // A title with accents or another script crosses the pipe intact.
        received = new TaskCompletionSource<PlayRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(PlayRequestChannel.TrySend(new PlayRequest("Pokémon 宝可梦", false), TimeSpan.FromSeconds(5), pipe));
        Assert.Equal("Pokémon 宝可梦", (await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).Query);
    }

    [Fact]
    public void ARequestWithNoRunningTrayTrigger_FailsQuickly()
    {
        string pipe = "TrayTrigger.Play.Test." + Guid.NewGuid().ToString("N");
        Assert.False(PlayRequestChannel.TrySend(new PlayRequest("Hades", false), TimeSpan.FromMilliseconds(300), pipe));
    }

    [Fact]
    public void ARunningTrayTriggerWithOtherLines_IgnoresGarbage()
    {
        string pipe = "TrayTrigger.Play.Test." + Guid.NewGuid().ToString("N");
        using var channel = new PlayRequestChannel(pipe);
        int count = 0;
        channel.Received += _ => Interlocked.Increment(ref count);
        channel.Start();

        using (var client = new System.IO.Pipes.NamedPipeClientStream(".", pipe, System.IO.Pipes.PipeDirection.Out, System.IO.Pipes.PipeOptions.CurrentUserOnly))
        {
            client.Connect(5000);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine("not a request");
        }
        Thread.Sleep(300);
        Assert.Equal(0, Volatile.Read(ref count));
    }

    [Fact]
    public async Task ALineReader_StopsAtTheLimit_AndNeverTakesALongerLine()
    {
        using var ok = new StringReader("1|Hades\r\nnext");
        Assert.Equal("1|Hades", await PlayRequestChannel.ReadBoundedLineAsync(ok, 20, default));

        using var tooLong = new StringReader(new string('x', 5000) + "\n");
        Assert.Null(await PlayRequestChannel.ReadBoundedLineAsync(tooLong, 20, default));

        using var empty = new StringReader("");
        Assert.Null(await PlayRequestChannel.ReadBoundedLineAsync(empty, 20, default));
    }

    [Fact]
    public void ADroppedShortcutToTrayTrigger_IsNotImportedAsAGame()
    {
        // The shortcut Create Shortcut makes starts a game through TrayTrigger.exe; importing it would add TrayTrigger as a "game".
        string dir = Path.Combine(Path.GetTempPath(), "TrayTriggerPlayRequestTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string lnk = Path.Combine(dir, "Hades.lnk");
            string exe = Path.Combine(dir, "TrayTrigger.exe");
            File.WriteAllText(exe, "x");
            ShortcutService.CreateGameShortcut(lnk, exe, "abc123", "Hades", iconSource: null);

            var resolved = new ShortcutService().Resolve(lnk);
            Assert.Equal(exe, resolved.TargetPath, ignoreCase: true);
            Assert.Equal("--play abc123", resolved.Arguments);
            var request = PlayRequest.FromArguments(resolved.Arguments.Split(' '));
            Assert.Equal("abc123", request!.Query);
            Assert.NotNull(TrayTrigger.ViewModels.ImportCoordinator.ValidateDroppedTarget(resolved));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}

/// <summary>
/// Play Without Changes: the session applies and notes none of the game's settings. These tests start no process (a
/// pre-launch script is stood in for by a missing file that would cancel the launch): a process started here shares
/// the test run with every other test that does, and a pipe or handle one holds open has made another test flake.
/// </summary>
[Collection(ActivityCurrentCollection.Name)]   // a launch reaches ActivityService.Add: see ActivityCurrentCollection
public class WithoutChangesLaunchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerWithoutChangesTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private ProcessLauncherService NewLauncher()
    {
        Directory.CreateDirectory(_root);
        var storage = new StorageService(Path.Combine(_root, "data"), Path.Combine(_root, "local"));
        return new ProcessLauncherService(
            storage, new PerformanceProfileService(storage), new GameScriptService(),
            new SteamScannerService(), new GogScannerService(), new EaScannerService(),
            new EpicScannerService(), new UbisoftScannerService(), new XboxScannerService(), new BattleNetScannerService());
    }

    /// <summary>A game whose pre-launch script is missing and whose launch is set to be cancelled by a script that fails.</summary>
    private static GameEntry GameWithMissingScript() => new()
    {
        Name = "Script Game",
        ExecutablePath = @"C:\Games\Script\game.exe",
        PreLaunchScriptPath = @"C:\TrayTriggerTests\no-such-script.bat",
        AbortLaunchOnScriptFailure = true,
        WaitForPreLaunchScript = true,
    };

    [Fact]
    public void AnOrdinaryLaunch_RunsThePreLaunchScript_AndNotesTheProfile()
    {
        var launcher = NewLauncher();

        var plain = new GameEntry { Name = "Plain", ExecutablePath = @"C:\Games\Plain\game.exe" };
        var session = launcher.BeginSession(plain, LaunchRoute.DirectExe, out _);
        Assert.NotNull(session);
        Assert.False(session!.WithoutChanges);
        Assert.Contains(LaunchRecord.Take(plain.Id), s => s.Section == LaunchRecord.Profile);

        // The script runs (here: is looked for, and its absence cancels the launch).
        var scripted = GameWithMissingScript();
        Assert.Null(launcher.BeginSession(scripted, LaunchRoute.DirectExe, out string? abort));
        Assert.Contains("not found", abort);
    }

    [Fact]
    public void WithoutChanges_RunsNoScript_AppliesNoProfile_AndSaysSoOnTheRow()
    {
        var launcher = NewLauncher();
        var game = GameWithMissingScript();
        launcher.MarkWithoutChanges(game.Id);

        var session = launcher.BeginSession(game, LaunchRoute.DirectExe, out string? abort);

        // The missing script would have cancelled the launch; it was never looked at.
        Assert.NotNull(session);
        Assert.Null(abort);
        Assert.True(session!.WithoutChanges);
        var record = LaunchRecord.Take(game.Id);
        Assert.DoesNotContain(record, s => s.Section == LaunchRecord.Profile);
        Assert.Contains(record, s => s.Section == LaunchRecord.Launch && s.Notes.Contains("played without changes"));
    }

    [Fact]
    public void ALaunchThatFailsBeforeASession_LeavesNoWithoutChangesRequestBehind()
    {
        var launcher = NewLauncher();
        var game = new GameEntry { Name = "No Path" };   // no executable: refused before any session

        Assert.False(launcher.LaunchGame(game, out _, out _, withoutChanges: true));

        // The next, ordinary launch of the same game must not inherit the request.
        var session = launcher.BeginSession(game, LaunchRoute.DirectExe, out _);
        Assert.NotNull(session);
        Assert.False(session!.WithoutChanges);
    }

    [Fact]
    public void EndingAWithoutChangesSession_PutsNothingBack_RunsNoPostExitScript_AndSaysSoOnTheRow()
    {
        var launcher = NewLauncher();
        var game = new GameEntry
        {
            Name = "Quiet Game",
            ExecutablePath = @"C:\Games\Quiet\game.exe",
            PerformanceProfile = PerformanceProfileMode.Aggressive,
            PostExitScriptPath = @"C:\TrayTriggerTests\no-such-post-exit.bat",
        };
        launcher.MarkWithoutChanges(game.Id);
        var session = launcher.BeginSession(game, LaunchRoute.DirectExe, out _);
        Assert.NotNull(session);

        var log = new ActivityService(Path.Combine(_root, "activity"), readOnly: true);
        var previous = ActivityService.Current;
        ActivityService.Current = log;
        try
        {
            launcher.FinishSession(session!, gameRan: true, reason: "test");

            // A Change row means something was put back; a Problem row would be the post-exit script failing to run.
            var row = Assert.Single(log.Entries, e => e.Subject == "Quiet Game");
            Assert.Equal(ActivityLevel.Activity, row.Level);
            Assert.Contains("Profile: Off", row.Detail);   // the game's Aggressive profile is not named
            Assert.DoesNotContain("Aggressive", row.Detail);
            Assert.Contains("played without changes", row.Detail);
        }
        finally
        {
            ActivityService.Current = previous;
        }
    }

    [Fact]
    public void TheLaunchPopup_NamesNoProfile_ForALaunchWithoutChanges()
    {
        var game = new GameEntry { Name = "Profiled", PerformanceProfile = PerformanceProfileMode.Aggressive };

        var ordinary = TrayTrigger.ViewModels.LibraryViewModel.PopupTargetFor(game, withoutChanges: false);
        Assert.Equal(PerformanceProfileMode.Aggressive, ordinary.PerformanceProfile);
        Assert.Contains("Aggressive profile", TrayTrigger.ViewModels.LaunchPopupCoordinator.LaunchingDetail(ordinary, platform: null));

        var without = TrayTrigger.ViewModels.LibraryViewModel.PopupTargetFor(game, withoutChanges: true);
        Assert.Equal(game.Id, without.Id);
        Assert.Equal(PerformanceProfileMode.Off, without.PerformanceProfile);
        Assert.Null(TrayTrigger.ViewModels.LaunchPopupCoordinator.LaunchingDetail(without, platform: null));
    }
}
