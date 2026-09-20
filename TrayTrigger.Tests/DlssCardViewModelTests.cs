using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.Tests.Fakes;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// The DLSS Override card: two versions and one switch.
///
/// <para>What these protect is the wording and the switch's meaning. The card's whole job is to
/// tell a user what they will get, and an earlier version of it described the mechanism instead -
/// three feature rows, a version each, and "Settings saved".</para>
/// </summary>
public class DlssCardViewModelTests
{
    private const string Exe = @"C:\Games\Test\game.exe";

    private static DlssProbeService.ProbeResult Result(
        IReadOnlyList<DlssProbeService.ShippedRuntime>? shipped = null,
        NvApi.DrsProfileInfo? profile = null,
        IReadOnlyList<DlssProbeService.SettingState>? settings = null,
        IReadOnlyList<NgxModelStore.StoredRuntime>? driver = null) => new()
        {
            ExecutablePath = Exe,
            ExecutableName = "game.exe",
            Profile = profile,
            SettingStates = settings ?? Array.Empty<DlssProbeService.SettingState>(),
            ShippedRuntimes = shipped ?? Array.Empty<DlssProbeService.ShippedRuntime>(),
            DriverRuntimes = driver ?? Array.Empty<NgxModelStore.StoredRuntime>()
        };

    private static DlssProbeService.ShippedRuntime Ship(string feature, string version) =>
        new(feature, "nvngx_dlss.dll", @"bin\nvngx_dlss.dll", version, 1024);

    private static NgxModelStore.StoredRuntime Store(string feature, string version, uint encoded) =>
        new(feature, version, encoded, "x.bin", 1);

    private static DlssProbeService.SettingState Toggle(string feature, uint value, NvApi.SettingOrigin origin)
    {
        var def = DlssProbeService.Settings.First(s => s.Feature == feature && s.Name.Contains("Enable DLL Override"));
        return new DlssProbeService.SettingState(def, new NvApi.DrsSettingValue(def.Id, def.Name, value, origin, false, false, 0), null);
    }

    private static DlssCardViewModel Card(
        FakeDrsBackend driver, List<DlssSettingRecord> records,
        Action? persist = null, DlssProbeService.ProbeResult? probe = null, GameEntry? game = null) =>
        new(Exe, "Test Game", records, persist, new DlssOverrideService(driver),
            _ => probe ?? Result(shipped: new[] { Ship("Super Resolution", "310.1.0") }),
            game ?? new GameEntry());

    // ---- What it says ------------------------------------------------------------------------

    [Fact]
    public void NoShippedDlss_HidesTheCardEntirely()
    {
        Assert.False(DlssCardViewModel.Project(Result()).HasDlss);
    }

    [Fact]
    public void ADriverWithSomethingNewer_SaysSoInOneSentence()
    {
        var p = DlssCardViewModel.Project(Result(
            shipped: new[] { Ship("Super Resolution", "310.1.0") },
            driver: new[] { Store("Super Resolution", "310.9.0", 20318464) }));

        Assert.True(p.DriverIsNewer);
        Assert.Equal("310.1.0", p.GameVersion);
        Assert.Equal("310.9.0", p.DriverVersion);
    }

    [Fact]
    public void AGameAlreadyOnTheDriversVersion_IsNotOfferedAnUpgrade()
    {
        var p = DlssCardViewModel.Project(Result(
            shipped: new[] { Ship("Super Resolution", "310.9.0") },
            driver: new[] { Store("Super Resolution", "310.9.0", 20318464) }));

        Assert.False(p.DriverIsNewer);
    }

    [Theory]
    // The trap: as strings, "310.7.128" sorts after "310.9.0", so a string compare would tell a
    // user their driver is older than the game when it is two releases newer.
    [InlineData("310.9.0", "310.7.128", 1)]
    [InlineData("310.7.128", "310.9.0", -1)]
    [InlineData("310.9.0", "310.9.0", 0)]
    [InlineData("2.3.4", "310.1.0", -1)]
    [InlineData("310.1.0", null, 1)]
    public void VersionsCompareNumerically_NotAsText(string? left, string? right, int expected)
    {
        Assert.Equal(expected, Math.Sign(DlssCardViewModel.Compare(left, right)));
    }

    [Fact]
    public void TheVersionShownIsTheOldestFeature_BecauseThatIsTheOneWithMostToGain()
    {
        var p = DlssCardViewModel.Project(Result(shipped: new[]
        {
            Ship("Super Resolution", "310.7.128"),
            Ship("Frame Generation", "310.1.0")
        }));

        Assert.Equal("310.1.0", p.GameVersion);
    }

