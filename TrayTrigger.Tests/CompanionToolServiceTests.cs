using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>
/// Tools that start with games or close for them: which ones start before a game, which are closed
/// first, which are left alone, what is reported when one doesn't start or close, which copies are
/// closed after the last game and which tools are opened again. Programs are faked except where
/// finding or ending a real copy is the point; those use a renamed ping.exe.
/// </summary>
[Collection(ActivityCurrentCollection.Name)]
public class CompanionToolServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerTests", Guid.NewGuid().ToString("N"));
    private readonly List<ToolEntry> _tools = new();
    private readonly List<string> _started = new();
    private readonly List<string> _failures = new();
    private readonly List<string?> _announced = new();
    private readonly List<string> _ended = new();
    private readonly List<(string Tool, bool Asking)> _elevatedClosing = new();
    private readonly List<Process> _toKill = new();
    private readonly List<string> _events = new();
    private readonly HashSet<string> _running = new();
    private readonly DateTime _now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
    private bool _enabled = true;
    private readonly CompanionToolService _service;
    private readonly ActivityService? _previousActivity;
    private readonly ActivityService _activity;

    private static readonly GameEntry Game = new() { Id = "companion-g1", Name = "Cyberpunk 2077" };

    /// <summary>The Tools line's notes for the game's launch, as the Played row would take them.</summary>
    private static IReadOnlyList<string> ToolNotes() =>
        LaunchRecord.Take(Game.Id).FirstOrDefault(r => r.Section == LaunchRecord.Tools).Notes ?? [];

    public CompanionToolServiceTests()
    {
        Directory.CreateDirectory(_root);
        // What the service records in Activity & History lands here; other tests' entries may too, so assertions filter by tool.
        _previousActivity = ActivityService.Current;
        _activity = new ActivityService(_root);
        ActivityService.Current = _activity;
        // The launcher begins the game's record at each launch; here each test is a launch.
        LaunchRecord.Begin(Game.Id);
        _service = new CompanionToolService(() => _tools, () => _enabled)
        {
            FileExists = _ => true,
            FindRunningCopy = _ => null,
            // Nothing closes for a game unless a test says what's running; this process stands in, never ended for real.
            FindRunningCopies = tool => _running.Contains(tool.Name) ? [Environment.ProcessId] : [],
            StartProcess = tool =>
            {
                _started.Add(tool.Name);
                return Process.GetCurrentProcess();
            },
            // Never the real ones by default: a started copy here is this test run's own process.
            FindStartedBy = _ => Array.Empty<HeldProcess>(),
            EndCopy = (targets, _) =>
            {
                _ended.Add(targets[0].Name);
                return CompanionToolService.EndResult.Ended;
            },
            EndCopyAsAdministrator = (_, _) => throw new InvalidOperationException("not expected"),
            CloseGrace = TimeSpan.FromMilliseconds(200),
            // A fixed clock, and waits recorded instead of slept.
            UtcNow = () => _now,
            Wait = span => _events.Add($"wait {span.TotalSeconds}s")
        };
        _service.StartFailed += _failures.Add;
        _service.Starting += (_, tool) =>
        {
            _announced.Add(tool?.Name);
            _events.Add($"popup {tool?.Name ?? "done"}");
        };
        _service.ClosingAsAdministrator += (tool, asking) => _elevatedClosing.Add((tool.Name, asking));
        _service.Closing += (_, tool) => _events.Add($"closing {tool?.Name ?? "done"}");
    }

    public void Dispose()
    {
        ActivityService.Current = _previousActivity;
        foreach (var process in _toKill)
        {
            try { process.Kill(); process.WaitForExit(5000); } catch { }
            process.Dispose();
        }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private readonly Dictionary<string, Process> _watchers = new();

    /// <summary>
    /// A real, windowless copy of ping.exe that runs for two minutes, given to the service as the
    /// started tool. The service disposes what it's given, so the test watches through a handle of
    /// its own (<see cref="_watchers"/>), opened at once so it still names the process after it exits.
    /// </summary>
    private Process StartPing(ToolEntry tool)
    {
        string exe = CopyPing(Path.Combine(_root, tool.Id), "TTCompanion" + Guid.NewGuid().ToString("N")[..8]);
        var process = Process.Start(new ProcessStartInfo(exe, "-n 120 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
        var watcher = Process.GetProcessById(process.Id);
        _ = watcher.Handle;
        _watchers[tool.Id] = watcher;
        _toKill.Add(watcher);
        return process;
    }

    private ToolEntry AddClosing(string name)
    {
        var tool = Add(name);
        tool.CloseAfterGames = true;
        return tool;
    }

    private ToolEntry Add(string name, string target = @"C:\Tools\tool.exe", bool start = true, string appId = "")
    {
        var tool = new ToolEntry { Id = Guid.NewGuid().ToString("N"), Name = name, TargetPath = target, AppId = appId, StartWithGames = start };
        _tools.Add(tool);
        return tool;
    }

    /// <summary>A running tool that closes for games, and opens again after them unless <paramref name="reopen"/> is false.</summary>
    private ToolEntry AddClosingForGames(string name, bool reopen = true, string target = @"C:\Tools\Discord.exe")
    {
        var tool = Add(name, target, start: false);
        tool.CloseForGames = true;
        tool.ReopenAfterGames = reopen;
        _running.Add(name);
        return tool;
    }

    [Fact]
    public void StartsEachTickedProgram_AndRemembersWhatItStarted()
    {
        var afterburner = Add("MSI Afterburner", @"C:\Tools\MSIAfterburner.exe");
        Add("Vortex", @"C:\Tools\Vortex.exe", start: false);

        _service.StartForGame(Game, remember: true);

        Assert.Equal(["MSI Afterburner"], _started);
        Assert.Equal([afterburner.Id], _service.Remembered);
        Assert.Empty(_failures);
    }

    [Fact]
    public void AlreadyRunning_IsLeftAlone_AndNotRemembered()
    {
        Add("MSI Afterburner", @"C:\Tools\MSIAfterburner.exe");
        _service.FindRunningCopy = _ => 4242;

        _service.StartForGame(Game, remember: true);

        Assert.Empty(_started);
        Assert.Empty(_service.Remembered);
        Assert.Empty(_announced);
    }

    [Fact]
    public void ToolsPageOff_StartsNothing()
    {
        Add("MSI Afterburner");
        _enabled = false;

        _service.StartForGame(Game, remember: true);

        Assert.Empty(_started);
    }

    [Fact]
    public void ScriptOrStoreApp_NeverStarts_EvenWhenTicked()
    {
        // tools.json is user-editable: Edit Tool never saves the tick on these, but a file can.
        Add("Backup", @"C:\Tools\backup.ps1");
        Add("Xbox", target: "", appId: "Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App");

        _service.StartForGame(Game, remember: true);

        Assert.Empty(_started);
        Assert.Empty(_failures);
    }

    [Fact]
    public void MissingProgram_IsReported_AndNotStarted()
    {
        Add("MSI Afterburner", @"C:\Tools\MSIAfterburner.exe");
        _service.FileExists = _ => false;

        _service.StartForGame(Game, remember: true);

        Assert.Empty(_started);
        var message = Assert.Single(_failures);
        Assert.Contains("\"MSI Afterburner\" wasn't started with the game because its file doesn't exist", message);
    }

    [Fact]
    public void DeclinedAdministratorPrompt_IsNotReported()
    {
        Add("MSI Afterburner");
        _service.StartProcess = _ => throw new Win32Exception(ToolLauncherService.ErrorCancelled);

        _service.StartForGame(Game, remember: true);

        Assert.Empty(_failures);
        Assert.Empty(_service.Remembered);
    }

    [Fact]
    public void AnyOtherFailure_IsReported_AndTheNextToolStillStarts()
    {
        Add("Broken");
        Add("SimHub", @"C:\Tools\SimHubWPF.exe");
        _service.StartProcess = tool =>
        {
            if (tool.Name == "Broken") throw new Win32Exception(2, "The system cannot find the file specified");
            _started.Add(tool.Name);
            return Process.GetCurrentProcess();
        };

        _service.StartForGame(Game, remember: true);

        Assert.Equal(["SimHub"], _started);
        Assert.Contains("\"Broken\" didn't start with the game", Assert.Single(_failures));
    }

    [Fact]
    public void AGameStartedFromALink_StartsItsTools_ButDoesNotRememberThem()
    {
        Add("MSI Afterburner");

        _service.StartForGame(Game, remember: false);

        Assert.Equal(["MSI Afterburner"], _started);
        Assert.Empty(_service.Remembered);
    }

    [Fact]
    public void Starting_NamesEachTool_ThenNull()
    {
        Add("MSI Afterburner");
        Add("SimHub");

        _service.StartForGame(Game, remember: true);

        Assert.Equal(["MSI Afterburner", "SimHub", null], _announced);
    }

    // ------------------------------------------------------------------ waiting before the game

    private ToolEntry AddWaiting(string name, int seconds)
    {
        var tool = Add(name);
        tool.WaitBeforeGame = true;
        tool.WaitBeforeGameSeconds = seconds;
        return tool;
    }

    [Fact]
    public void Wait_HoldsTheGameBack_WithThePopupNamingTheTool()
    {
        AddWaiting("MSI Afterburner", 7);

        _service.StartForGame(Game, remember: true);

        Assert.Equal(["popup MSI Afterburner", "popup MSI Afterburner", "wait 7s", "popup done"], _events);
    }

    [Fact]
    public void Wait_SeveralTools_IsTheLongest_NotTheSum()
    {
        AddWaiting("MSI Afterburner", 5);
        AddWaiting("SimHub", 8);
        Add("TrackIR");

        _service.StartForGame(Game, remember: true);

        Assert.Equal(["wait 8s"], _events.Where(e => e.StartsWith("wait", StringComparison.Ordinal)));
    }

    [Fact]
    public void Wait_NotWhenTheToolWasAlreadyRunning()
    {
        AddWaiting("MSI Afterburner", 7);
        _service.FindRunningCopy = _ => 4242;

        _service.StartForGame(Game, remember: true);

        Assert.Empty(_events);
    }

    [Fact]
    public void Wait_NotWhenThePromptWasDeclined()
    {
        AddWaiting("MSI Afterburner", 7);
        _service.StartProcess = _ => throw new Win32Exception(ToolLauncherService.ErrorCancelled);

        _service.StartForGame(Game, remember: true);

        Assert.DoesNotContain(_events, e => e.StartsWith("wait", StringComparison.Ordinal));
    }

    [Fact]
    public void Wait_ClockMovedBackWhileStarting_IsStillAtMostAMinute()
    {
        AddWaiting("MSI Afterburner", 7);
        int reads = 0;
        // Read once when the tool starts, then ten minutes earlier: a time sync just after boot.
        _service.UtcNow = () => reads++ == 0 ? _now : _now.AddMinutes(-10);

        _service.StartForGame(Game, remember: true);

        Assert.Contains("wait 60s", _events);
    }

    [Fact]
    public void Wait_FromAHandEditedFile_IsKeptWithinAMinute()
    {
        AddWaiting("MSI Afterburner", 100000);

        _service.StartForGame(Game, remember: true);

        Assert.Contains("wait 60s", _events);
    }

    [Fact]
    public void FindRunningCopyOf_MatchesByPath_NotByName()
    {
        string name = "TTCompanion" + Guid.NewGuid().ToString("N")[..8];
        string running = CopyPing(Path.Combine(_root, "a"), name);
        string sameNameElsewhere = CopyPing(Path.Combine(_root, "b"), name);

        using var process = Process.Start(new ProcessStartInfo(running, "-n 120 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            Assert.Equal(process.Id, CompanionToolService.FindRunningCopyOf(new ToolEntry { TargetPath = running }));
            Assert.Null(CompanionToolService.FindRunningCopyOf(new ToolEntry { TargetPath = sameNameElsewhere }));
        }
        finally
        {
            process.Kill();
            process.WaitForExit(5000);
        }
    }

    /// <summary>
    /// A tool that runs something through a shared program (javaw.exe -jar tracker.jar) is running only
    /// when a copy was started with its arguments - not whenever any copy of the program is.
    /// </summary>
    [Fact]
    public void FindRunningCopyOf_WithArguments_OnlyACopyStartedWithThem()
    {
        string exe = CopyPing(Path.Combine(_root, "c"), "TTCompanion" + Guid.NewGuid().ToString("N")[..8]);
        using var process = Process.Start(new ProcessStartInfo(exe, "-n 120 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            Assert.Equal(process.Id, CompanionToolService.FindRunningCopyOf(new ToolEntry { TargetPath = exe, Arguments = "-n 120 127.0.0.1" }));
            Assert.Equal(process.Id, CompanionToolService.FindRunningCopyOf(new ToolEntry { TargetPath = exe, Arguments = " -N  120 127.0.0.1 " }));
            Assert.Null(CompanionToolService.FindRunningCopyOf(new ToolEntry { TargetPath = exe, Arguments = "-n 60 127.0.0.1" }));
        }
        finally
        {
            process.Kill();
            process.WaitForExit(5000);
        }
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Java\\bin\\javaw.exe\" -jar tracker.jar", "-jar tracker.jar")]
    [InlineData("C:\\Tools\\ping.exe -n 5 host", "-n 5 host")]
    [InlineData("\"C:\\Tools\\MSIAfterburner.exe\"", "")]
    [InlineData("C:\\Tools\\MSIAfterburner.exe", "")]
    public void ArgumentsOf_IsWhatFollowsTheProgram(string commandLine, string expected) =>
        Assert.Equal(expected, ProcessPathResolver.ArgumentsOf(commandLine));

    // ------------------------------------------------------------------ closing after the last game

    [Fact]
    public void AfterTheLastGame_EndsTheCopyItStarted()
    {
        var tool = AddClosing("SimHub");
        _service.StartProcess = StartPing;
        _service.EndCopy = CompanionToolService.EndNormally;
        _service.StartForGame(Game, remember: true);

        _service.AfterLastGame(() => true);

        // No window to ask, so it is ended once the grace period is over.
        Assert.True(_watchers[tool.Id].WaitForExit(5000), "the copy started for the game should have been ended");
        Assert.Empty(_service.Remembered);
    }

    [Fact]
    public void NotSetToClose_IsLeftRunning_AndForgotten()
    {
        var tool = Add("SimHub");
        _service.StartProcess = StartPing;
        _service.EndCopy = CompanionToolService.EndNormally;
        _service.StartForGame(Game, remember: true);

        _service.AfterLastGame(() => true);

        Assert.False(_watchers[tool.Id].HasExited);
        Assert.Empty(_service.Remembered);
    }

    [Fact]
    public void WhileAGameIsRunningOrLaunching_NothingCloses()
    {
        var tool = AddClosing("SimHub");
        _service.StartForGame(Game, remember: true);

        _service.AfterLastGame(() => false);

        Assert.Empty(_ended);
        Assert.Equal([tool.Id], _service.Remembered);
    }

    [Fact]
    public void ACopyItDidNotStart_IsNeverClosed()
    {
        AddClosing("SimHub");
        _service.FindRunningCopy = _ => 4242;
        _service.StartForGame(Game, remember: true);

        _service.AfterLastGame(() => true);

        Assert.Empty(_ended);
    }

    [Fact]
    public void ARemovedTool_IsLeftRunning()
    {
        var tool = AddClosing("SimHub");
        _service.StartForGame(Game, remember: true);
        _tools.Remove(tool);

        _service.AfterLastGame(() => true);

        Assert.Empty(_ended);
        Assert.Empty(_service.Remembered);
    }

    [Fact]
    public void ACopyThatAlreadyClosed_IsForgottenQuietly()
    {
        var tool = AddClosing("SimHub");
        _service.StartProcess = StartPing;
        _service.StartForGame(Game, remember: true);
        _watchers[tool.Id].Kill();
        _watchers[tool.Id].WaitForExit(5000);

        _service.AfterLastGame(() => true);

        Assert.Empty(_ended);
        Assert.Empty(_service.Remembered);
    }

    /// <summary>
    /// Afterburner starts RTSS as it starts up; closing Afterburner alone left RTSS running. Here the
    /// tool is a Command Prompt that starts a ping, and closing the tool ends the ping too.
    /// </summary>
    [Fact]
    public void Closing_AlsoEndsWhatTheToolStartedAsItStartedUp()
    {
        var tool = AddClosing("SimHub");
        string childName = "TTChild" + Guid.NewGuid().ToString("N")[..8];
        string child = CopyPing(Path.Combine(_root, "child"), childName);
        _service.StartProcess = t =>
        {
            var cmd = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /c \"\"{child}\" -n 120 127.0.0.1\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            })!;
            var watcher = Process.GetProcessById(cmd.Id);
            _ = watcher.Handle;
            _watchers[t.Id] = watcher;
            _toKill.Add(watcher);
            return cmd;
        };
        _service.EndCopy = CompanionToolService.EndNormally;
        _service.FindStartedBy = CompanionToolService.StartedAsItStartedUp;
        _service.StartForGame(Game, remember: true);
        Assert.True(WaitUntil(() => Process.GetProcessesByName(childName).Length == 1), "the tool should have started its child");
        var started = Process.GetProcessesByName(childName)[0];
        _ = started.Handle;
        _toKill.Add(started);

        _service.AfterLastGame(() => true);

        Assert.True(_watchers[tool.Id].WaitForExit(5000), "the tool should have been ended");
        Assert.True(started.WaitForExit(5000), "what the tool started should have been ended with it");
    }

    [Theory]
    [InlineData(-1, false)]   // older than its "parent": the child of an earlier process with the same id
    [InlineData(0, true)]
    [InlineData(5, true)]     // RTSS, a few seconds after Afterburner
    [InlineData(30, true)]
    [InlineData(31, false)]   // opened from the tool later, like a browser for a link
    public void IsStartupChild_WithinTheWindowAndNeverBeforeItsParent(int secondsAfterParent, bool expected)
    {
        var parent = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, ProcessTree.IsStartupChild(parent, parent.AddSeconds(secondsAfterParent), parent + CompanionToolService.StartupWindow));
    }

    /// <summary>
    /// A browser a tool opened comes with renderer processes of its own. Turning down the browser must
    /// leave those out too, or closing the tool would kill the renderers of a browser left open. Here a
    /// Command Prompt starts PowerShell, which starts a ping: turn down PowerShell and the ping goes too.
    /// </summary>
    [Fact]
    public void StartupDescendants_AChildTurnedDown_TakesEverythingUnderItWithIt()
    {
        string pingName = "TTNested" + Guid.NewGuid().ToString("N")[..8];
        string ping = CopyPing(Path.Combine(_root, "nested"), pingName);
        string powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        // /s strips exactly the outer pair of quotes, leaving the inner ones for PowerShell.
        var outer = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            $"/d /s /c \"\"{powershell}\" -NoProfile -NonInteractive -Command \"& '{ping}' -n 120 127.0.0.1\"\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        _toKill.Add(outer);
        Assert.True(WaitUntil(() => Process.GetProcessesByName(pingName).Length == 1, 20000), "the ping should have started under PowerShell");

        using var root = HeldProcess.Open(outer.Id, "cmd")!;
        var everything = ProcessTree.StartupDescendants(root, CompanionToolService.StartupWindow, _ => true);
        // Cleaned up by what was found under this test's own Command Prompt - never by name, which
        // would reach the PowerShell of a test running alongside this one.
        foreach (var p in everything)
        {
            try { _toKill.Add(Process.GetProcessById(p.Id)); }
            catch (ArgumentException) { /* it has already exited */ }
        }
        var withoutPowerShell = ProcessTree.StartupDescendants(root, CompanionToolService.StartupWindow,
            p => !p.Name.StartsWith("powershell", StringComparison.OrdinalIgnoreCase));
        try
        {
            Assert.Contains(everything, p => p.Name.StartsWith(pingName, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(withoutPowerShell, p => p.Name.StartsWith(pingName, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(withoutPowerShell, p => p.Name.StartsWith("powershell", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            foreach (var p in everything.Concat(withoutPowerShell)) p.Dispose();
        }
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }

    [Fact]
    public void OneToolFailingToClose_DoesNotStopTheOthers()
    {
        AddClosing("SimHub");
        AddClosing("TrackIR");
        int calls = 0;
        _service.EndCopy = (_, _) =>
        {
            if (calls++ == 0) throw new Win32Exception(6, "The handle is invalid");
            _ended.Add("second");
            return CompanionToolService.EndResult.Ended;
        };
        _service.StartForGame(Game, remember: true);

        _service.AfterLastGame(() => true);

        Assert.Equal(["second"], _ended);
        Assert.Empty(_service.Remembered);
    }

    [Fact]
    public void RunningAsAdministrator_AsksOnce_AndNoIsFinal()
    {
        AddClosing("MSI Afterburner");
        _service.EndCopy = (_, _) => CompanionToolService.EndResult.NeedsAdministrator;
        int asked = 0;
        _service.EndCopyAsAdministrator = (_, _) =>
        {
            asked++;
            return false; // the prompt was declined
        };
        _service.StartForGame(Game, remember: true);

        _service.AfterLastGame(() => true);
        _service.AfterLastGame(() => true);

        Assert.Equal(1, asked);
        Assert.Equal([("MSI Afterburner", true), ("MSI Afterburner", false)], _elevatedClosing);
        Assert.Empty(_service.Remembered);
    }

    [Fact]
    public void RunningAsAdministrator_ClosedWithPermission()
    {
        AddClosing("MSI Afterburner");
        _service.EndCopy = (_, _) => CompanionToolService.EndResult.NeedsAdministrator;
        _service.EndCopyAsAdministrator = (_, _) => true;
        _service.StartForGame(Game, remember: true);

        _service.AfterLastGame(() => true);

        Assert.Equal([("MSI Afterburner", true), ("MSI Afterburner", false)], _elevatedClosing);
    }

    [Fact]
    public void ClosesForGames_BeforeAnyToolStarts_WithThePopupNamingEach()
    {
        AddClosingForGames("Discord");
        Add("MSI Afterburner", @"C:\Tools\MSIAfterburner.exe");

        _service.StartForGame(Game, remember: true);

        Assert.Equal(["Discord"], _ended);
        Assert.Equal(["MSI Afterburner"], _started);
        Assert.Equal(["closing Discord", "closing done", "popup MSI Afterburner", "popup done"], _events);
    }

    [Fact]
    public void NotRunning_NothingToClose_AndNoPopup()
    {
        var discord = AddClosingForGames("Discord");
        _running.Clear();

        _service.StartForGame(Game, remember: true);

        Assert.Empty(_ended);
        Assert.Empty(_events);
        Assert.Empty(_service.ToReopen);
        Assert.False(discord.StartWithGames);
    }

    [Fact]
    public void ToolsPageOff_ClosesNothing()
    {
        AddClosingForGames("Discord");
        _enabled = false;

        _service.StartForGame(Game, remember: true);

        Assert.Empty(_ended);
    }

    [Fact]
    public void TickedToStartAndClose_InAHandEditedFile_Starts()
    {
        var tool = AddClosingForGames("Discord");
        tool.StartWithGames = true;

        _service.StartForGame(Game, remember: true);

        Assert.Empty(_ended);
        Assert.Equal(["Discord"], _started);
    }

    [Fact]
    public void AScriptOrStoreApp_IsNeverClosed_EvenWhenTicked()
    {
        AddClosingForGames("Backup", target: @"C:\Tools\backup.ps1");
        var xbox = AddClosingForGames("Xbox", target: "");
        xbox.AppId = "Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App";

        _service.StartForGame(Game, remember: true);

        Assert.Empty(_ended);
    }

    [Fact]
    public void AfterTheLastGame_OpensItAgain_TheWayTheToolsPageWould()
    {
        var discord = AddClosingForGames("Discord");
        _service.StartForGame(Game, remember: true);
        Assert.Equal([discord.Id], _service.ToReopen);

        _service.AfterLastGame(() => true);

        Assert.Equal(["Discord"], _started);
        Assert.Empty(_service.ToReopen);
        Assert.Empty(_service.Remembered);
    }

    [Fact]
    public void WhileAGameIsRunningOrLaunching_NothingIsOpenedAgain()
    {
        var discord = AddClosingForGames("Discord");
        _service.StartForGame(Game, remember: true);

        _service.AfterLastGame(() => false);

        Assert.Empty(_started);
        Assert.Equal([discord.Id], _service.ToReopen);
    }

    [Fact]
    public void OpenedAgainByTheUserMeanwhile_IsLeftAlone()
    {
        AddClosingForGames("Discord");
        _service.StartForGame(Game, remember: true);
        _service.FindRunningCopy = _ => 4242;

        _service.AfterLastGame(() => true);

        Assert.Empty(_started);
        Assert.Empty(_service.ToReopen);
    }

    [Fact]
    public void WithoutTheReopenBox_ItStaysClosed()
    {
        AddClosingForGames("Discord", reopen: false);

        _service.StartForGame(Game, remember: true);
        _service.AfterLastGame(() => true);

        Assert.Equal(["Discord"], _ended);
        Assert.Empty(_started);
        Assert.Empty(_service.ToReopen);
    }

    [Fact]
    public void AGameStartedFromALink_ClosesTheTool_ButCannotOpenItAgain()
    {
        AddClosingForGames("Discord");

        _service.StartForGame(Game, remember: false);

        Assert.Equal(["Discord"], _ended);
        Assert.Empty(_service.ToReopen);
    }

    [Fact]
    public void ARemovedTool_OrOneUnticked_IsNotOpenedAgain()
    {
        var discord = AddClosingForGames("Discord");
        var slack = AddClosingForGames("Slack", target: @"C:\Tools\slack.exe");
        _service.StartForGame(Game, remember: true);
        _tools.Remove(discord);
        slack.ReopenAfterGames = false;

        _service.AfterLastGame(() => true);

        Assert.Empty(_started);
        Assert.Empty(_service.ToReopen);
    }

    [Fact]
    public void StillRunningAfterTheClose_IsNotOpenedAgain()
    {
        AddClosingForGames("Discord");
        _service.EndCopy = (_, _) => CompanionToolService.EndResult.StillRunning;

        _service.StartForGame(Game, remember: true);

        Assert.Empty(_service.ToReopen);
    }

    [Fact]
    public void RunningAsAdministrator_IsClosedWithPermission_UnderTheLaunchPopup()
    {
        var discord = AddClosingForGames("Discord");
        _service.EndCopy = (_, _) => CompanionToolService.EndResult.NeedsAdministrator;
        int asked = 0;
        _service.EndCopyAsAdministrator = (_, _) =>
        {
            asked++;
            return true;
        };

        _service.StartForGame(Game, remember: true);

        Assert.Equal(1, asked);
        // The launch popup already says "Closing Discord first", so the prompt's own popup isn't shown.
        Assert.Empty(_elevatedClosing);
        Assert.Equal([discord.Id], _service.ToReopen);
    }

    [Fact]
    public void OpeningAgainFailing_IsSwallowed_AndTheNextToolStillOpens()
    {
        AddClosingForGames("Broken", target: @"C:\Tools\broken.exe");
        AddClosingForGames("Discord");
        _service.StartForGame(Game, remember: true);
        _service.StartProcess = tool =>
        {
            if (tool.Name == "Broken") throw new Win32Exception(2, "The system cannot find the file specified");
            _started.Add(tool.Name);
            return Process.GetCurrentProcess();
        };

        _service.AfterLastGame(() => true);

        Assert.Equal(["Discord"], _started);
        Assert.Empty(_service.ToReopen);
        Assert.Empty(_failures);
    }

    [Fact]
    public void OneToolFailingToCloseForAGame_DoesNotStopTheRest_OrTheGame()
    {
        AddClosingForGames("Discord");
        AddClosingForGames("Slack", target: @"C:\Tools\slack.exe");
        int calls = 0;
        _service.EndCopy = (targets, _) =>
        {
            if (calls++ == 0) throw new Win32Exception(6, "The handle is invalid");
            _ended.Add(targets[0].Name);
            return CompanionToolService.EndResult.Ended;
        };

        _service.StartForGame(Game, remember: true);

        Assert.Equal(["Slack"], _ended);
    }

    [Fact]
    public void WhatWasDoneForTheLaunch_GoesOnTheGamesPlayedRow_NotRowsOfItsOwn()
    {
        AddClosingForGames("Discord");
        AddClosingForGames("Slack", reopen: false, target: @"C:\Tools\slack.exe");
        AddClosing("MSI Afterburner");
        Add("SimHub", @"C:\Tools\SimHubWPF.exe");

        _service.StartForGame(Game, remember: true);

        Assert.Equal(["Discord closed, opens again after your last game", "Slack closed", "MSI Afterburner started, closes after your last game", "SimHub started"], ToolNotes());
        // Taken once: the row has them now.
        Assert.Empty(ToolNotes());

        _service.AfterLastGame(() => true);
        // Successes aren't rows in Activity & History; the game's Played row carries them.
        Assert.DoesNotContain(_activity.Entries, e => e.Subject is "Discord" or "Slack" or "MSI Afterburner" or "SimHub");
    }

    /// <summary>The Played row, recorded by the launcher with the notes, then handed back so the outcome can be written on it.</summary>
    private ActivityEntry PlayedRow()
    {
        var notes = ToolNotes();
        var row = _activity.Record(ActivityLevel.Change, "Played Cyberpunk 2077 · 10m", subject: Game.Name,
            detail: "From 10:00 to 10:10." + "\n" + "Tools: " + string.Join(" · ", notes) + ".", groupKey: "played|companion-g1");
        _service.AttachPlayedRow(Game.Id, row);
        return row;
    }

    private string RowDetail(ActivityEntry row) => _activity.Entries.Single(e => e.Id == row.Id).Detail!;

    [Fact]
    public void TheToolsLine_SaysWhatHappened_OnceTheLastGameHasExited()
    {
        AddClosingForGames("Discord");
        AddClosing("MSI Afterburner");
        _service.StartForGame(Game, remember: true);
        var row = PlayedRow();
        Assert.Equal("Tools: Discord closed, opens again after your last game · MSI Afterburner started, closes after your last game.", RowDetail(row).Split('\n')[1]);

        _service.AfterLastGame(() => true);

        Assert.Equal("Tools: Discord closed, opened again after · MSI Afterburner started, closed after.", RowDetail(row).Split('\n')[1]);
        Assert.Equal("From 10:00 to 10:10.", RowDetail(row).Split('\n')[0]);
    }

    [Fact]
    public void TheToolsLine_SaysWhenItDidNotGoToPlan_BesideTheProblemRows()
    {
        AddClosingForGames("Discord");
        AddClosing("MSI Afterburner");
        _service.StartForGame(Game, remember: true);
        var row = PlayedRow();
        _service.EndCopy = (_, _) => CompanionToolService.EndResult.StillRunning;
        _service.StartProcess = _ => throw new Win32Exception(2, "The system cannot find the file specified");

        _service.AfterLastGame(() => true);

        Assert.Equal("Tools: Discord closed, couldn't be opened again · MSI Afterburner started, couldn't be closed after.", RowDetail(row).Split('\n')[1]);
        Assert.Contains(_activity.Entries, e => e.Text == "MSI Afterburner couldn't be closed after your game");
        Assert.Contains(_activity.Entries, e => e.Text == "Discord wasn't opened again after your game");
    }

    [Fact]
    public void TheToolsLine_WhenTheUserGotThereFirst_OrChangedTheirMind()
    {
        var discord = AddClosingForGames("Discord");
        var afterburner = AddClosing("MSI Afterburner");
        _service.StartForGame(Game, remember: true);
        var row = PlayedRow();
        _service.FindRunningCopy = _ => 4242;      // Discord opened again by the user meanwhile
        afterburner.CloseAfterGames = false;       // Afterburner unticked while the game ran

        _service.AfterLastGame(() => true);

        Assert.Equal("Tools: Discord closed, open again already · MSI Afterburner started, left running.", RowDetail(row).Split('\n')[1]);
        Assert.True(discord.CloseForGames);
    }

    [Fact]
    public void ARowThatWasCleared_IsLeftAlone()
    {
        AddClosing("MSI Afterburner");
        _service.StartForGame(Game, remember: true);
        var row = PlayedRow();
        _activity.Clear();

        _service.AfterLastGame(() => true);

        Assert.DoesNotContain(_activity.Entries, e => e.Id == row.Id);
    }

    [Fact]
    public void AGameStartedFromALink_NotesWhatCannotComeBack()
    {
        AddClosingForGames("Discord");
        AddClosing("MSI Afterburner");

        _service.StartForGame(Game, remember: false);

        Assert.Equal(["Discord closed", "MSI Afterburner started"], ToolNotes());
    }

    [Fact]
    public void NothingToDo_SaysSo_AndRecordsNoRows()
    {
        var discord = AddClosingForGames("Discord");
        _running.Clear();
        Add("MSI Afterburner", @"C:\Tools\MSIAfterburner.exe");
        Add("Broken", @"C:\Tools\broken.exe");
        _service.FindRunningCopy = t => t.Name == "MSI Afterburner" ? 4242 : null;
        _service.FileExists = path => !path.Contains("broken");

        _service.StartForGame(Game, remember: true);
        _service.AfterLastGame(() => true);

        // The row says what wasn't done and why: that's the transparency, not a problem row.
        Assert.Equal(["Discord wasn't running, nothing to close", "MSI Afterburner already running, left as it was", "Broken wasn't started: its file doesn't exist"], ToolNotes());
        Assert.DoesNotContain(_activity.Entries, e => e.Subject is "Discord" or "MSI Afterburner");
        Assert.False(discord.StartWithGames);
    }

    [Fact]
    public void AToolThatClosedWhileBeingReached_IsSaidOnTheToolsLine_NotAProblem()
    {
        AddClosingForGames("Discord");
        // Found running, gone by the time it's opened: a process id nothing has.
        _service.FindRunningCopies = _ => [int.MaxValue - 1];

        _service.StartForGame(Game, remember: true);

        Assert.Equal(["Discord had closed already, nothing to close"], ToolNotes());
        Assert.DoesNotContain(_activity.Entries, e => e.Subject == "Discord");
        Assert.Empty(_service.ToReopen);
    }

    [Fact]
    public void AFailureToClose_IsAProblemRow_AndSaidOnTheToolsLine()
    {
        AddClosingForGames("Discord");
        _service.EndCopy = (_, _) => CompanionToolService.EndResult.StillRunning;

        _service.StartForGame(Game, remember: true);

        Assert.Equal(["Discord couldn't be closed"], ToolNotes());
        var problem = Assert.Single(_activity.Entries, e => e.Subject == "Discord");
        Assert.Equal(ActivityLevel.Problem, problem.Level);
        Assert.Equal("Discord couldn't be closed for Cyberpunk 2077", problem.Text);
    }

    [Fact]
    public void FindRunningCopiesOf_ListsEveryCopy()
    {
        // Two real copies of the same renamed ping: both count, with no arguments to tell them apart.
        var tool = new ToolEntry { Id = "two", Name = "Two", TargetPath = CopyPing(Path.Combine(_root, "two"), "TTTwo" + Guid.NewGuid().ToString("N")[..8]) };
        var first = Process.Start(new ProcessStartInfo(tool.TargetPath, "-n 120 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
        var second = Process.Start(new ProcessStartInfo(tool.TargetPath, "-n 120 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
        _toKill.Add(first);
        _toKill.Add(second);

        var copies = CompanionToolService.FindRunningCopiesOf(tool);

        Assert.Equal(new[] { first.Id, second.Id }.OrderBy(i => i), copies.OrderBy(i => i));
        Assert.Contains(CompanionToolService.FindRunningCopyOf(tool)!.Value, copies);
    }

    private static string CopyPing(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name + ".exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), path);
        return path;
    }
}
