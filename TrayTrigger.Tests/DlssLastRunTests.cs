using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>
/// Turning the modules seen in a running game into the card's "Last run" line.
///
/// <para>The rule that matters most is what it must NOT say. A loaded runtime is a reading, not a
/// cause, and seeing nothing is not a failed override - it is nothing to record.</para>
/// </summary>
public class DlssLastRunTests
{
    private const string Store = @"C:\ProgramData\NVIDIA\NGX\models";

    private static DlssProbeService.LoadedRuntime FromStore(string feature, uint encodedVersion) =>
        new("160_E658700.bin", $@"{Store}\{feature}\versions\{encodedVersion}\files\160_E658700.bin",
            null, FromDriverStore: true);

    private static DlssProbeService.LoadedRuntime FromGame(string dll, string version) =>
        new(dll, $@"C:\Games\Test\bin\x64\{dll}", version, FromDriverStore: false);

    [Fact]
    public void ARuntimeFromTheDriverStore_IsReportedWithTheVersionInItsPath()
    {
        // The hashed .bin carries no version resource; the versions\<n> folder is the version.
        var last = DlssProbeService.ReadLastRun(new[] { FromStore("dlss", 20318464) }, "310.1.0");

        Assert.NotNull(last);
        Assert.Equal(new[] { "310.9.0" }, last!.FromNvidia);
        Assert.Empty(last.FromGame);
        Assert.Equal("310.1.0", last.GameVersion);
    }

    [Fact]
    public void TheGamesOwnDll_IsReportedAsTheGames()
    {
        var last = DlssProbeService.ReadLastRun(new[] { FromGame("nvngx_dlss.dll", "310.1.0") }, "310.1.0");

        Assert.Equal(new[] { "310.1.0" }, last!.FromGame);
        Assert.Empty(last.FromNvidia);
    }

    [Fact]
    public void WhenBothAreLoadedForAFeature_TheStoresRuntimeWins()
    {
        // NGX can have the game's DLL open as well as the one it substituted, and module order
        // must not decide which of the two is reported.
        var last = DlssProbeService.ReadLastRun(
            new[] { FromGame("nvngx_dlss.dll", "310.1.0"), FromStore("dlss", 20318464) }, null);

        Assert.Equal(new[] { "310.9.0" }, last!.FromNvidia);
        Assert.Empty(last.FromGame);
    }

    [Fact]
    public void FeaturesAreToldApart_ByFolderInTheStore_AndByFileNameInTheGame()
    {
        // The store's .bin has the same name for all three; nvngx_dlss must not match dlssg.
        var last = DlssProbeService.ReadLastRun(
            new[] { FromStore("dlssg", 20318464), FromGame("nvngx_dlss.dll", "310.1.0") }, null);

        Assert.Equal(new[] { "310.9.0" }, last!.FromNvidia);
        Assert.Equal(new[] { "310.1.0" }, last.FromGame);
    }

    [Fact]
    public void ThreeFeaturesOnOneVersion_AreListedOnce()
    {
        var last = DlssProbeService.ReadLastRun(
            new[] { FromStore("dlss", 20318464), FromStore("dlssd", 20318464), FromStore("dlssg", 20318464) }, null);

        Assert.Single(last!.FromNvidia);
    }

    [Fact]
    public void NothingSeen_IsNothingToRecord()
    {
        // Not running, not in use yet, or refused by anti-cheat: none of them is a result.
        Assert.Null(DlssProbeService.ReadLastRun(Array.Empty<DlssProbeService.LoadedRuntime>(), "310.1.0"));
        Assert.Null(DlssProbeService.ReadLastRun(
            new[] { new DlssProbeService.LoadedRuntime("sl.interposer.dll", @"C:\Games\Test\sl.interposer.dll", "2.4.0", false) }, null));
    }

    [Fact]
    public void ANullProcess_ScansToNothing_RatherThanThrowing()
    {
        Assert.Empty(DlssProbeService.ScanLoadedModules(null, out string? note));
        Assert.NotNull(note);
    }

    [Theory]
    [InlineData(new[] { "310.2.0", "310.1.0" }, "310.1.0")]
    [InlineData(new[] { "310.9.0", "310.10.0" }, "310.9.0")]   // numeric, not alphabetical
    public void TheOldestShippedVersion_IsComparedNumerically(string[] versions, string expected)
    {
        var shipped = versions.Select(v => new DlssProbeService.ShippedRuntime("Super Resolution", "x.dll", v));

        Assert.Equal(expected, DlssProbeService.OldestVersion(shipped));
    }
}