    // ---- The switch --------------------------------------------------------------------------

    [Fact]
    public async Task SwitchingOn_WritesTheOverrideAndSavesImmediately()
    {
        // Immediately, not on Save Changes: the driver has already changed when this returns, so
        // Cancel must not be able to strand an override with no record of it.
        var driver = new FakeDrsBackend();
        driver.AddProfile("game.exe", "Test Game");
        var records = new List<DlssSettingRecord>();
        int persisted = 0;

        var card = Card(driver, records, () => persisted++);
        await card.LoadAsync();
        card.OverrideEnabled = true;
        await WaitForIdle(card);

        Assert.True(card.OverrideEnabled);
        Assert.Equal(DlssProbeService.Settings.Length, records.Count);
        Assert.Equal(1, persisted);
        Assert.True(card.CanRestore);
    }

    [Fact]
    public async Task SwitchingOff_PutsItBack()
    {
        var driver = new FakeDrsBackend();
        driver.AddProfile("game.exe", "Test Game");
        var records = new List<DlssSettingRecord>();

        var card = Card(driver, records);
        await card.LoadAsync();
        card.OverrideEnabled = true;
        await WaitForIdle(card);

        card.OverrideEnabled = false;
        await WaitForIdle(card);

        Assert.False(card.OverrideEnabled);
        Assert.Empty(records);
    }

    [Fact]
    public async Task TheSwitchReflectsWhetherTrayTriggerOwnsAnything()
    {
        var driver = new FakeDrsBackend();
        driver.AddProfile("game.exe", "Test Game");

        var card = Card(driver, new List<DlssSettingRecord>());
        await card.LoadAsync();

        Assert.False(card.OverrideEnabled);
        Assert.False(card.CanRestore);
    }

    [Fact]
    public async Task WhenTheDriverRefuses_NothingIsRecordedAndTheReasonIsShown()
    {
        var driver = new FakeDrsBackend { SaveError = "NVAPI_ACCESS_DENIED" };
        driver.AddProfile("game.exe", "Test Game");
        var records = new List<DlssSettingRecord>();

        var card = Card(driver, records);
        await card.LoadAsync();
        card.OverrideEnabled = true;
        await WaitForIdle(card);

        Assert.False(card.OverrideEnabled);
        Assert.Empty(records);
        Assert.Contains("NVAPI_ACCESS_DENIED", card.Status);
    }

    [Fact]
    public async Task WhenTheValuesDoNotReadBack_TheCardDoesNotClaimItWorked()
    {
        var driver = new FakeDrsBackend();
        var profile = driver.AddProfile("game.exe", "Test Game");
        driver.AfterSave = () => profile.Settings.Clear();

        var card = Card(driver, new List<DlssSettingRecord>());
        await card.LoadAsync();
        card.OverrideEnabled = true;
        await WaitForIdle(card);

        Assert.Contains("did not report it back", card.Status);
    }

    [Fact]
    public async Task Restore_PutsBackWhatWasCaptured()
    {
        var driver = new FakeDrsBackend();
        driver.AddProfile("game.exe", "Test Game");
        var records = new List<DlssSettingRecord>();

        var card = Card(driver, records);
        await card.LoadAsync();
        card.OverrideEnabled = true;
        await WaitForIdle(card);

        await ((AsyncRelayCommand)card.RestoreCommand).ExecuteAsync();

        Assert.Empty(records);
        Assert.Contains("Put back", card.Status);
    }

    // ---- The overlay -------------------------------------------------------------------------

    [Fact]
    public void TheOverlayIsOffByDefault_AndSavesWhenTicked()
    {
        var game = new GameEntry();
        int persisted = 0;
        var card = Card(new FakeDrsBackend(), new List<DlssSettingRecord>(), () => persisted++, game: game);

        Assert.False(card.ShowOverlay);

        card.ShowOverlay = true;

        Assert.True(game.DlssShowOverlay);
        Assert.Equal(1, persisted);
    }

    // ---- Notices -----------------------------------------------------------------------------

    [Fact]
    public void AnOverrideTrayTriggerDidNotWrite_IsCalledOut()
    {
        var p = DlssCardViewModel.Project(Result(
            shipped: new[] { Ship("Super Resolution", "310.1.0") },
            settings: new[] { Toggle("Super Resolution", 1, NvApi.SettingOrigin.GlobalProfile) }));

        Assert.NotNull(p.ExternalOverrideNotice);
        Assert.Contains("Something else", p.ExternalOverrideNotice);
    }

