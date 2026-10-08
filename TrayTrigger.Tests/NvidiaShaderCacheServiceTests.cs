using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.Tests.Fakes;

namespace TrayTrigger.Tests;

/// <summary>
/// The NVIDIA shader cache size on the Global profile, under the DLSS override's undo rule: put
/// back what was captured, and only while the driver still reports Unlimited.
/// </summary>
public class NvidiaShaderCacheServiceTests
{
    private const uint Id = NvidiaShaderCacheService.SettingId;
    private const uint Unlimited = NvidiaShaderCacheService.Unlimited;

    private static (NvidiaShaderCacheService Service, FakeDrsBackend Driver) NewService()
    {
        var driver = new FakeDrsBackend();
        return (new NvidiaShaderCacheService(driver), driver);
    }

    [Fact]
    public void NoNvidiaDriver_IsUnavailable()
    {
        var (service, driver) = NewService();
        driver.UnknownSettingIds.Add(Id);

        var state = service.Read();

        Assert.False(state.IsAvailable);
        Assert.Null(state.Error);
        Assert.Equal(0, driver.SessionsOpened);
    }

    [Fact]
    public void GlobalProfileRefused_IsUnavailable_WithTheReason()
    {
        var (service, driver) = NewService();
        driver.GlobalError = "NVAPI_ERROR (-1)";

        var state = service.Read();

        Assert.False(state.IsAvailable);
        Assert.Equal("NVAPI_ERROR (-1)", state.Error);
    }

    [Fact]
    public void UnlimitedAlready_ReadsAsOptimal()
    {
        var (service, driver) = NewService();
        driver.Global.Settings[Id] = (Unlimited, false);

        Assert.True(service.Read().IsUnlimited);
    }

    [Fact]
    public void NothingSet_ApplyThenRestore_LeavesNothingBehind()
    {
        var (service, driver) = NewService();
        Assert.False(service.Read().IsUnlimited);

        Assert.True(service.Apply(out string? prior, out _));
        Assert.Equal(NvidiaShaderCacheService.AbsentToken, prior);
        Assert.Equal((Unlimited, false), driver.Global.Settings[Id]);

        Assert.True(service.Restore(prior, out _));
        Assert.False(driver.Global.Settings.ContainsKey(Id));
    }

    /// <summary>A size the user picked is theirs: undo writes it back rather than deleting it.</summary>
    [Fact]
    public void UsersOwnSize_IsPutBack()
    {
        var (service, driver) = NewService();
        driver.Global.Settings[Id] = (10240, false);

        Assert.True(service.Apply(out string? prior, out _));
        Assert.Equal("user:10240", prior);

        Assert.True(service.Restore(prior, out _));
        Assert.Equal((10240u, false), driver.Global.Settings[Id]);
    }

    [Fact]
    public void NvidiasOwnValue_IsRestored_NotDeleted()
    {
        var (service, driver) = NewService();
        driver.Global.Settings[Id] = (4096, true);

        Assert.True(service.Apply(out string? prior, out _));
        Assert.Equal(NvidiaShaderCacheService.PredefinedToken, prior);

        Assert.True(service.Restore(prior, out _));
        Assert.Equal((4096u, true), driver.Global.Settings[Id]);
    }

    /// <summary>Changed since in NVIDIA App or the Control Panel: theirs now, and left alone.</summary>
    [Fact]
    public void SizeChangedSince_IsLeftAlone()
    {
        var (service, driver) = NewService();
        Assert.True(service.Apply(out string? prior, out _));
        driver.Global.Settings[Id] = (20480, false);

        Assert.True(service.Restore(prior, out _));
        Assert.Equal((20480u, false), driver.Global.Settings[Id]);
    }

    [Fact]
    public void RefusedSave_ReportsFailure_AndCapturesNothing()
    {
        var (service, driver) = NewService();
        driver.SaveError = "NVAPI_INVALID_USER_PRIVILEGE (-137)";

        Assert.False(service.Apply(out string? prior, out string? error));
        Assert.Null(prior);
        Assert.Contains("-137", error);
        Assert.False(driver.Global.Settings.ContainsKey(Id));
    }

