using System.Diagnostics;
using TrayTrigger.Services;
using Xunit;

namespace TrayTrigger.Tests;

public class BoundedProcessTests
{
    [Fact]
    public void ReturnsOutputAndExitCode_WhenToolFinishes()
    {
        var psi = new ProcessStartInfo("cmd.exe", "/c echo hello & echo oops 1>&2 & exit 3") { UseShellExecute = false, CreateNoWindow = true };
        var result = BoundedProcess.Run(psi, 5000);
        Assert.NotNull(result);
        Assert.True(result!.Value.Completed);
        Assert.Equal(3, result.Value.ExitCode);
        Assert.Contains("hello", result.Value.Output);
        Assert.Contains("oops", result.Value.Error);
    }

    [Fact]
    public void KillsTheTool_WhenItRunsPastTheLimit()
    {
        var psi = new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true };
        var started = Stopwatch.StartNew();
        var result = BoundedProcess.Run(psi, 500);
        Assert.NotNull(result);
        Assert.False(result!.Value.Completed);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5));
    }
}
