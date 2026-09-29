using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>
/// Tools that start with games: which ones start before a game, which are left alone, what is
/// reported when one doesn't start, and which copies are closed after the last game. Programs are
/// faked except where finding or ending a real copy is the point; those use a renamed ping.exe.
/// </summary>
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
    private bool _enabled = true;
    private readonly CompanionToolService _service;

    private static readonly GameEntry Game = new() { Id = "g1", Name = "Cyberpunk 2077" };

    public CompanionToolServiceTests()
    {
        Directory.CreateDirectory(_root);
        _service = new CompanionToolService(() => _tools, () => _enabled)
        {
            FileExists = _ => true,
            FindRunningCopy = _ => null,
            StartProcess = tool =>
            {
                _started.Add(tool.Name);
                return Process.GetCurrentProcess();
            },
            // Never the real one by default: a started copy here is this test run's own process.
            EndCopy = (process, _) =>
            {
                _ended.Add(process.ProcessName);
                return CompanionToolService.EndResult.Ended;
            },
            EndCopyAsAdministrator = (_, _) => throw new InvalidOperationException("not expected"),
            CloseGrace = TimeSpan.FromMilliseconds(200)
        };
        _service.StartFailed += _failures.Add;
        _service.Starting += (_, tool) => _announced.Add(tool?.Name);
        _service.ClosingAsAdministrator += (tool, asking) => _elevatedClosing.Add((tool.Name, asking));
    }

    public void Dispose()
    {
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

    [Fact]
    public void FindRunningProgram_MatchesByPath_NotByName()
    {
        string name = "TTCompanion" + Guid.NewGuid().ToString("N")[..8];
        string running = CopyPing(Path.Combine(_root, "a"), name);
        string sameNameElsewhere = CopyPing(Path.Combine(_root, "b"), name);

        using var process = Process.Start(new ProcessStartInfo(running, "-n 120 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            Assert.Equal(process.Id, CompanionToolService.FindRunningProgram(running));
            Assert.Null(CompanionToolService.FindRunningProgram(sameNameElsewhere));
        }
        finally
        {
            process.Kill();
            process.WaitForExit(5000);
        }
    }

    // ------------------------------------------------------------------ closing after the last game

    [Fact]
    public void AfterTheLastGame_EndsTheCopyItStarted()
    {
        var tool = AddClosing("SimHub");
        _service.StartProcess = StartPing;
        _service.EndCopy = CompanionToolService.EndNormally;
        _service.StartForGame(Game, remember: true);

        _service.CloseIfIdle(() => true);

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

        _service.CloseIfIdle(() => true);

        Assert.False(_watchers[tool.Id].HasExited);
        Assert.Empty(_service.Remembered);
    }

    [Fact]
    public void WhileAGameIsRunningOrLaunching_NothingCloses()
    {
        var tool = AddClosing("SimHub");
        _service.StartForGame(Game, remember: true);

        _service.CloseIfIdle(() => false);

        Assert.Empty(_ended);
        Assert.Equal([tool.Id], _service.Remembered);
    }

    [Fact]
    public void ACopyItDidNotStart_IsNeverClosed()
    {
        AddClosing("SimHub");
        _service.FindRunningCopy = _ => 4242;
        _service.StartForGame(Game, remember: true);

        _service.CloseIfIdle(() => true);

        Assert.Empty(_ended);
    }

    [Fact]
    public void ARemovedTool_IsLeftRunning()
    {
        var tool = AddClosing("SimHub");
        _service.StartForGame(Game, remember: true);
        _tools.Remove(tool);

        _service.CloseIfIdle(() => true);

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

        _service.CloseIfIdle(() => true);

        Assert.Empty(_ended);
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

        _service.CloseIfIdle(() => true);
        _service.CloseIfIdle(() => true);

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

        _service.CloseIfIdle(() => true);

        Assert.Equal([("MSI Afterburner", true), ("MSI Afterburner", false)], _elevatedClosing);
    }

    private static string CopyPing(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name + ".exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), path);
        return path;
    }
}
