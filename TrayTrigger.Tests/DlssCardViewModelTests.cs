using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// The DLSS card's display rules. These matter more than usual: the card's whole justification is
/// that it reports what was read rather than what we assume, so the cases that must never say
/// "Game default" are the point.
/// </summary>
public class DlssCardViewModelTests
{
    private static DlssProbeService.ProbeResult Result(
        IReadOnlyList<DlssProbeService.ShippedRuntime>? shipped = null,
        NvApi.DrsProfileInfo? profile = null,
        IReadOnlyList<DlssProbeService.SettingState>? settings = null,
        IReadOnlyList<NgxModelStore.StoredRuntime>? driver = null) => new()
        {
            ExecutablePath = @"C:\Games\Test\game.exe",
            ExecutableName = "game.exe",
            Profile = profile,
            SettingStates = settings ?? Array.Empty<DlssProbeService.SettingState>(),
            ShippedRuntimes = shipped ?? Array.Empty<DlssProbeService.ShippedRuntime>(),
            DriverRuntimes = driver ?? Array.Empty<NgxModelStore.StoredRuntime>()
        };

    private static DlssProbeService.ShippedRuntime Ship(string feature, string version) =>
        new(feature, "nvngx_dlss.dll", @"bin\nvngx_dlss.dll", version, 1024);

    private static NvApi.DrsProfileInfo Profile() => new("Test Game", true, 1, 5, "game.exe", "Test Game");

    private static DlssProbeService.SettingState Toggle(string feature, uint value, NvApi.SettingOrigin origin)
    {
        var def = DlssProbeService.Settings.First(s => s.Feature == feature && s.Name.Contains("Enable DLL Override"));
        return new DlssProbeService.SettingState(def, new NvApi.DrsSettingValue(def.Id, def.Name, value, origin, false, false, 0), null);
    }

    private static DlssProbeService.SettingState Absent(string feature)
    {
        var def = DlssProbeService.Settings.First(s => s.Feature == feature && s.Name.Contains("Enable DLL Override"));
        return new DlssProbeService.SettingState(def, null, "NVAPI_SETTING_NOT_FOUND");
    }

    [Fact]
    public void NoShippedDlss_HidesTheCardEntirely()
    {
        var p = DlssCardViewModel.Project(Result());

        Assert.False(p.HasDlss);
        Assert.Empty(p.Rows);
    }

    [Fact]
    public void ShippedDlss_WithNoOverride_ReadsAsGameDefault()
    {
        var p = DlssCardViewModel.Project(Result(
            shipped: new[] { Ship("Super Resolution", "310.2.1") },
            profile: Profile(),
            settings: new[] { Absent("Super Resolution") }));

        Assert.True(p.HasDlss);
        var row = Assert.Single(p.Rows);
        Assert.Equal("Super Resolution", row.Feature);
        Assert.Equal("310.2.1", row.Version);
        Assert.Equal("Game default", row.State);
        Assert.Null(p.ExternalOverrideNotice);
    }

    [Fact]
    public void OverrideSetToZero_IsGameDefault_NotAnOverride()
    {
        // An explicit "off" is not an override, and reporting it as one would tell the user
        // something is happening to their game when nothing is.
        var p = DlssCardViewModel.Project(Result(
            shipped: new[] { Ship("Super Resolution", "310.2.1") },
            profile: Profile(),
            settings: new[] { Toggle("Super Resolution", 0, NvApi.SettingOrigin.ApplicationProfile) }));

        Assert.Equal("Game default", p.Rows[0].State);
        Assert.Null(p.ExternalOverrideNotice);
    }

    [Fact]
    public void NoDriverProfile_DoesNotClaimGameDefault()
    {
        // NVIDIA has no entry for this executable, so nothing was read about its settings.
        // Saying "Game default" here would assert something the probe never saw.
        var p = DlssCardViewModel.Project(Result(
            shipped: new[] { Ship("Super Resolution", "2.3.7") },
            profile: null));

        Assert.Equal("No driver profile", p.Rows[0].State);
        Assert.Null(p.ExternalOverrideNotice);
    }

    [Theory]
    [InlineData(NvApi.SettingOrigin.ApplicationProfile, "Overridden for this game")]
    [InlineData(NvApi.SettingOrigin.GlobalProfile, "Overridden by your global settings")]
    [InlineData(NvApi.SettingOrigin.BaseProfile, "Overridden by the driver")]
    public void AnActiveOverride_NamesTheLayerItCameFrom(NvApi.SettingOrigin origin, string expected)
    {
        var p = DlssCardViewModel.Project(Result(
            shipped: new[] { Ship("Super Resolution", "310.2.1") },
            profile: Profile(),
            settings: new[] { Toggle("Super Resolution", 1, origin) }));

        Assert.Equal(expected, p.Rows[0].State);
    }

