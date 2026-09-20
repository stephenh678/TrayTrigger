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
        DlssProbeService.LibraryFolderProvider = null;
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

    [Fact]
    public void AGameLaunchedByLink_IsFoundThroughItsInstallFolder()
    {
        // A Steam import's executable path is steam://rungameid/..., so there is no folder to
        // derive from it - the install folder the importer recorded is what there is to look in.
        File_(@"bin\nvngx_dlss.dll", 512);
        string game = File_(@"bin\Portal.exe", 60_000);
        File_(@"bin\CrashReporter.exe", 90_000);

        Assert.Equal(game, DlssProbeService.ResolveRenderingExecutable("steam://rungameid/620", _root), ignoreCase: true);
    }

    /// <summary>The real shape of an Unreal install: DLSS is a plugin, never beside the executable.</summary>
    private string UnrealGame()
    {
        File_(@"Engine\Plugins\Runtime\Nvidia\DLSS\Binaries\ThirdParty\Win64\nvngx_dlss.dll", 512);
        File_(@"Engine\Binaries\Win64\CrashReportClient.exe", 90_000);
        return File_(@"MortalShell2\Binaries\Win64\MortalShell2-Win64-Shipping.exe", 60_000);
    }

    [Fact]
    public void AnUnrealGameAddedByItsShippingExe_FindsTheDlssPluginAtTheRoot()
    {
        string game = UnrealGame();

        Assert.Equal(_root, DlssProbeService.DlssSearchRoot(game), ignoreCase: true);
        Assert.Single(DlssProbeService.FindShippedRuntimes(DlssProbeService.DlssSearchRoot(game)!));
        Assert.Equal(game, DlssProbeService.ResolveRenderingExecutable(game), ignoreCase: true);
    }

    [Fact]
    public void AnUnrealGameAddedByItsRootStub_ResolvesToTheShippingExe()
    {
        string game = UnrealGame();
        string stub = File_("MortalShell2.exe", 1024);

        Assert.Equal(game, DlssProbeService.ResolveRenderingExecutable(stub), ignoreCase: true);
    }

    [Fact]
    public void AnUnrealGameLaunchedByLink_ResolvesToTheShippingExe()
    {
        string game = UnrealGame();

        Assert.Equal(game, DlssProbeService.ResolveRenderingExecutable("steam://rungameid/620", _root), ignoreCase: true);
    }

    // ---- Any engine: the game's folder, not the executable's ---------------------------------

    [Theory]
    [InlineData(@"bin\x64\Game.exe", @"plugins\nvidia\nvngx_dlss.dll")]              // REDengine
    [InlineData(@"Bin64\Game.exe", @"plugins\nvidia\nvngx_dlss.dll")]                // CryEngine
    [InlineData(@"game\bin\win64\Game.exe", @"game\plugins\nvidia\nvngx_dlss.dll")]  // Source 2: stops at "game"
    [InlineData(@"Game.exe", @"plugins\nvidia\nvngx_dlss.dll")]
    public void DlssInAFolderOfItsOwn_IsFoundFromABinariesFolder(string exe, string dll)
    {
        string game = File_(exe, 60_000);
        File_(dll, 512);

        var where = DlssProbeService.Locate(game);

        Assert.Single(DlssProbeService.FindShippedRuntimes(where.Root!));
        // No executable beside the DLLs, and the one launched does not call itself a launcher.
        Assert.Equal(game, where.Renderer);
    }

    [Fact]
    public void ALauncherWithDlssInAFolderOfItsOwn_ResolvesToTheLargestExecutableInTheGame()
    {
        string launcher = File_("GameLauncher.exe", 90_000);
        File_(@"plugins\nvngx_dlss.dll", 512);
        File_(@"bin\x64\CrashReporter.exe", 99_000);
        string game = File_(@"bin\x64\TheGame.exe", 60_000);

        Assert.Equal(game, DlssProbeService.ResolveRenderingExecutable(launcher), ignoreCase: true);
        Assert.Equal(game, DlssProbeService.ResolveRenderingExecutable("steam://rungameid/620", _root), ignoreCase: true);
    }

    [Fact]
    public void ABinariesFolderIsWalkedOutOf_ButNoFurther()
    {
        string game = File_(@"Thing\Binaries\Win64\Game.exe", 1024);

        Assert.Equal(Path.Combine(_root, "Thing"), DlssProbeService.DlssSearchRoot(game), ignoreCase: true);
    }

    [Fact]
    public void AGameUnderAKnownLibrary_IsItsSubfolder_WhateverItsLayout()
    {
        DlssProbeService.LibraryFolderProvider = () => [_root];
        string game = File_(@"Foo\some\odd\place\Game.exe", 60_000);
        File_(@"Foo\nv\nvngx_dlss.dll", 512);
        File_(@"Bar\nvngx_dlss.dll", 512);
        File_(@"Bar\Bar.exe", 90_000);

        var where = DlssProbeService.Locate(game);

        Assert.Equal(Path.Combine(_root, "Foo"), where.Root, ignoreCase: true);
        Assert.Equal(game, where.Renderer);
        Assert.Single(DlssProbeService.FindShippedRuntimes(where.Root!));
    }

    [Fact]
    public void ALibraryIsNeverSearchedAsOneGame()
    {
        DlssProbeService.LibraryFolderProvider = () => [_root];
        File_(@"Bar\nvngx_dlss.dll", 512);
        File_(@"Bar\Bar.exe", 90_000);
        // Sitting loose in the library, and in a binaries-named folder directly under it.
        string loose = File_("Loose.exe", 1024);
        string inBin = File_(@"bin\Game.exe", 1024);

        Assert.Empty(DlssProbeService.FindShippedRuntimes(_root));
        Assert.Equal(loose, DlssProbeService.ResolveRenderingExecutable(loose));
        Assert.Equal(inBin, DlssProbeService.ResolveRenderingExecutable(inBin));
    }

    [Fact]
    public void ASteamGame_IsItsFolderUnderCommon()
    {
        string game = File_(@"steamapps\common\Foo\x\y\Game.exe", 1024);
        File_(@"steamapps\common\Other\nvngx_dlss.dll", 512);

        Assert.Equal(Path.Combine(_root, @"steamapps\common\Foo"), DlssProbeService.DlssSearchRoot(game), ignoreCase: true);
        Assert.Empty(DlssProbeService.FindShippedRuntimes(Path.Combine(_root, @"steamapps\common")));
    }

    [Fact]
    public void TheRecordedInstallFolder_IsTheGamesFolder_UnlessItIsTheExecutablesOwn()
    {
        string game = File_(@"Foo\Client\Game.exe", 1024);
        string exeDir = Path.GetDirectoryName(game)!;

        Assert.Equal(Path.Combine(_root, "Foo"), DlssProbeService.DlssSearchRoot(game, Path.Combine(_root, "Foo")), ignoreCase: true);
        // What a game added by hand records: no evidence of anything.
        Assert.Equal(exeDir, DlssProbeService.DlssSearchRoot(game, exeDir), ignoreCase: true);
    }

    [Fact]
    public void ALinkWithNothingToFind_ComesBackUnchanged_AndIsNeverWrittenTo()
    {
        const string link = "steam://rungameid/620";

        Assert.Equal(link, DlssProbeService.ResolveRenderingExecutable(link, _root));
        Assert.Equal(link, DlssProbeService.ResolveRenderingExecutable(link));

        var driver = new Fakes.FakeDrsBackend();
        var result = new DlssOverrideService(driver).Apply(link, "Portal 2");

        Assert.False(result.Succeeded);
        Assert.Empty(driver.CreatedProfiles);
    }
}
