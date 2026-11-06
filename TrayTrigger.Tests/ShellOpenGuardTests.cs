using System.Text.RegularExpressions;
using Xunit;
using static TrayTrigger.Tests.SourceTree;

namespace TrayTrigger.Tests;

/// <summary>
/// A link, a folder, a file or a Windows page is opened through Services/ShellOpen, which disposes the process and
/// logs a failure the same way everywhere. Starting a game, a tool or a script stays in the services that need the
/// process they start.
/// </summary>
public class ShellOpenGuardTests
{
    [Fact]
    public void ViewModelsAndViews_StartNoProcessesOfTheirOwn()
    {
        var files = Sources("*.cs").Where(f => f.StartsWith("ViewModels/") || f.StartsWith("Views/"));
        // The one exception: System's "Restart now", which runs shutdown.exe.
        var offenders = Offenders(files, new Regex(@"Process\.Start\(|UseShellExecute\s*=\s*true"))
            .Where(line => !line.Contains("shutdown.exe"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Use ShellOpen.Open, Reveal or OpenFolder (Services/ShellOpen.cs). Found:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void OnlyShellOpen_DrivesExplorer()
    {
        // PackagedAppActivator starts a Store app through Explorer's shell:AppsFolder: a launch, not an open.
        var files = Sources("*.cs").Where(f => f != "Services/ShellOpen.cs" && f != "Services/PackagedAppActivator.cs"
                                            && f != "Services/SystemExecutables.cs");
        var offenders = Offenders(files, new Regex(@"SystemExecutables\.Explorer|""explorer(\.exe)?"""));

        Assert.True(offenders.Count == 0,
            "Open Explorer with ShellOpen.Reveal or ShellOpen.OpenFolder. Found:\n" + string.Join('\n', offenders));
    }

    [Theory]
    [InlineData(@"C:\Users\Jo Smith, Jr\AppData\Roaming\TrayTrigger", "\"C:\\Users\\Jo Smith, Jr\\AppData\\Roaming\\TrayTrigger\"")]
    [InlineData("shell:AppsFolder", "shell:AppsFolder")]
    public void OpenFolder_QuotesAPath_ButPassesAShellLocationAsItIs(string folder, string expected) =>
        Assert.Equal(expected, TrayTrigger.Services.ShellOpen.ExplorerTarget(folder));
}
