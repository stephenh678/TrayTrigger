using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.Tests.Fakes;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// The DLSS Override card: two versions and one switch.
///
/// <para>What these protect is the wording and the switch's meaning. The card's whole job is to
/// tell a user what they will get, not to describe the mechanism.</para>
/// </summary>
public class DlssCardViewModelTests
{
    private const string Exe = @"C:\Games\Test\game.exe";

    private static DlssProbeService.ProbeResult Result(
        IReadOnlyList<DlssProbeService.ShippedRuntime>? shipped = null,
        IReadOnlyList<NgxModelStore.StoredRuntime>? driver = null) => new()
        {
            ShippedRuntimes = shipped ?? Array.Empty<DlssProbeService.ShippedRuntime>(),
            DriverRuntimes = driver ?? Array.Empty<NgxModelStore.StoredRuntime>()
        };

    private static DlssProbeService.ShippedRuntime Ship(string feature, string version) =>
        new(feature, @"bin\nvngx_dlss.dll", version);

    private static NgxModelStore.StoredRuntime Store(string feature, string version, uint encoded) =>
        new(feature, version, encoded, "x.bin", 1);

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

    [Fact]
    public async Task TheVersionPairReadsAsBeforeAndAfter_NotAsProse()
    {
        // The whole point of the pair is that it needs no sentence around it. If this ever grows
        // words, the card has started explaining itself again.
        var card = Card(new FakeDrsBackend(), new List<DlssSettingRecord>(),
            probe: Result(
                shipped: new[] { Ship("Super Resolution", "310.1.0") },
                driver: new[] { Store("Super Resolution", "310.9.0", 20318464) }));
        await card.LoadAsync();

        Assert.Equal("310.1.0 → 310.9.0", card.VersionLine);
    }

    [Fact]
    public async Task AGameAlreadyCurrent_SaysSoInsteadOfShowingAnArrowToNowhere()
    {
        var card = Card(new FakeDrsBackend(), new List<DlssSettingRecord>(),
            probe: Result(
                shipped: new[] { Ship("Super Resolution", "310.9.0") },
                driver: new[] { Store("Super Resolution", "310.9.0", 20318464) }));
        await card.LoadAsync();

        Assert.Equal("310.9.0 (already current)", card.VersionLine);
    }

    // ---- What actually loaded last run ---------------------------------------------------------

    private static DlssLastRun Ran(string[] fromNvidia, string[] fromGame, string? gameVersion = "310.1.0") =>
        new() { FromNvidia = fromNvidia.ToList(), FromGame = fromGame.ToList(), GameVersion = gameVersion };

    private static readonly string[] None = Array.Empty<string>();

    private static async Task<DlssCardViewModel> Played(DlssLastRun? lastRun)
    {
        var game = new GameEntry { DlssLastRun = lastRun };
        var card = Card(new FakeDrsBackend(), new List<DlssSettingRecord>(), game: game);
        await card.LoadAsync();
        return card;
    }

    [Fact]
    public async Task BeforeTheGameHasBeenPlayed_ThereIsNoLastRunLine()
    {
        // Everything else on the card is read off disk. This is the only thing that needs a run,
        // and claiming it early would be inventing a result.
        var card = await Played(null);

        Assert.False(card.HasLastRun);
        Assert.Empty(card.LastRunLine);
    }

    [Fact]
    public async Task ARuntimeLoadedFromNvidia_IsTheWholePayoff()
    {
        var card = await Played(Ran(new[] { "310.9.0" }, None));

        Assert.Equal("Last run: loaded 310.9.0 from NVIDIA.", card.LastRunLine);
    }

