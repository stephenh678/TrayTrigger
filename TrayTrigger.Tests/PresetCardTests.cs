using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// The Performance Preset card's lines and the order of the tweak rows: what is applied, what
/// isn't and what applying it will ask for, what this PC can't use, and not-applied rows first.
/// </summary>
public class PresetCardTests
{
    [Theory]
    [InlineData(12, 14, "12 of 14 preset tweaks applied")]
    [InlineData(14, 14, "All 14 preset tweaks applied")]
    [InlineData(0, 0, "No preset tweak applies to this PC")]
    public void Score_ReadsAsAFraction_OrAllDone(int applied, int total, string expected)
    {
        Assert.Equal(expected, SystemViewModel.PresetScoreText(applied, total));
    }

    [Fact]
    public void NotApplied_NamesEachTweak_AndWhatApplyWillAskFor()
    {
        string two = SystemViewModel.PresetNotAppliedText(
        [
            ("Hardware-Accelerated GPU Scheduling (HAGS)", true, true),
            ("Disable Game Bar Captures and Background Recording", false, false),
        ]);
        Assert.Equal("Not applied: Hardware-Accelerated GPU Scheduling (HAGS) and Disable Game Bar Captures and Background Recording. Apply turns on both; Hardware-Accelerated GPU Scheduling (HAGS) asks for administrator permission and needs a restart.", two);

        string one = SystemViewModel.PresetNotAppliedText([("Windows Game Mode", false, false)]);
        Assert.Equal("Not applied: Windows Game Mode. Apply turns it on.", one);

        string three = SystemViewModel.PresetNotAppliedText([("A", true, false), ("B", false, true), ("C", false, false)]);
        Assert.Equal("Not applied: A, B and C. Apply turns on all 3; A asks for administrator permission; B needs a restart.", three);

        Assert.StartsWith("Every preset tweak is on.", SystemViewModel.PresetNotAppliedText([]));
    }

    [Fact]
    public void NotCounted_NamesTheTweak_AndWhy()
    {
        Assert.Equal("", SystemViewModel.PresetNotCountedText([]));
        Assert.Equal("Not counted: Variable Refresh Rate for Windowed Games: No VRR display found.",
            SystemViewModel.PresetNotCountedText([("Variable Refresh Rate for Windowed Games", "No VRR display found.")]));
        Assert.Equal("Not counted: Auto HDR: Auto HDR exists only on Windows 11.",
            SystemViewModel.PresetNotCountedText([("Auto HDR", "Auto HDR exists only on Windows 11.")]));
        Assert.Equal("Not counted: Auto HDR.", SystemViewModel.PresetNotCountedText([("Auto HDR", "")]));
    }

    [Theory]
    [InlineData(0, 0, 5, 0, "all 5 preset tweaks applied")]
    [InlineData(2, 0, 5, 0, "2 of 5 preset tweaks not applied")]
    [InlineData(1, 1, 7, 3, "1 of 7 preset tweaks not applied · 1 n/a · 3 opt-in")]
    [InlineData(0, 1, 5, 2, "all 5 preset tweaks applied · 1 n/a · 2 opt-in")]
    [InlineData(0, 0, 1, 0, "all 1 preset tweak applied")]
    [InlineData(0, 0, 0, 2, "2 opt-in")]
    [InlineData(0, 0, 0, 0, "")]
    public void GroupSummary_CountsThePresetTweaks_ThenTheRest(int notApplied, int notAvailable, int recommended, int optIn, string expected)
    {
        Assert.Equal(expected, SystemViewModel.GroupSummaryText(notApplied, notAvailable, recommended, optIn));
    }

    [Fact]
    public void Rows_SortNotAppliedFirst_ThenApplied_ThenOptIn_ThenUnavailable()
    {
        int notApplied = SystemViewModel.SortKey(recommended: true, optimal: false, optIn: false, available: true, informational: false);
        int applied = SystemViewModel.SortKey(true, true, false, true, false);
        int optIn = SystemViewModel.SortKey(false, false, true, true, false);
        int unavailable = SystemViewModel.SortKey(false, false, false, false, false);
        int informational = SystemViewModel.SortKey(false, true, false, true, true);

        Assert.True(notApplied < applied);
        Assert.True(applied < optIn);
        Assert.True(optIn < unavailable);
        Assert.Equal(optIn, informational);
    }
}
