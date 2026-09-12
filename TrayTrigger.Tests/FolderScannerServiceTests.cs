using System.IO;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

// IsDisqualified reads real file size/FileVersionInfo from disk, so these tests create small
// throwaway files under a per-test-run temp directory and clean up afterward.
public class FolderScannerServiceTests : IDisposable
{
    private readonly string _tempDir;

    public FolderScannerServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TrayTriggerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string CreateFile(string name, int sizeBytes)
    {
        string path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, new byte[sizeBytes]);
        return path;
    }

    /// <summary>
    /// UbisoftScannerService depends on this: Ubisoft publishes no display name, so the install
    /// folder is the only title there is, and the scanner asks for preferExe: false to get it.
    /// Taking the default instead returned the exe stem, which turned "Tom Clancy's Rainbow Six
    /// Extraction" into "R 6 Extraction Plus" in Steph's 1.4.1-beta.1 log.
    /// </summary>
    [Fact]
    public void ScanFolder_PreferExeFalse_NamesTheGameAfterTheFolder()
    {
        string gameDir = Path.Combine(_tempDir, "Tom Clancy's Rainbow Six Extraction");
        Directory.CreateDirectory(gameDir);
        File.WriteAllBytes(Path.Combine(gameDir, "R6-Extraction_Plus.exe"), new byte[400 * 1024]);

        var byFolder = new FolderScannerService().ScanFolder(gameDir, preferExe: false);
        Assert.Equal("Tom Clancy's Rainbow Six Extraction", Assert.Single(byFolder).Name);

        // The default still derives the name from the executable, which other callers rely on.
        var byExe = new FolderScannerService().ScanFolder(gameDir, preferExe: true);
        Assert.Equal("R6 Extraction Plus", Assert.Single(byExe).Name);
    }

    [Fact]
    public void IsDisqualified_TinyStub_IsDisqualified()
    {
        // Stubs under 60KB are almost never real games.
        string path = CreateFile("game.exe", 10 * 1024);
        Assert.True(FolderScannerService.IsDisqualified(path, "MyGame"));
    }

    [Fact]
    public void IsDisqualified_MissingFile_IsDisqualified()
    {
        string path = Path.Combine(_tempDir, "does_not_exist.exe");
        Assert.True(FolderScannerService.IsDisqualified(path, "MyGame"));
    }

    [Fact]
    public void IsDisqualified_UninstallerName_IsDisqualified()
    {
        string path = CreateFile("unins000.exe", 200 * 1024);
        Assert.True(FolderScannerService.IsDisqualified(path, "MyGame"));
    }

    [Fact]
    public void IsDisqualified_VcRedistName_IsDisqualified()
    {
        string path = CreateFile("vcredist_x64.exe", 200 * 1024);
        Assert.True(FolderScannerService.IsDisqualified(path, "MyGame"));
    }

    [Fact]
    public void IsDisqualified_PlausibleGameExecutable_IsNotDisqualified()
    {
        string path = CreateFile("MyGame.exe", 200 * 1024);
        Assert.False(FolderScannerService.IsDisqualified(path, "MyGame"));
    }

    [Fact]
    public void IsDisqualified_NameMatchesRootFolder_IsNotDisqualifiedEvenIfPatternMatches()
    {
        // "Crash Bandicoot" folder + "crash.exe" should NOT be disqualified, since the root
        // folder name itself legitimately contains the flagged word.
        string path = CreateFile("crash.exe", 200 * 1024);
        Assert.False(FolderScannerService.IsDisqualified(path, "Crash Bandicoot"));
    }

    private string CreateDir(params string[] segments)
    {
        string path = Path.Combine(new[] { _tempDir }.Concat(segments).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void ExpandLibraryContainers_DescendsThroughSteamappsAndCommon()
    {
        // D:\SteamLibrary\steamapps\common\<games> - dropping the library root must reach the
        // game folders, not collapse the whole steamapps tree into one best-scoring exe.
        string steamapps = CreateDir("steamapps");
        string gameA = CreateDir("steamapps", "common", "Game A");
        string gameB = CreateDir("steamapps", "common", "Game B");
        CreateDir("steamapps", "shadercache");

        var expanded = FolderScannerService.ExpandLibraryContainers([steamapps]);

        Assert.Contains(gameA, expanded);
        Assert.Contains(gameB, expanded);
        Assert.DoesNotContain(steamapps, expanded);
        Assert.DoesNotContain(Path.Combine(steamapps, "common"), expanded);
    }

    [Fact]
    public void ExpandLibraryContainers_LeavesOrdinaryGameFoldersAlone()
    {
        string game = CreateDir("Cyberpunk 2077");
        CreateDir("Cyberpunk 2077", "bin");

        var expanded = FolderScannerService.ExpandLibraryContainers([game]);

        Assert.Equal([game], expanded);
    }

    [Fact]
    public void ExpandLibraryContainers_KeepsAnEmptyContainer()
    {
        string empty = CreateDir("Games");

        var expanded = FolderScannerService.ExpandLibraryContainers([empty]);

        Assert.Equal([empty], expanded);
    }
}
