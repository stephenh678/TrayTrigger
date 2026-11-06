using System.Text.RegularExpressions;
using Xunit;
using static TrayTrigger.Tests.SourceTree;

namespace TrayTrigger.Tests;

/// <summary>
/// Pages and view models open a link, a folder or a Windows page through Services/ShellOpen, which disposes the
/// process and logs a failure the same way everywhere. Starting a game, a tool or a script stays in the services.
/// </summary>
public class ShellOpenGuardTests
{
    [Fact]
    public void ViewModelsAndViews_OpenThingsThroughShellOpen()
    {
        var files = Sources("*.cs").Where(f => f.StartsWith("ViewModels/") || f.StartsWith("Views/"));
        var offenders = Offenders(files, new Regex(@"UseShellExecute\s*=\s*true"));

        Assert.True(offenders.Count == 0,
            "Use ShellOpen.Open, Reveal or OpenFolder (Services/ShellOpen.cs). Found:\n" + string.Join('\n', offenders));
    }
}
