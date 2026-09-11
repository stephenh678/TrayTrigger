using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>
/// The bundled scripts folder: what ships, that it installs without overwriting user edits, and
/// that every bundled script runs clean through the same path Test Run uses. The examples act on
/// real programs, so before running one a test points its "Change these" settings at something
/// harmless (a missing app, or a copy of ping.exe under a unique name). No test can touch an app
/// that is actually running on the machine.
/// </summary>
public class ScriptLibraryServiceTests : IDisposable
{
    private static readonly string[] Examples =
    {
        "Example-CompanionApps.ps1",
        "Example-OBSReplayBuffer.ps1",
        "Example-QuietMode.ps1",
        "Example-SaveBackup.ps1",
        "Example-WallpaperEnginePause.ps1",
    };

    public static TheoryData<string> ExampleNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in Examples) data.Add(name);
        return data;
    }

    private readonly string _base;
    private readonly ScriptLibraryService _lib;
    /// <summary>Unique per test, so the examples' TEMP note files can't collide with a real game's.</summary>
    private readonly string _gameId = "libtest" + Guid.NewGuid().ToString("N");
    /// <summary>Process names of the ping.exe copies a test started, killed on dispose.</summary>
    private readonly List<string> _pingCopies = new();

    public ScriptLibraryServiceTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "TrayTriggerLib_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_base);
        _lib = new ScriptLibraryService(_base);
    }

    public void Dispose()
    {
        foreach (var name in _pingCopies)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { p.Kill(); p.WaitForExit(2000); } catch { }
            }
        }
        foreach (var note in NotesForThisGame())
        {
            try { File.Delete(note); } catch { }
        }
        try { Directory.Delete(_base, recursive: true); } catch { }
    }

    // ------------------------------------------------------------------ the bundle

    [Fact]
    public void Bundle_ContainsTemplatesExamplesAndReadme()
    {
        var expected = new[] { "_Blank.bat", "_Blank.ps1", "README.txt" }
            .Concat(Examples)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(expected, ScriptLibraryService.BundledFileNames());
    }

    [Fact]
    public void EnsureInstalled_WritesEverything_ThenNeverOverwritesUserEdits_ButRefreshesReadme()
    {
        var first = _lib.EnsureInstalled();
        Assert.Equal(Examples.Length + 3, first.Count);
        Assert.Equal(Path.Combine(_base, "Scripts"), _lib.ScriptsDirectory);

        string example = Path.Combine(_lib.ScriptsDirectory, "Example-SaveBackup.ps1");
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
    [MemberData(nameof(ExampleNames))]
    public void Example_HasTheCatalogHeader_ASettingsBlock_AndOnlyAscii(string example)
    {
        string text = ScriptLibraryService.ReadBundled(example)!;

        foreach (var field in new[] { "Name:", "Description:", "Author:", "Version:", "Phase:", "Needs admin:", "Dependencies:", "Script Arguments:" })
        {
            Assert.Contains("  " + field, text);
        }
        Assert.Contains("---- Change these", text);

        // Windows PowerShell reads a file without a byte-order mark in the ANSI code page, so any
        // non-ASCII character (a curly quote, a dash) would arrive garbled.
        int bad = text.IndexOf(text.FirstOrDefault(c => c > 127));
        Assert.True(text.All(c => c <= 127), $"{example} has a non-ASCII character at index {bad}");
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

        Assert.True(r.Succeeded, Describe(r));
        Assert.Contains(expectedOutput, r.Output);
    }

    // ------------------------------------------------------------------ every example

    [Theory]
    [MemberData(nameof(ExampleNames))]
    public void Example_WithNothingToActOn_ExitsZeroInBothPhases_LeavesNoNote_AndRejectsAnUnknownPhase(string example)
    {
        string script = InstallExample(example, Neutralise(example));
        var game = Game();

        var pre = GameScriptService.TestRun(script, game, GameScriptService.PhasePreLaunch, null);
        var post = GameScriptService.TestRun(script, game, GameScriptService.PhasePostExit, 7);
        var unknown = GameScriptService.TestRun(script, game, "sometime", null);

        Assert.True(pre.Succeeded, Describe(pre));
        Assert.True(post.Succeeded, Describe(post));
        Assert.Equal(1, unknown.ExitCode);
        Assert.Empty(NotesForThisGame());
    }

    /// <summary>Settings edits that stop an example from finding a real app on this machine.</summary>
    private static (string From, string To)[] Neutralise(string example) => example switch
    {
        "Example-WallpaperEnginePause.ps1" => new[]
        {
            ("$ProcessNames = @('wallpaper32', 'wallpaper64')", "$ProcessNames = @('TrayTriggerNoSuchWallpaper')"),
        },
        "Example-OBSReplayBuffer.ps1" => new[]
        {
            (@"$ObsExe = Join-Path $env:ProgramFiles 'obs-studio\bin\64bit\obs64.exe'", @"$ObsExe = 'C:\TrayTriggerNoSuchFolder\obs64.exe'"),
        },
        _ => Array.Empty<(string, string)>(),
    };

    // ------------------------------------------------------------------ Quiet Mode

    [Fact]
    public void QuietMode_ClosesANamedProgram_ThenReopensIt_AndNeverClosesProtectedOnes()
    {
        string exe = CopyPing("TTQuiet");
        string name = Path.GetFileNameWithoutExtension(exe);
        using var running = Process.Start(new ProcessStartInfo(exe, "-n 120 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;

        // The reopened copy would flash a console window during the test run.
        string script = InstallExample("Example-QuietMode.ps1",
            ("Start-Process -FilePath $path -WorkingDirectory (Split-Path -Parent $path)",
             "Start-Process -FilePath $path -WorkingDirectory (Split-Path -Parent $path) -WindowStyle Hidden"));

        var protectedOnly = GameScriptService.TestRun(script, Game("explorer"), GameScriptService.PhasePreLaunch, null);
        Assert.True(protectedOnly.Succeeded, Describe(protectedOnly));
        Assert.Contains("never-close list", protectedOnly.Output);
        Assert.Empty(NotesForThisGame());

        var pre = GameScriptService.TestRun(script, Game(name), GameScriptService.PhasePreLaunch, null);
        Assert.True(pre.Succeeded, Describe(pre));
        Assert.True(running.WaitForExit(5000), "the named program should have been closed");
        Assert.Contains($"Ended {name}.", pre.Output);   // no window, so it is ended rather than asked
        Assert.Single(NotesForThisGame());

        var post = GameScriptService.TestRun(script, Game(name), GameScriptService.PhasePostExit, 3);
        Assert.True(post.Succeeded, Describe(post));
        Assert.Contains($"Reopened {name}.", post.Output);
        Assert.Empty(NotesForThisGame());
    }

    // ------------------------------------------------------------------ Companion Apps

    [Fact]
    public void CompanionApps_LeavesAnAlreadyRunningProgram_OtherwiseStartsItAndClosesItAfter()
    {
        string exe = CopyPing("TTCompanion");
        string name = Path.GetFileNameWithoutExtension(exe);
        string script = InstallExample("Example-CompanionApps.ps1",
            ("$Companions = @(", $"$Companions = @( @{{ Path = '{exe}'; Arguments = '-n 120 127.0.0.1' }}"),
            ("$WindowStyle = 'Minimized'", "$WindowStyle = 'Hidden'"),
            ("$GraceSeconds = 5", "$GraceSeconds = 1"));

        using (var mine = Process.Start(new ProcessStartInfo(exe, "-n 120 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!)
        {
            var alreadyOpen = GameScriptService.TestRun(script, Game(), GameScriptService.PhasePreLaunch, null);
            Assert.True(alreadyOpen.Succeeded, Describe(alreadyOpen));
            Assert.Contains("already running. Leaving it alone.", alreadyOpen.Output);
            Assert.Empty(NotesForThisGame());
            mine.Kill();
            mine.WaitForExit(5000);
        }

        var pre = GameScriptService.TestRun(script, Game(), GameScriptService.PhasePreLaunch, null);
        Assert.True(pre.Succeeded, Describe(pre));
        Assert.Contains($"Started {name}.", pre.Output);
        Assert.True(WaitUntil(() => Process.GetProcessesByName(name).Length == 1), "the companion should be running");

        var post = GameScriptService.TestRun(script, Game(), GameScriptService.PhasePostExit, 10);
        Assert.True(post.Succeeded, Describe(post));
        Assert.Contains($"Ended {name}.", post.Output);
        Assert.True(WaitUntil(() => Process.GetProcessesByName(name).Length == 0), "the companion should have been closed");
        Assert.Empty(NotesForThisGame());
    }

    // ------------------------------------------------------------------ Save Backup

    [Fact]
    public void SaveBackup_ZipsBeforeAndAfter_SkipsUnchangedSaves_PrunesOldZips_AndNeverTouchesTheSaves()
    {
        string saves = Path.Combine(_base, "My Saves");
        Directory.CreateDirectory(saves);
        string slot = Path.Combine(saves, "slot1.sav");
        File.WriteAllText(slot, "data");
        string backups = Path.Combine(_base, "Backups");
        var game = Game($"\"{saves}\" \"{backups}\"", name: "Zip: Game?");
        string gameFolder = Path.Combine(backups, "Zip_ Game_");

        string script = InstallExample("Example-SaveBackup.ps1");

        var before = GameScriptService.TestRun(script, game, GameScriptService.PhasePreLaunch, null);
        Assert.True(before.Succeeded, Describe(before));
        string beforeZip = Assert.Single(Directory.GetFiles(gameFolder, "*.zip"));
        Assert.EndsWith(" before.zip", beforeZip);
        using (var zip = ZipFile.OpenRead(beforeZip))
        {
            // The save folder itself is inside the zip, so it extracts back into place.
            Assert.Contains(zip.Entries, e => e.FullName.Replace('\\', '/') == "My Saves/slot1.sav");
        }

        var unchanged = GameScriptService.TestRun(script, game, GameScriptService.PhasePreLaunch, null);
        Assert.True(unchanged.Succeeded, Describe(unchanged));
        Assert.Contains("No changes since the last backup", unchanged.Output);
        Assert.Single(Directory.GetFiles(gameFolder, "*.zip"));

        File.SetLastWriteTime(slot, DateTime.Now.AddMinutes(1));
        var after = GameScriptService.TestRun(script, game, GameScriptService.PhasePostExit, 45);
        Assert.True(after.Succeeded, Describe(after));
        Assert.Contains(Directory.GetFiles(gameFolder, "*.zip"), z => z.EndsWith(" after 45 min.zip"));
        Assert.Equal(2, Directory.GetFiles(gameFolder, "*.zip").Length);

        InstallExample("Example-SaveBackup.ps1", ("$Keep = 10", "$Keep = 1"));
        File.SetLastWriteTime(slot, DateTime.Now.AddMinutes(2));
        var pruned = GameScriptService.TestRun(script, game, GameScriptService.PhasePostExit, 46);
        Assert.True(pruned.Succeeded, Describe(pruned));
        Assert.Contains("Removed old backup", pruned.Output);
        Assert.Single(Directory.GetFiles(gameFolder, "*.zip"));

        Assert.Equal("data", File.ReadAllText(slot));

        var missing = GameScriptService.TestRun(script, Game($"\"{Path.Combine(_base, "Nope")}\""), GameScriptService.PhasePostExit, 1);
        Assert.Equal(1, missing.ExitCode);
        Assert.Contains("Save folder not found", missing.Output);
    }

    // ------------------------------------------------------------------ helpers

    private GameEntry Game(string scriptArguments = "", string name = "Lib Test Game") => new()
    {
        Id = _gameId,
        Name = name,
        ExecutablePath = @"C:\Games\Lib\game.exe",
        ScriptArguments = scriptArguments,
    };

    /// <summary>
    /// Installs the bundle (fresh copies of anything missing), then applies text edits to one
    /// example's installed copy. Each edit must match, so a renamed setting fails the test loudly
    /// instead of silently running the example against a real app.
    /// </summary>
    private string InstallExample(string example, params (string From, string To)[] edits)
    {
        _lib.EnsureInstalled();
        string path = Path.Combine(_lib.ScriptsDirectory, example);
        string text = File.ReadAllText(path);
        foreach (var (from, to) in edits)
        {
            Assert.True(text.Contains(from), $"{example} no longer contains the text this test edits: {from}");
            text = text.Replace(from, to);
        }
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>A copy of ping.exe under a unique name: a harmless, windowless program to close and start.</summary>
    private string CopyPing(string prefix)
    {
        string name = prefix + Guid.NewGuid().ToString("N")[..8];
        string target = Path.Combine(_base, name + ".exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), target);
        _pingCopies.Add(name);
        return target;
    }

    private IEnumerable<string> NotesForThisGame() =>
        Directory.GetFiles(Path.GetTempPath(), $"TrayTrigger-*-{_gameId}.txt");

    private static bool WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(100);
        }
        return condition();
    }

    private static string Describe(ScriptTestResult r) =>
        $"exit={r.ExitCode} timedOut={r.TimedOut} err={r.Error}\n{r.Output}";
}
