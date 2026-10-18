using TrayTrigger.Services;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>The Activity lines for a launch that failed, or whose game TrayTrigger never saw running.</summary>
public class LaunchProblemsTests
{
    [Fact]
    public void AFailedLaunch_NamesTheGameAndGivesTheReason()
    {
        var (text, detail) = LaunchProblems.DescribeFailed("Elden Ring", " Access is denied. ", executableMissing: false);

        Assert.Equal("Elden Ring didn't launch", text);
        Assert.Equal("Access is denied.", detail);
    }

    [Theory]
    [InlineData(null, true, "Its executable wasn't found.")]
    [InlineData("", false, "No reason was given.")]
    public void AFailedLaunchWithNoMessage_StillSaysSomething(string? error, bool missing, string expected)
    {
        Assert.Equal(expected, LaunchProblems.DescribeFailed("X", error, missing).Detail);
    }

    [Theory]
    [InlineData("Steam never reported the game running", "Steam never reported it running, so no playtime was recorded for this launch.")]
    [InlineData("game process never appeared", "Its process never appeared, so no playtime was recorded for this launch.")]
    [InlineData("no process handle returned", "Windows gave TrayTrigger no process to follow, so no playtime was recorded for this launch.")]
    public void AGameNeverSeenRunning_IsAProblem(string endReason, string detail)
    {
        var line = LaunchProblems.DescribeUnseen("Hades", endReason);

        Assert.NotNull(line);
        Assert.Equal("Hades was started, but TrayTrigger never saw it running", line!.Value.Text);
        Assert.Equal(detail, line.Value.Detail);
    }

    [Theory]
    [InlineData("closed by user before the game started")]
    [InlineData("launch did not complete")]
    [InlineData("ended by user")]
    [InlineData("process exited")]
    public void AnEndTheUserChose_OrOneAlreadyReported_IsNotAProblem(string endReason)
    {
        Assert.Null(LaunchProblems.DescribeUnseen("Hades", endReason));
    }

    [Fact]
    public void TheTwoProblemsGroupSeparatelyPerGame()
    {
        Assert.NotEqual(LaunchProblems.FailedGroupKey("a"), LaunchProblems.UnseenGroupKey("a"));
        Assert.NotEqual(LaunchProblems.FailedGroupKey("a"), LaunchProblems.FailedGroupKey("b"));
    }
}
