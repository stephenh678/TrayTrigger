using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>
/// Tools that start with games: which ones start before a game, which are left alone, what is
/// reported when one doesn't start, and which started copies are remembered for closing later.
/// Programs are faked except where finding a running copy by its path is the point.
/// </summary>
public class CompanionToolServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerTests", Guid.NewGuid().ToString("N"));
    private readonly List<ToolEntry> _tools = new();
    private readonly List<string> _started = new();
    private readonly List<string> _failures = new();
    private readonly List<string?> _announced = new();
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
            }
        };
        _service.StartFailed += _failures.Add;
        _service.Starting += (_, tool) => _announced.Add(tool?.Name);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
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

    private static string CopyPing(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name + ".exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), path);
        return path;
    }
}
