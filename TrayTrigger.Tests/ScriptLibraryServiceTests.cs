using System.IO;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>
/// The bundled scripts folder: what ships, that it installs without overwriting user edits,
/// and that every bundled script actually runs clean through the same path Test Run uses.
/// </summary>
public class ScriptLibraryServiceTests : IDisposable
{
    private readonly string _base;
    private readonly ScriptLibraryService _lib;

    public ScriptLibraryServiceTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "TrayTriggerLib_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_base);
        _lib = new ScriptLibraryService(_base);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { }
    }

    [Fact]
    public void Bundle_ContainsTemplatesExamplesAndReadme()
    {
        var names = ScriptLibraryService.BundledFileNames();
        Assert.Contains("_Blank.bat", names);
        Assert.Contains("_Blank.ps1", names);
        Assert.Contains("Example-LogSessions.ps1", names);
        Assert.Contains("Example-ZipFolderAfterExit.ps1", names);
        Assert.Contains("Example-CloseAndRestartProgram.bat", names);
        Assert.Contains("README.txt", names);
        Assert.Equal(6, names.Count);
    }

    [Fact]
    public void EnsureInstalled_WritesEverything_ThenNeverOverwritesUserEdits_ButRefreshesReadme()
    {
        var first = _lib.EnsureInstalled();
        Assert.Equal(6, first.Count);
        Assert.Equal(Path.Combine(_base, "Scripts"), _lib.ScriptsDirectory);

        string example = Path.Combine(_lib.ScriptsDirectory, "Example-LogSessions.ps1");
        string readme = Path.Combine(_lib.ScriptsDirectory, "README.txt");
        File.WriteAllText(example, "# my edit");
        File.WriteAllText(readme, "stale");

        var second = _lib.EnsureInstalled();

        Assert.Equal(new[] { "README.txt" }, second);
        Assert.Equal("# my edit", File.ReadAllText(example));
        Assert.Equal(ScriptLibraryService.ReadBundled("README.txt"), File.ReadAllText(readme));

        // Nothing to do on a third pass.
        Assert.Empty(_lib.EnsureInstalled());
    }

    [Theory]
    [InlineData("new.bat", "_Blank.bat")]
    [InlineData("new.cmd", "_Blank.bat")]
    [InlineData("new.ps1", "_Blank.ps1")]
    public void CreateFromBlankTemplate_WritesMatchingTemplate(string fileName, string template)
    {
        string target = Path.Combine(_base, "sub", fileName);

        Assert.True(ScriptLibraryService.CreateFromBlankTemplate(target));
        Assert.Equal(ScriptLibraryService.ReadBundled(template), File.ReadAllText(target));

        // Existing files are left alone.
        File.WriteAllText(target, "edited");
        Assert.False(ScriptLibraryService.CreateFromBlankTemplate(target));
        Assert.Equal("edited", File.ReadAllText(target));
    }

    [Fact]
    public void CreateFromBlankTemplate_RefusesUnknownExtension()
    {
        string target = Path.Combine(_base, "tool.exe");
        Assert.False(ScriptLibraryService.CreateFromBlankTemplate(target));
        Assert.False(File.Exists(target));
    }

    private static readonly GameEntry Probe = new() { Id = "libtest", Name = "Lib Test Game", ExecutablePath = @"C:\Games\Lib\game.exe" };

    [Theory]
    [InlineData("_Blank.bat", "prelaunch", null, "Pre-launch for \"Lib Test Game\"")]
    [InlineData("_Blank.bat", "postexit", 12L, "after 12 minute(s)")]
    [InlineData("_Blank.ps1", "prelaunch", null, "Pre-launch for 'Lib Test Game'")]
    [InlineData("_Blank.ps1", "postexit", 12L, "after 12 minute(s)")]
    public void BlankTemplates_RunClean_InBothPhases(string template, string phase, long? playtime, string expectedOutput)
    {
        _lib.EnsureInstalled();
        var r = GameScriptService.TestRun(Path.Combine(_lib.ScriptsDirectory, template), Probe, phase, playtime);

        Assert.True(r.Succeeded, $"exit={r.ExitCode} timedOut={r.TimedOut} err={r.Error}\n{r.Output}");
        Assert.Contains(expectedOutput, r.Output);
    }

    [Fact]
    public void ExampleLogSessions_AppendsCsvRows_ToTheFileGivenAsScriptArgument()
    {
        _lib.EnsureInstalled();
        string csv = Path.Combine(_base, "out dir", "sessions.csv");
        var game = new GameEntry { Id = "g1", Name = "Log \"Quoted\", Game", ExecutablePath = @"C:\g\g.exe", ScriptArguments = $"\"{csv}\"" };
        string script = Path.Combine(_lib.ScriptsDirectory, "Example-LogSessions.ps1");

        var pre = GameScriptService.TestRun(script, game, GameScriptService.PhasePreLaunch, null);
        var post = GameScriptService.TestRun(script, game, GameScriptService.PhasePostExit, 33);

        Assert.True(pre.Succeeded, pre.Output + pre.Error);
        Assert.True(post.Succeeded, post.Output + post.Error);
        string[] lines = File.ReadAllLines(csv);
        Assert.Equal(3, lines.Length);
        Assert.Equal("Timestamp,Phase,Game,Minutes,GameId,Exe", lines[0]);
        Assert.Contains(",prelaunch,\"Log \"\"Quoted\"\", Game\",0,g1,\"C:\\g\\g.exe\"", lines[1]);
        Assert.Contains(",postexit,\"Log \"\"Quoted\"\", Game\",33,g1,", lines[2]);
    }

    [Fact]
    public void ExampleZipFolder_DoesNothingWithoutArgument_AndZipsWithOne()
    {
        _lib.EnsureInstalled();
        string script = Path.Combine(_lib.ScriptsDirectory, "Example-ZipFolderAfterExit.ps1");

        var noArgs = GameScriptService.TestRun(script, Probe, GameScriptService.PhasePostExit, 5);
        Assert.True(noArgs.Succeeded, noArgs.Output);
        Assert.Contains("No folder given", noArgs.Output);

        string saves = Path.Combine(_base, "My Saves");
        Directory.CreateDirectory(saves);
        File.WriteAllText(Path.Combine(saves, "slot1.sav"), "data");
        string backups = Path.Combine(_base, "Backups");
        var game = new GameEntry { Id = "z", Name = "Zip: Game?", ExecutablePath = @"C:\g\g.exe", ScriptArguments = $"\"{saves}\" \"{backups}\"" };

        var pre = GameScriptService.TestRun(script, game, GameScriptService.PhasePreLaunch, null);
        Assert.True(pre.Succeeded, pre.Output);
        Assert.Contains("Nothing to do on prelaunch", pre.Output);
        Assert.False(Directory.Exists(backups));

        var post = GameScriptService.TestRun(script, game, GameScriptService.PhasePostExit, 45);
        Assert.True(post.Succeeded, post.Output + post.Error);
        string gameDir = Path.Combine(backups, "Zip_ Game_");
        var zips = Directory.GetFiles(gameDir, "*.zip");
        Assert.Single(zips);
        Assert.Contains("(45 min)", Path.GetFileName(zips[0]));
        Assert.True(File.Exists(Path.Combine(saves, "slot1.sav")), "source must be untouched");
    }

    [Fact]
    public void ExampleCloseAndRestart_IsHarmless_WithoutArgument_OrWithAProcessThatIsNotRunning()
    {
        _lib.EnsureInstalled();
        string script = Path.Combine(_lib.ScriptsDirectory, "Example-CloseAndRestartProgram.bat");

        var noArgs = GameScriptService.TestRun(script, Probe, GameScriptService.PhasePreLaunch, null);
        Assert.True(noArgs.Succeeded, noArgs.Output);
        Assert.Contains("No process name given", noArgs.Output);

        var game = new GameEntry { Id = "cr", Name = "CR", ExecutablePath = @"C:\g\g.exe", ScriptArguments = "TrayTriggerNoSuchProcess.exe" };
        var pre = GameScriptService.TestRun(script, game, GameScriptService.PhasePreLaunch, null);
        Assert.True(pre.Succeeded, pre.Output);
        Assert.Contains("is not running", pre.Output);

        var post = GameScriptService.TestRun(script, game, GameScriptService.PhasePostExit, 0);
        Assert.True(post.Succeeded, post.Output);
        Assert.Contains("Nothing to restart", post.Output);
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "TrayTrigger-restart-cr.txt")));
    }
}
