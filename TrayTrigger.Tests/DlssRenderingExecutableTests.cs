using System.IO;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>
/// Picking the executable the driver keys its DLSS profile on.
///
/// <para>This is the difference between the feature working and doing nothing at all: Cyberpunk
/// 2077's library entry points at REDprelauncher.exe, whose driver profile is "RED Launcher
/// Service", while the renderer is bin\x64\Cyberpunk2077.exe under "Cyberpunk 2077". Writing to the
/// launcher's profile fails silently - no error, no effect.</para>
/// </summary>
public class DlssRenderingExecutableTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tt-dlss-" + Guid.NewGuid().ToString("N"));

    public DlssRenderingExecutableTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string File_(string relativePath, int sizeBytes)
    {
        string full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, new byte[sizeBytes]);
        return full;
    }

    [Fact]
    public void TheRendererIsTheExecutableBesideTheDlssDlls_NotTheLauncher()
    {
        // The real shape of a GOG Cyberpunk install.
        string launcher = File_("REDprelauncher.exe", 2048);
        File_(@"bin\x64\nvngx_dlss.dll", 512);
        string game = File_(@"bin\x64\Cyberpunk2077.exe", 60_000);
        File_(@"bin\x64\REDEngineErrorReporter.exe", 1024);

        Assert.Equal(game, DlssProbeService.ResolveRenderingExecutable(launcher));
    }

    [Fact]
    public void NamesThatAreNeverRenderers_AreDiscardedEvenWhenLarger()
    {
        string launcher = File_("start.exe", 512);
        File_(@"bin\nvngx_dlss.dll", 512);
        File_(@"bin\CrashReporter.exe", 90_000);
        string game = File_(@"bin\TheGame.exe", 40_000);

        Assert.Equal(game, DlssProbeService.ResolveRenderingExecutable(launcher));
    }

    [Fact]
    public void AGameWhoseOwnExeSitsBesideTheDlls_IsAlreadyRight()
    {
        // Most direct-launch games. The largest-file rule must not move the answer off the
        // executable the user actually pointed at.
        string game = File_("Game.exe", 1024);
        File_("nvngx_dlss.dll", 512);
        File_("BigPatcher.exe", 99_000);

        Assert.Equal(game, DlssProbeService.ResolveRenderingExecutable(game));
    }

    [Fact]
    public void AGameThatShipsNoDlss_KeepsTheGivenPath()
    {
        string game = File_("Game.exe", 1024);

        Assert.Equal(game, DlssProbeService.ResolveRenderingExecutable(game));
    }

    [Fact]
    public void AFolderOfOnlyDiscardedNames_FallsBackRatherThanPickingRubbish()
    {
        string launcher = File_("launch.exe", 512);
        File_(@"bin\nvngx_dlss.dll", 512);
        File_(@"bin\unins000.exe", 90_000);

        Assert.Equal(launcher, DlssProbeService.ResolveRenderingExecutable(launcher));
    }

    [Fact]
    public void AMissingPath_IsReturnedUnchanged_NotThrown()
    {
        string missing = Path.Combine(_root, "nope", "gone.exe");

        Assert.Equal(missing, DlssProbeService.ResolveRenderingExecutable(missing));
    }
}
