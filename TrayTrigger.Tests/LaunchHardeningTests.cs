using System.IO;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>Checks made before a program or link is started from stored data: the update installer, an Xbox package, a Steam id.</summary>
public class LaunchHardeningTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "TrayTriggerTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void AnInstallerThisDownloadNeverVerified_IsNotRun()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "TrayTrigger-v9.9.9-Setup.exe");
        File.WriteAllText(path, "not an installer");

        var ex = Assert.Throws<UpdateVerificationException>(() => UpdateService.LaunchInstallerAndExit(path));

        Assert.Contains("not verified", ex.Message);
    }

    [Fact]
    public void AnInstallerThatChangedSinceItWasVerified_IsNotRun()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "TrayTrigger-v9.9.9-Setup.exe");
        File.WriteAllText(path, "not an installer");

        Assert.Throws<UpdateVerificationException>(() => UpdateService.LaunchInstallerAndExit(path, new string('a', 64)));
    }

    [Theory]
    [InlineData("Microsoft.254428597CFE2_8wekyb3d8bbwe!Game", true)]
    [InlineData("Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App", true)]
    [InlineData("pkg_8wekyb3d8bbwe!App\" /e,C:\\Windows", false)]
    [InlineData("pkg_8wekyb3d8bbwe!App /select,C:\\x", false)]
    [InlineData("..\\..\\Windows", false)]
    [InlineData("", false)]
    public void ExplorersCommandLine_TakesOnlyAWellFormedPackageId(string aumid, bool allowed)
    {
        Assert.Equal(allowed, PackagedAppActivator.CanUseShellFallback(aumid));
    }
}
