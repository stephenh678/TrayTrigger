using System.IO;
using TrayTrigger.Services;
using TrayTrigger.Tests.Fakes;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>
/// The elevated step runs with administrator rights, so what it will do is written down and checked at both ends:
/// only the settings and values in <see cref="NvidiaAdminOp.Allowed"/>, and only a .reg file TrayTrigger made.
/// </summary>
public class ElevatedHelperTests
{
    private const uint Rebar = PerformanceProfileService.NvResizableBarSettingId;

    [Fact]
    public void OnlyResizableBar_IsAllowed_WithOffOrOn()
    {
        Assert.Equal([Rebar], NvidiaAdminOp.Allowed.Keys.ToArray());
        Assert.True(new NvidiaAdminOp(Rebar, NvidiaAdminAction.Set, 0).IsAllowed);
        Assert.True(new NvidiaAdminOp(Rebar, NvidiaAdminAction.Set, 1).IsAllowed);
        Assert.False(new NvidiaAdminOp(Rebar, NvidiaAdminAction.Set, 2).IsAllowed);
        Assert.True(new NvidiaAdminOp(Rebar, NvidiaAdminAction.Delete).IsAllowed);
        Assert.True(new NvidiaAdminOp(Rebar, NvidiaAdminAction.RestoreDefault).IsAllowed);
        // Another setting, even a harmless-looking one, is not this step's to change.
        Assert.False(new NvidiaAdminOp(PerformanceProfileService.NvPowerModeSettingId, NvidiaAdminAction.Set, 1).IsAllowed);
    }

    [Theory]
    [InlineData("set:000F00BA:1")]
    [InlineData("set:000F00BA:0")]
    [InlineData("delete:000F00BA")]
    [InlineData("default:000F00BA")]
    public void AnArgument_RoundTrips(string argument)
    {
        Assert.True(NvidiaAdminOp.TryParse(argument, out var op));
        Assert.Equal(argument, op.ToArgument());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("set")]
    [InlineData("set:000F00BA")]
    [InlineData("set:000F00BA:2")]
    [InlineData("set:000F00BA:-1")]
    [InlineData("set:000F00BA:1:extra")]
    [InlineData("set:0F00BA:1")]
    [InlineData("set:xxxxxxxx:1")]
    [InlineData("set:00000001:1")]
    [InlineData("delete:00000001")]
    [InlineData("delete:000F00BA:1")]
    [InlineData("run:000F00BA")]
    [InlineData("set:000F00BA:1; calc")]
    public void AnythingElse_IsRefused(string? argument)
    {
        Assert.False(NvidiaAdminOp.TryParse(argument, out _));
    }

    [Fact]
    public void TheNvidiaChanges_AreMadeOnTheGlobalProfile_AndReadBack()
    {
        var driver = new FakeDrsBackend();

        Assert.True(ElevatedHelper.ApplyNvidiaOps([new NvidiaAdminOp(Rebar, NvidiaAdminAction.Set, 1)], driver, out var error), error);
        Assert.Equal((1u, false), driver.Global.Settings[Rebar]);

        Assert.True(ElevatedHelper.ApplyNvidiaOps([new NvidiaAdminOp(Rebar, NvidiaAdminAction.Delete)], driver, out error), error);
        Assert.False(driver.Global.Settings.ContainsKey(Rebar));
    }

    [Fact]
    public void ARefusedChange_IsAFailure_WithTheDriversReason()
    {
        var driver = new FakeDrsBackend();
        driver.RefusedWrites[Rebar] = "NVAPI_INVALID_USER_PRIVILEGE (-137)";

        Assert.False(ElevatedHelper.ApplyNvidiaOps([new NvidiaAdminOp(Rebar, NvidiaAdminAction.Set, 1)], driver, out var error));
        Assert.Contains("-137", error);
        Assert.False(driver.Global.Settings.ContainsKey(Rebar));
    }

    [Fact]
    public void AChangeThatIsNotAllowed_IsNotMade()
    {
        var driver = new FakeDrsBackend();

        Assert.False(ElevatedHelper.ApplyNvidiaOps([new NvidiaAdminOp(PerformanceProfileService.NvPowerModeSettingId, NvidiaAdminAction.Set, 1)], driver, out _));
        Assert.Empty(driver.Global.Settings);
    }

    [Fact]
    public void OnlyARegFileTrayTriggerMade_IsImported()
    {
        string temp = Path.GetTempPath();
        string ours = Path.Combine(temp, $"TrayTrigger-{Guid.NewGuid():N}.reg");
        string wrongName = Path.Combine(temp, "evil.reg");
        string elsewhere = Path.Combine(Directory.GetCurrentDirectory(), $"TrayTrigger-{Guid.NewGuid():N}.reg");
        try
        {
            File.WriteAllText(ours, "x");
            File.WriteAllText(wrongName, "x");
            File.WriteAllText(elsewhere, "x");

            Assert.True(ElevatedHelper.IsOurTempRegFile(ours));
            Assert.False(ElevatedHelper.IsOurTempRegFile(wrongName));
            Assert.False(ElevatedHelper.IsOurTempRegFile(elsewhere));
            Assert.False(ElevatedHelper.IsOurTempRegFile(Path.Combine(temp, $"TrayTrigger-{Guid.NewGuid():N}.reg")));
            Assert.False(ElevatedHelper.IsOurTempRegFile(Path.Combine(temp, "..", Path.GetFileName(ours))));
            Assert.False(ElevatedHelper.IsOurTempRegFile(@"C:\Windows\System32\config\SAM"));
            Assert.False(ElevatedHelper.IsOurTempRegFile("not a path \0"));
        }
        finally
        {
            foreach (var f in new[] { ours, wrongName, elsewhere }) { try { File.Delete(f); } catch { } }
        }
    }

    [Fact]
    public void TheHelper_RefusesWhatItWasNotAskedPlainly_BeforeDoingAnything()
    {
        // A change that is not allowed, whatever else is on the command line.
        Assert.Equal(ElevatedHelper.BadRequestBit, ElevatedHelper.RunAsHelper([ElevatedHelper.Switch, "--nvidia", "set:000F00BA:7"]));
        // Without administrator rights it does nothing: this process is normally not elevated.
        if (!SystemTweaksService.IsElevated)
            Assert.Equal(ElevatedHelper.BadRequestBit, ElevatedHelper.RunAsHelper([ElevatedHelper.Switch, "--nvidia", "set:000F00BA:1"]));
    }

    [Fact]
    public void ARun_WithNothingToDo_AsksNothing()
    {
        Assert.Equal(ElevatedResult.Nothing, ElevatedHelper.Run([], []));
    }
}
