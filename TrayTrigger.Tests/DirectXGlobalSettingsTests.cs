using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>The "key=value;" parser behind the windowed-optimizations, VRR, and Auto HDR tweaks.</summary>
public class DirectXGlobalSettingsTests
{
    [Fact]
    public void SetToken_ReplacesExistingValue_InsteadOfAppendingDuplicate()
    {
        // The exact case the old string-append code got wrong: Windows had written =0.
        string raw = "SwapEffectUpgradeEnable=0;VRROptimizeEnable=1;";
        string updated = DirectXGlobalSettings.SetToken(raw, DirectXGlobalSettings.SwapEffectUpgrade, "1");

        Assert.Equal("VRROptimizeEnable=1;SwapEffectUpgradeEnable=1;", updated);
        Assert.Equal(1, updated.Split("SwapEffectUpgradeEnable").Length - 1);
    }

    [Fact]
    public void SetToken_Null_RemovesToken_AndLeavesOthers()
    {
        string raw = "SwapEffectUpgradeEnable=1;AutoHDREnable=1;";
        Assert.Equal("AutoHDREnable=1;", DirectXGlobalSettings.SetToken(raw, DirectXGlobalSettings.SwapEffectUpgrade, null));
        Assert.Equal("", DirectXGlobalSettings.SetToken("SwapEffectUpgradeEnable=1;", DirectXGlobalSettings.SwapEffectUpgrade, null));
    }

    [Fact]
    public void SetToken_OnEmptyOrNull_CreatesSingleToken()
    {
        Assert.Equal("VRROptimizeEnable=1;", DirectXGlobalSettings.SetToken(null, DirectXGlobalSettings.Vrr, "1"));
        Assert.Equal("VRROptimizeEnable=1;", DirectXGlobalSettings.SetToken("", DirectXGlobalSettings.Vrr, "1"));
    }

    [Fact]
    public void Parse_CollapsesDuplicates_LastWins_AndIsCaseInsensitive()
    {
        var tokens = DirectXGlobalSettings.Parse("swapeffectupgradeenable=0;SwapEffectUpgradeEnable=1;;  ;");
        Assert.Single(tokens);
        Assert.Equal("1", tokens[0].Value);
        Assert.Equal("1", DirectXGlobalSettings.GetToken("swapeffectupgradeenable=0;SwapEffectUpgradeEnable=1;", "SWAPEFFECTUPGRADEENABLE"));
    }

    [Fact]
    public void GetToken_MissingReturnsNull()
    {
        Assert.Null(DirectXGlobalSettings.GetToken("AutoHDREnable=1;", DirectXGlobalSettings.Vrr));
        Assert.Null(DirectXGlobalSettings.GetToken(null, DirectXGlobalSettings.Vrr));
    }
}
