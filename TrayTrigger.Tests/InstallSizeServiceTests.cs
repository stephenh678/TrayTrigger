using System.IO;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>Game Details' "Size on disk": counted from the game's own folder, on this PC.</summary>
public class InstallSizeServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerTests", Guid.NewGuid().ToString("N"));

    public InstallSizeServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void File_(string relative, int bytes)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    [Fact]
    public void Measure_AddsEveryFileUnderTheFolder()
    {
        File_("game.exe", 1000);
        File_(@"data\a.pak", 2000);
        File_(@"data\deep\b.pak", 3000);
        Assert.Equal(6000, InstallSizeService.Measure(_root));
    }

    [Fact]
    public void Measure_GivesNothing_ForAFolderWithMoreFilesThanOneGameHas()
    {
        for (int i = 0; i < 5; i++) File_($"f{i}.bin", 10);
        Assert.Null(InstallSizeService.Measure(_root, maxFiles: 4));
        Assert.Equal(50, InstallSizeService.Measure(_root, maxFiles: 5));
    }

    [Fact]
    public void Measure_GivesNothing_ForAFolderThatIsNotThere() =>
        Assert.Null(InstallSizeService.Measure(Path.Combine(_root, "missing")));

    [Fact]
    public void Measure_StopsWhenAskedTo()
    {
        File_("a.bin", 10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => InstallSizeService.Measure(_root, cts.Token));
    }

    [Fact]
    public void MeasureCached_CountsOnce_ForAnHour()
    {
        File_("a.bin", 100);
        Assert.Equal(100, InstallSizeService.MeasureCached(_root));
        File_("b.bin", 100);
        Assert.Equal(100, InstallSizeService.MeasureCached(_root));
    }

    [Theory]
    [InlineData(500, "1 KB")]
    [InlineData(40 * 1024L, "40 KB")]
    [InlineData(812L * 1024 * 1024, "812 MB")]
    [InlineData(1024L * 1024 * 1024, "1 GB")]
    [InlineData(79_700_000_000L, "74.2 GB")]
    public void Format_UsesTheUnitThatReadsBest(long bytes, string expected) =>
        Assert.Equal(expected, InstallSizeService.Format(bytes).Replace(',', '.'));

    [Fact]
    public void GameRoot_IsNull_ForAGameWithNoLocalFolder_AndForADrivesRoot()
    {
        Assert.Null(InstallSizeService.GameRoot(new GameEntry { Name = "X", ExecutablePath = "steam://rungameid/1" }));
        Assert.Null(InstallSizeService.GameRoot(new GameEntry { Name = "X", ExecutablePath = string.Empty, WorkingDirectory = Path.GetPathRoot(_root)! }));
    }

    [Fact]
    public void GameRoot_IsTheFolderOfTheExecutable_ForAGameInItsOwnFolder()
    {
        File_(@"MyGame\game.exe", 10);
        string? root = InstallSizeService.GameRoot(new GameEntry { Name = "X", ExecutablePath = Path.Combine(_root, "MyGame", "game.exe") });
        Assert.NotNull(root);
        Assert.StartsWith(_root, root!, StringComparison.OrdinalIgnoreCase);
    }
}