    /// <summary>A read that failed must not be recorded as "absent", or undo would delete a real size.</summary>
    [Fact]
    public void UnreadableSetting_IsNotWritten()
    {
        var (service, driver) = NewService();
        driver.UnreadableSettingIds.Add(Id);

        Assert.False(service.Apply(out string? prior, out _));
        Assert.Null(prior);
        Assert.Equal(0, driver.SaveCount);
    }

    [Fact]
    public void WriteThatDidNotLand_IsReportedAsFailed()
    {
        var (service, driver) = NewService();
        driver.AfterSave = () => driver.Global.Settings.Remove(Id);

        Assert.False(service.Apply(out _, out string? error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(null, DlssSettingOrigin.Absent, 0u)]
    [InlineData("absent", DlssSettingOrigin.Absent, 0u)]
    [InlineData("predefined", DlssSettingOrigin.Predefined, 0u)]
    [InlineData("user:10240", DlssSettingOrigin.UserSet, 10240u)]
    [InlineData("user:-5", DlssSettingOrigin.Absent, 0u)]
    [InlineData("garbage", DlssSettingOrigin.Absent, 0u)]
    public void Tokens_RoundTrip(string? token, DlssSettingOrigin origin, uint value) =>
        Assert.Equal((origin, value), NvidiaShaderCacheService.ParseToken(token));

    // ---- Through the System tweak row --------------------------------------------------------------

    [Fact]
    public void TweakRow_RecordsWhatWasThere_AndConsumesItOnRestore()
    {
        var driver = new FakeDrsBackend();
        driver.Global.Settings[Id] = (10240, false);
        var settings = new AppSettings();
        var tweaks = new SystemTweaksService(() => settings, () => { }) { NvShaderCache = new NvidiaShaderCacheService(driver) };

        Assert.True(tweaks.ApplyTweak("nv_shader_cache", true));
        Assert.True(tweaks.GetTweakState("nv_shader_cache"));
        Assert.Equal("user:10240", settings.TweakPriorState["nv_shader_cache"]);

        Assert.True(tweaks.ApplyTweak("nv_shader_cache", false));
        Assert.False(tweaks.GetTweakState("nv_shader_cache"));
        Assert.Equal((10240u, false), driver.Global.Settings[Id]);
        Assert.False(settings.TweakPriorState.ContainsKey("nv_shader_cache"));
    }

    /// <summary>
    /// The driver saved the write but the check afterwards didn't see it: the size may be Unlimited
    /// all the same, so what it was is still recorded for Restore Previous.
    /// </summary>
    [Fact]
    public void TweakRow_RecordsWhatWasThere_EvenWhenTheCheckFails()
    {
        var driver = new FakeDrsBackend();
        driver.Global.Settings[Id] = (10240, false);
        driver.AfterSave = () => driver.Global.Settings[Id] = (10240, false);
        var settings = new AppSettings();
        var tweaks = new SystemTweaksService(() => settings, () => { }) { NvShaderCache = new NvidiaShaderCacheService(driver) };

        Assert.False(tweaks.ApplyTweak("nv_shader_cache", true));
        Assert.Equal("user:10240", settings.TweakPriorState["nv_shader_cache"]);
    }

    /// <summary>A restore that failed keeps the record, so the next try still knows what to put back.</summary>
    [Fact]
    public void TweakRow_FailedRestore_KeepsTheRecord()
    {
        var driver = new FakeDrsBackend();
        driver.Global.Settings[Id] = (10240, false);
        var settings = new AppSettings();
        var tweaks = new SystemTweaksService(() => settings, () => { }) { NvShaderCache = new NvidiaShaderCacheService(driver) };
        Assert.True(tweaks.ApplyTweak("nv_shader_cache", true));

        driver.SaveError = "refused";
        Assert.False(tweaks.ApplyTweak("nv_shader_cache", false));
        Assert.Equal("user:10240", settings.TweakPriorState["nv_shader_cache"]);
    }
}