    [Fact]
    public async Task TheGamesOwnRuntime_IsReportedWithoutCallingItAFailure()
    {
        // An intermediate load state, someone else's profile, or a genuine refusal all read the
        // same. The line says what was seen and stops.
        var card = await Played(Ran(None, new[] { "310.1.0" }));

        Assert.Equal("Last run: loaded 310.1.0 from the game's own files.", card.LastRunLine);
        Assert.DoesNotContain("fail", card.LastRunLine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AMixOfSources_NamesBothRatherThanPickingOne()
    {
        var card = await Played(Ran(new[] { "310.9.0" }, new[] { "310.1.0" }));

        Assert.Equal(
            "Last run: loaded 310.9.0 from NVIDIA and 310.1.0 from the game's own files.",
            card.LastRunLine);
    }

    [Fact]
    public async Task AReadingFromBeforeAGamePatch_IsDroppedRatherThanShown()
    {
        // The probe reports the game shipping 310.1.0; the reading was taken when it shipped
        // 309.0.0, so it describes a setup that no longer exists.
        var card = await Played(Ran(new[] { "310.9.0" }, None, gameVersion: "309.0.0"));

        Assert.False(card.HasLastRun);
    }

    [Fact]
    public async Task AReadingWithNoGameVersionRecorded_IsStillShown()
    {
        // Unknown-versus-known is not a patch. Discarding it would lose the only evidence the
        // override worked.
        var card = await Played(Ran(new[] { "310.9.0" }, None, gameVersion: null));

        Assert.Equal("Last run: loaded 310.9.0 from NVIDIA.", card.LastRunLine);
    }

    [Fact]
    public async Task SwitchingTheOverride_ForgetsWhatTheOldSetupDid()
    {
        var game = new GameEntry { DlssLastRun = Ran(new[] { "310.9.0" }, None) };
        var card = Card(new FakeDrsBackend(), new List<DlssSettingRecord>(), game: game);
        await card.LoadAsync();
        Assert.True(card.HasLastRun);

        card.OverrideEnabled = true;
        await WaitForIdle(card);

        Assert.False(card.HasLastRun);
        Assert.Null(game.DlssLastRun);
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
        var driverSavesAtEachPersist = new List<int>();

        var card = Card(driver, records, () => driverSavesAtEachPersist.Add(driver.SaveCount));
        await card.LoadAsync();
        card.OverrideEnabled = true;
        await WaitForIdle(card);

        Assert.True(card.OverrideEnabled);
        Assert.Equal(DlssProbeService.Settings.Length, records.Count);
        // Once before the driver commits - so a crash in between leaves a record that undoes to
        // nothing rather than a write nothing can undo - and once after, with the result.
        Assert.Equal(new[] { 0, 1 }, driverSavesAtEachPersist);
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

    // ---- What the card shows --------------------------------------------------------------------

    [Fact]
    public void WithNoNvidiaDriver_TheCardStaysHidden_WhateverTheGameShips()
    {
        var p = DlssCardViewModel.Project(
            Result(shipped: new[] { Ship("Super Resolution", "310.1.0") }) with { DriverAvailable = false });

        Assert.False(p.HasDlss);
    }

    [Fact]
    public void TheVersionPair_IsForOneFeature_NotTheOldestOfOneAgainstTheNewestOfAnother()
    {
        var p = DlssCardViewModel.Project(Result(
            shipped: new[] { Ship("Super Resolution", "310.2.0"), Ship("Frame Generation", "310.1.0") },
            driver: new[] { Store("Super Resolution", "310.1.0", 20316416), Store("Frame Generation", "310.4.0", 20317184) }));

        Assert.Equal("310.1.0", p.GameVersion);
        Assert.Equal("310.4.0", p.DriverVersion);

        var srOnly = DlssCardViewModel.Project(Result(
            shipped: new[] { Ship("Super Resolution", "310.2.0") },
            driver: new[] { Store("Super Resolution", "310.1.0", 20316416), Store("Frame Generation", "310.4.0", 20317184) }));

        Assert.Equal("310.1.0", srOnly.DriverVersion);
        Assert.False(srOnly.DriverIsNewer);
    }

    // ---- The switch shows what the driver holds ------------------------------------------------

    private static uint SrPreset => DlssProbeService.Settings.First(d => d.FeatureCode == "SR" && d.Name.Contains("Preset")).Id;

    private static async Task<(DlssCardViewModel Card, FakeDrsBackend Driver, FakeDrsBackend.FakeProfile Profile, GameEntry Game)> SwitchedOn()
    {
        var driver = new FakeDrsBackend();
        var profile = driver.AddProfile("game.exe", "Test Game");
        var game = new GameEntry();
        var card = Card(driver, game.DlssSettings, game: game);
        await card.LoadAsync();
        card.OverrideEnabled = true;
        await WaitForIdle(card);
        return (card, driver, profile, game);
    }

    /// <summary>The dialog reopened: a new card over the same game and driver.</summary>
    private static async Task<DlssCardViewModel> Reopened(FakeDrsBackend driver, GameEntry game)
    {
        var card = Card(driver, game.DlssSettings, game: game);
        await card.LoadAsync();
        return card;
    }

    [Fact]
    public async Task ReopeningTheDialog_ShowsTheSwitchOn_WhileTheDriverStillHoldsOurValues()
    {
        var (_, driver, _, game) = await SwitchedOn();

        Assert.True((await Reopened(driver, game)).OverrideEnabled);
    }

    [Fact]
    public async Task WhenSomethingElseHasChangedASetting_TheSwitchReadsOff_AndCanBeSwitchedOnAgainToTakeOver()
    {
        // Read from the driver, not remembered. Were it to read on, unticking would run a restore
        // that leaves the stranger's value alone, and there would be no way to apply again.
        var (_, driver, profile, game) = await SwitchedOn();
        profile.Settings[SrPreset] = (0x0000000D, false);   // NVIDIA App, Profile Inspector...

        var card = await Reopened(driver, game);
        Assert.False(card.OverrideEnabled);
        Assert.True(card.CanRestore);

        card.OverrideEnabled = true;
        await WaitForIdle(card);

        Assert.True(card.OverrideEnabled);
        // Their value is what undo now gives back; ours, where still ours, kept the original capture.
        Assert.Equal(0x0000000Du, game.DlssSettings.First(r => r.SettingId == SrPreset).PreviousValue);
    }

    [Fact]
    public async Task WhenTheWholeProfileIsGone_TheSwitchReadsOff()
    {
        var (_, driver, _, game) = await SwitchedOn();
        driver.Profiles.Remove("game.exe");   // a clean driver install

        Assert.False((await Reopened(driver, game)).OverrideEnabled);
    }

    [Fact]
    public async Task Restore_HandsAStrangersSettingsBack_AndLeavesNothingHeld()
    {
        var (_, driver, profile, game) = await SwitchedOn();
        profile.Settings[SrPreset] = (0x0000000D, false);
        var card = await Reopened(driver, game);

        await ((AsyncRelayCommand)card.RestoreCommand).ExecuteAsync();

        Assert.Empty(game.DlssSettings);
        Assert.False(card.OverrideEnabled);
        Assert.False(card.CanRestore);
        Assert.Equal(0x0000000Du, profile.Settings[SrPreset].Value);
    }

    // ---- Loading -----------------------------------------------------------------------------

    [Fact]
    public void ConstructingTheCard_NeverTouchesTheDriver()
    {
        var card = new DlssCardViewModel(Exe);

        Assert.False(card.IsVisible);
        Assert.Empty(card.VersionLine);
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