    [Fact]
    public void TrayTriggersOwnOverride_IsNotMistakenForAStrangers()
    {
        // Without the records the card would accuse itself the moment it switched on.
        var def = DlssProbeService.Settings.First(s => s.Name.Contains("Enable DLL Override"));
        var owned = new[] { new DlssSettingRecord { SettingId = def.Id, WrittenValue = 1 } };

        var p = DlssCardViewModel.Project(
            Result(shipped: new[] { Ship("Super Resolution", "310.1.0") },
                   settings: new[] { Toggle("Super Resolution", 1, NvApi.SettingOrigin.ApplicationProfile) }),
            owned);

        Assert.Null(p.ExternalOverrideNotice);
    }

    [Fact]
    public void AnOverrideSetToZero_IsNotAnOverride()
    {
        var p = DlssCardViewModel.Project(Result(
            shipped: new[] { Ship("Super Resolution", "310.1.0") },
            settings: new[] { Toggle("Super Resolution", 0, NvApi.SettingOrigin.ApplicationProfile) }));

        Assert.Null(p.ExternalOverrideNotice);
    }

    [Fact]
    public void AConflictedGame_ExplainsItselfInsteadOfSilentlyDoingNothing()
    {
        var game = new GameEntry { DlssConflicted = true };
        var card = Card(new FakeDrsBackend(), new List<DlssSettingRecord>(), game: game);

        Assert.True(card.HasNotice);
        Assert.Contains("stopped re-applying", card.Notice);
    }

    // ---- Details -----------------------------------------------------------------------------

    [Fact]
    public void DetailsListEachFeatureTheGameShips_AndNothingItDoesNot()
    {
        var p = DlssCardViewModel.Project(Result(shipped: new[]
        {
            Ship("Super Resolution", "310.1.0"),
            Ship("Frame Generation", "310.1.0")
        }));

        Assert.Equal(2, p.Details.Count);
        Assert.Contains(p.Details, d => d.StartsWith("Super Resolution"));
        Assert.DoesNotContain(p.Details, d => d.StartsWith("Ray Reconstruction"));
    }

    [Fact]
    public void DetailsIncludeWhatWasSeenLoading_WhenTheGameHasBeenPlayed()
    {
        var observations = new[]
        {
            new DlssObservation
            {
                Feature = "SR",
                State = DlssObservationState.RuntimeObserved,
                Version = "310.9.0",
                LoadedFromPath = @"C:\ProgramData\NVIDIA\NGX\models\dlss\versions\20318464\files\x.bin",
                GameRuntimeVersion = "310.1.0"
            }
        };

        var p = DlssCardViewModel.Project(
            Result(shipped: new[] { Ship("Super Resolution", "310.1.0") }), null, observations);

        Assert.Contains(p.Details, d => d.Contains("310.9.0") && d.Contains("from NVIDIA"));
    }

    [Fact]
    public void AnObservationFromBeforeAGamePatch_IsNotShown()
    {
        var observations = new[]
        {
            new DlssObservation
            {
                Feature = "SR",
                State = DlssObservationState.RuntimeObserved,
                Version = "310.9.0",
                GameRuntimeVersion = "309.0.0"   // the game has been patched since
            }
        };

        var p = DlssCardViewModel.Project(
            Result(shipped: new[] { Ship("Super Resolution", "310.1.0") }), null, observations);

        Assert.DoesNotContain(p.Details, d => d.Contains("310.9.0"));
    }

    // ---- Loading -----------------------------------------------------------------------------

    [Fact]
    public void ConstructingTheCard_NeverTouchesTheDriver()
    {
        var card = new DlssCardViewModel(Exe);

        Assert.False(card.IsVisible);
        Assert.Empty(card.Details);
    }

    [Fact]
    public async Task WithNoExecutable_TheCardStaysHidden()
    {
        var card = new DlssCardViewModel(null);

        await card.LoadAsync();

        Assert.False(card.IsVisible);
    }

    /// <summary>
    /// The switch starts its work without awaiting - a property setter cannot - so a test has to
    /// wait for it to settle.
    /// </summary>
    private static async Task WaitForIdle(DlssCardViewModel card)
    {
        for (int i = 0; i < 200 && card.IsBusy; i++) await Task.Delay(5);
        await Task.Yield();
    }
}
