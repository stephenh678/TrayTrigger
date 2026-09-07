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
}
