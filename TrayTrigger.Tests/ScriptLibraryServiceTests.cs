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
        "Example-StartCompanionApps.ps1",
        "Example-OBSReplayBuffer.ps1",
        "Example-CloseBackgroundApps.ps1",
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

    /// <summary>
    /// The bundled files are TrayTrigger's copy, not the user's: one still exactly as TrayTrigger
    /// left it is replaced silently, so a correction to a template or an example reaches a folder
    /// that already exists instead of only ever reaching fresh installs. An edited one is kept - see
    /// <see cref="EnsureInstalled_AnEditedFile_IsKeptAsPrevious"/>.
    /// </summary>
    [Fact]
    public void EnsureInstalled_WritesEverything_ThenReplacesWhatItWroteItself()
    {
        var first = _lib.EnsureInstalled();
        Assert.Equal(Examples.Length + 3, first.Count);
        Assert.Equal(Path.Combine(_base, "Scripts"), _lib.ScriptsDirectory);

        string example = Path.Combine(_lib.ScriptsDirectory, "Example-SaveBackup.ps1");
        string blank = Path.Combine(_lib.ScriptsDirectory, "_Blank.ps1");
        string readme = Path.Combine(_lib.ScriptsDirectory, "README.txt");

        // An older TrayTrigger's copy: untouched since it was written, so it is ours to replace.
        // Simulated by writing it and recording it the way EnsureInstalled itself would.
        StaleButUnedited(example, "# an older version");
        StaleButUnedited(blank, "# an older version");
        StaleButUnedited(readme, "an older version");

        var second = _lib.EnsureInstalled();

        Assert.Equal(new[] { "Example-SaveBackup.ps1", "README.txt", "_Blank.ps1" }, second.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(ScriptLibraryService.ReadBundled("Example-SaveBackup.ps1"), File.ReadAllText(example));
        Assert.Equal(ScriptLibraryService.ReadBundled("_Blank.ps1"), File.ReadAllText(blank));
        Assert.Equal(ScriptLibraryService.ReadBundled("README.txt"), File.ReadAllText(readme));

        // Nothing set aside: none of these was the user's work.
        Assert.Empty(Directory.GetFiles(_lib.ScriptsDirectory, "*" + ScriptLibraryService.SetAsideSuffix + "*"));

        // Nothing to do on a third pass: only a file that differs is rewritten, so the folder's
        // timestamps don't churn on every start.
        Assert.Empty(_lib.EnsureInstalled());
    }

    /// <summary>
    /// Writes <paramref name="content"/> to a bundled file and records it in the manifest as though
    /// TrayTrigger had written it, which is what an older version's copy looks like: out of date,
    /// but untouched since.
    /// </summary>
    private void StaleButUnedited(string path, string content)
    {
        File.WriteAllText(path, content);
        string manifest = Path.Combine(_base, "scripts-bundled.txt");
        var lines = File.Exists(manifest)
            ? File.ReadAllLines(manifest).Where(l => !l.StartsWith(Path.GetFileName(path) + "\t", StringComparison.OrdinalIgnoreCase)).ToList()
            : new List<string>();
        lines.Add(Path.GetFileName(path) + "\t" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))));
        File.WriteAllLines(manifest, lines);
    }

    /// <summary>
    /// The user edited a bundled file. It is their work, so it is renamed to ".previous" rather than
    /// overwritten - the notice at the top of each file warns them, and the ones who lose work are
    /// exactly the ones who did not read it. The fresh copy still lands.
    /// </summary>
    [Fact]
    public void EnsureInstalled_AnEditedFile_IsKeptAsPrevious()
    {
        _lib.EnsureInstalled();
        string example = Path.Combine(_lib.ScriptsDirectory, "Example-CloseBackgroundApps.ps1");
        File.WriteAllText(example, "# my careful work");

        var written = _lib.EnsureInstalled();

        Assert.Contains("Example-CloseBackgroundApps.ps1", written);
        Assert.Equal(ScriptLibraryService.ReadBundled("Example-CloseBackgroundApps.ps1"), File.ReadAllText(example));
        Assert.Equal("# my careful work", File.ReadAllText(example + ScriptLibraryService.SetAsideSuffix));
    }

    /// <summary>A second edit does not overwrite the first rescue: each gets its own numbered name.</summary>
    [Fact]
    public void EnsureInstalled_ASecondEdit_DoesNotLoseTheFirstOneKept()
    {
        _lib.EnsureInstalled();
        string example = Path.Combine(_lib.ScriptsDirectory, "Example-CloseBackgroundApps.ps1");

        File.WriteAllText(example, "# first");
        _lib.EnsureInstalled();
        File.WriteAllText(example, "# second");
        _lib.EnsureInstalled();

        Assert.Equal("# first", File.ReadAllText(example + ScriptLibraryService.SetAsideSuffix));
        Assert.Equal("# second", File.ReadAllText(example + ScriptLibraryService.SetAsideSuffix + ".2"));
        Assert.Equal(ScriptLibraryService.ReadBundled("Example-CloseBackgroundApps.ps1"), File.ReadAllText(example));
    }

    /// <summary>
    /// A folder from before the manifest existed cannot be told apart from an edited one, and the
    /// older README promised edits here were safe - so it is kept rather than assumed to be ours.
    /// </summary>
    [Fact]
    public void EnsureInstalled_AFolderWithNoManifest_KeepsWhatIsThere()
    {
        Directory.CreateDirectory(_lib.ScriptsDirectory);
        string example = Path.Combine(_lib.ScriptsDirectory, "Example-CloseBackgroundApps.ps1");
        File.WriteAllText(example, "# edited long ago, under the old rules");

        _lib.EnsureInstalled();

        Assert.Equal("# edited long ago, under the old rules", File.ReadAllText(example + ScriptLibraryService.SetAsideSuffix));
        Assert.Equal(ScriptLibraryService.ReadBundled("Example-CloseBackgroundApps.ps1"), File.ReadAllText(example));
    }

    /// <summary>A file set aside is not a bundled name, so it is never touched again.</summary>
    [Fact]
    public void EnsureInstalled_LeavesAPreviousCopyAlone()
    {
        _lib.EnsureInstalled();
        string example = Path.Combine(_lib.ScriptsDirectory, "Example-CloseBackgroundApps.ps1");
        File.WriteAllText(example, "# mine");
        _lib.EnsureInstalled();

        string kept = example + ScriptLibraryService.SetAsideSuffix;
        Assert.Empty(_lib.EnsureInstalled());
        Assert.Equal("# mine", File.ReadAllText(kept));
    }

    /// <summary>
    /// A script the user made is not a bundled name, so it is never among the files replaced.
    /// "New script..." names its copy after the game, which is what keeps the two apart.
    /// </summary>
    [Fact]
    public void EnsureInstalled_LeavesTheUsersOwnScriptsAlone()
    {
        _lib.EnsureInstalled();
        string mine = Path.Combine(_lib.ScriptsDirectory, "Elden Ring-PreLaunch.ps1");
        File.WriteAllText(mine, "# mine");

        Assert.Empty(_lib.EnsureInstalled());
        Assert.Equal("# mine", File.ReadAllText(mine));
    }

    /// <summary>
    /// Every bundled file says it is replaced, because that is the only warning a user gets before
    /// an edit of theirs disappears.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllBundledNames))]
    public void EveryBundledFile_SaysItIsReplaced(string name)
    {
        string? content = ScriptLibraryService.ReadBundled(name);
        Assert.NotNull(content);
        Assert.Contains("DO NOT EDIT THIS FILE", content);
        // And says where the edit goes instead, since the notice is useless without it.
        Assert.Contains("New script...", content);
    }

    public static IEnumerable<object[]> AllBundledNames() =>
        ScriptLibraryService.BundledFileNames()
            .Where(n => !n.Equals("README.txt", StringComparison.OrdinalIgnoreCase))
            .Select(n => new object[] { n });

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
        string script = InstallExample("Example-CloseBackgroundApps.ps1",
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
        string script = InstallExample("Example-StartCompanionApps.ps1",
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