    [Fact]
    public void AnyExistingOverride_WarnsThatSomethingElseSetIt()
    {
        // TrayTrigger has no write path yet, so every override found came from elsewhere. The plan
        // forbids silently overwriting one.
        var p = DlssCardViewModel.Project(Result(
            shipped: new[] { Ship("Super Resolution", "310.2.1") },
            profile: Profile(),
            settings: new[] { Toggle("Super Resolution", 1, NvApi.SettingOrigin.GlobalProfile) }));

        Assert.NotNull(p.ExternalOverrideNotice);
        Assert.Contains("TrayTrigger has not changed anything", p.ExternalOverrideNotice);
    }

    [Fact]
    public void FeaturesAppearInNvidiasOrder_AndOnlyWhenShipped()
    {
        var p = DlssCardViewModel.Project(Result(
            shipped: new[] { Ship("Frame Generation", "3.5.0"), Ship("Super Resolution", "310.2.1") },
            profile: Profile()));

        Assert.Equal(new[] { "Super Resolution", "Frame Generation" }, p.Rows.Select(r => r.Feature));
        Assert.DoesNotContain(p.Rows, r => r.Feature == "Ray Reconstruction");
    }

    [Fact]
    public void DuplicateCopiesOfOneFeature_CollapseToOneRow()
    {
        // A game can carry the same runtime in several folders; three identical rows is noise.
        var p = DlssCardViewModel.Project(Result(
            shipped: new[] { Ship("Super Resolution", "310.2.1"), Ship("Super Resolution", "310.2.1") },
            profile: Profile()));

        Assert.Single(p.Rows);
    }

    [Fact]
    public void DriverLine_SaysTheVersionOnce_WhenAllFeaturesMatch()
    {
        var p = DlssCardViewModel.Project(Result(
            shipped: new[] { Ship("Super Resolution", "310.2.1") },
            profile: Profile(),
            driver: new[]
            {
                new NgxModelStore.StoredRuntime("Super Resolution", "310.9.0", 20318464, "a.bin", 1),
                new NgxModelStore.StoredRuntime("Super Resolution", "2.3.4", 131844, "b.bin", 1),
                new NgxModelStore.StoredRuntime("Frame Generation", "310.9.0", 20318464, "c.bin", 1)
            }));

        Assert.Equal("Your driver holds DLSS 310.9.0.", p.DriverLine);
    }

    [Fact]
    public void DriverLine_SpellsOutFeatures_OnlyWhenTheyDiffer()
    {
        var p = DlssCardViewModel.Project(Result(
            shipped: new[] { Ship("Super Resolution", "310.2.1") },
            profile: Profile(),
            driver: new[]
            {
                new NgxModelStore.StoredRuntime("Super Resolution", "310.9.0", 20318464, "a.bin", 1),
                new NgxModelStore.StoredRuntime("Frame Generation", "310.7.0", 20317952, "c.bin", 1)
            }));

        Assert.Contains("Frame Generation 310.7.0", p.DriverLine);
        Assert.Contains("Super Resolution 310.9.0", p.DriverLine);
    }

    [Fact]
    public void NoDriverStore_SaysSo_RatherThanClaimingAVersion()
    {
        var p = DlssCardViewModel.Project(Result(
            shipped: new[] { Ship("Super Resolution", "310.2.1") },
            profile: Profile()));

        Assert.Equal("The driver holds no DLSS runtimes of its own.", p.DriverLine);
    }

    [Fact]
    public void UnknownShippedVersion_IsLabelled_NotBlank()
    {
        var p = DlssCardViewModel.Project(Result(
            shipped: new[] { new DlssProbeService.ShippedRuntime("Super Resolution", "nvngx_dlss.dll", "nvngx_dlss.dll", null, 0) },
            profile: Profile()));

        Assert.Equal("unknown", p.Rows[0].Version);
    }

    [Fact]
    public void CardStaysHidden_UntilLoadHasRun()
    {
        // Constructing the view model must not touch the driver, so nothing can be visible yet.
        var vm = new DlssCardViewModel(@"C:\Games\Test\game.exe");

        Assert.False(vm.IsVisible);
        Assert.False(vm.IsLoading);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task LoadAsync_WithNoExecutable_DoesNothingAndStaysHidden()
    {
        var vm = new DlssCardViewModel(null);

        await vm.LoadAsync();

        Assert.False(vm.IsVisible);
        Assert.Empty(vm.Rows);
    }
}
