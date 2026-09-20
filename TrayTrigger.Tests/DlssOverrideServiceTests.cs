using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.Tests.Fakes;

namespace TrayTrigger.Tests;

/// <summary>
/// The ownership rules from docs/dlss-plan.md. The two sequences the plan says a single boolean
/// gets wrong are the first two tests here, because they are the reason this record exists.
/// </summary>
public class DlssOverrideServiceTests
{
    private const uint SrEnable = 0x10E41E01;   // DLSS - Enable DLL Override
    private const uint SrPreset = 0x10E41DF3;   // DLSS - Forced Preset Letter
    private const string Exe = "game.exe";
    private const string TestExe = @"C:\Games\Test\game.exe";

    private static (DlssOverrideService Service, FakeDrsBackend Driver) NewService()
    {
        var driver = new FakeDrsBackend();
        return (new DlssOverrideService(driver), driver);
    }

    // --- The two sequences a single flag breaks -------------------------------------------

    [Fact]
    public void UndoRestoresAPresetTheUserChose_RatherThanDeletingIt()
    {
        // Plan sequence 1: a user has their own presets, lets TrayTrigger replace them, then undoes.
        // Deleting would destroy a choice TrayTrigger never owned.
        var (service, driver) = NewService();
        var profile = driver.AddProfile(Exe, "Test Game");
        profile.Settings[SrPreset] = (0x0000000B, false);   // the user's own preset letter K

        var applied = service.Apply(@"C:\Games\Test\game.exe", "Test Game");
        Assert.True(applied.Succeeded);
        Assert.Equal(0x00FFFFFFu, profile.Settings[SrPreset].Value);

        var undone = service.Undo(applied.Records);

        Assert.True(undone.Succeeded);
        Assert.Equal(0x0000000Bu, profile.Settings[SrPreset].Value);
        Assert.Contains(undone.Details, d => d.SettingId == SrPreset && d.Outcome == DlssSettingOutcome.Restored);
    }

    [Fact]
    public void AValueSomethingElseChangedAfterwards_IsLeftAlone()
    {
        // Plan sequence 2: the user changes a preset in Profile Inspector after TrayTrigger applied
        // one. Undo must not erase that newer choice.
        var (service, driver) = NewService();
        var profile = driver.AddProfile(Exe, "Test Game");

        var applied = service.Apply(@"C:\Games\Test\game.exe", "Test Game");
        profile.Settings[SrPreset] = (0x0000000D, false);   // changed elsewhere, after we wrote

        var undone = service.Undo(applied.Records);

        Assert.Equal(0x0000000Du, profile.Settings[SrPreset].Value);
        Assert.True(undone.HadForeignChanges);
        Assert.Contains(undone.Details, d => d.SettingId == SrPreset && d.Outcome == DlssSettingOutcome.SkippedForeignChange);
    }

    // --- Capture ---------------------------------------------------------------------------

    [Fact]
    public void AnAbsentSetting_IsCapturedAsAbsent_AndUndoDeletesIt()
    {
        var (service, driver) = NewService();
        var profile = driver.AddProfile(Exe, "Test Game");

        var applied = service.Apply(@"C:\Games\Test\game.exe", "Test Game");
        var record = applied.Records.First(r => r.SettingId == SrEnable);

        Assert.Null(record.PreviousValue);
        Assert.Equal(DlssSettingOrigin.Absent, record.PreviousOrigin);

        service.Undo(applied.Records);
        Assert.False(profile.Settings.ContainsKey(SrEnable));
    }

    [Fact]
    public void AnInheritedSetting_IsCapturedAsInherited_AndUndoDeletesRatherThanPinningIt()
    {
        // The case on the development machine: four DLSS settings already live on the Global
        // profile. Writing the inherited value back onto the game's profile would pin it there, so
        // it would stop following the Global profile - the machine would not be as it was.
        var (service, driver) = NewService();
        var profile = driver.AddProfile(Exe, "Test Game");
        driver.GlobalProfile[SrEnable] = 1;

        var applied = service.Apply(@"C:\Games\Test\game.exe", "Test Game");
        var record = applied.Records.First(r => r.SettingId == SrEnable);

        Assert.Equal(DlssSettingOrigin.Inherited, record.PreviousOrigin);
        Assert.Equal(1u, record.PreviousValue);

        var undone = service.Undo(applied.Records);

        Assert.False(profile.Settings.ContainsKey(SrEnable));
        Assert.Contains(undone.Details, d => d.SettingId == SrEnable && d.Outcome == DlssSettingOutcome.Deleted);
    }

    [Fact]
    public void NvidiasOwnPredefinedValue_IsCapturedAsPredefined_AndUndoDeletesRatherThanClaimingIt()
    {
        // Writing the same number back would convert NVIDIA's value into a user-set one, which is
        // a different state even though it reads the same.
        var (service, driver) = NewService();
        var profile = driver.AddProfile(Exe, "Test Game");
        profile.Settings[SrPreset] = (0x00000002, IsPredefined: true);

        var applied = service.Apply(@"C:\Games\Test\game.exe", "Test Game");
        var record = applied.Records.First(r => r.SettingId == SrPreset);

        Assert.Equal(DlssSettingOrigin.Predefined, record.PreviousOrigin);

        service.Undo(applied.Records);
        Assert.False(profile.Settings.ContainsKey(SrPreset));
    }

    [Fact]
    public void AMatchingValueArrivingFromTheGlobalProfile_IsNotOurs()
    {
        // Same number, different layer. Our write was removed; what is left belongs to somebody
        // else, so undo must not touch it.
        var (service, driver) = NewService();
        var profile = driver.AddProfile(Exe, "Test Game");

        var applied = service.Apply(@"C:\Games\Test\game.exe", "Test Game");
        profile.Settings.Remove(SrEnable);
        driver.GlobalProfile[SrEnable] = 1;   // the value we wrote, but inherited now

        var undone = service.Undo(applied.Records);

        Assert.Contains(undone.Details, d => d.SettingId == SrEnable && d.Outcome == DlssSettingOutcome.SkippedForeignChange);
        Assert.Equal(1u, driver.GlobalProfile[SrEnable]);
    }

    // --- Writing ---------------------------------------------------------------------------

    [Fact]
    public void AGameNvidiaDoesNotKnow_GetsAProfileCreatedForIt()
    {
        var (service, driver) = NewService();

        var applied = service.Apply(@"C:\Games\Indie\indie.exe", "Indie Game");

        Assert.True(applied.Succeeded);
        Assert.Single(driver.CreatedProfiles);
        Assert.Equal("Indie Game (indie.exe)", driver.CreatedProfiles[0]);
    }

    [Fact]
    public void ApplyWritesEverySettingInTheRecipe()
    {
        var (service, driver) = NewService();
        driver.AddProfile(Exe, "Test Game");

        var applied = service.Apply(@"C:\Games\Test\game.exe", "Test Game");

        Assert.Equal(DlssProbeService.Settings.Length, applied.Records.Count);
        foreach (var def in DlssProbeService.Settings)
        {
            var record = applied.Records.First(r => r.SettingId == def.Id);
            Assert.Equal(def.RecommendedValue, record.WrittenValue);
            Assert.Equal(def.FeatureCode, record.Feature);
            Assert.Equal(Exe, record.ApplicationName);
            Assert.Equal("Test Game", record.ProfileName);
        }
    }

    [Fact]
    public void ARefusedSave_ReportsFailureAndPersistsNothing()
    {
        // The unelevated-write case. Returning records here would leave the game claiming an
        // override that never reached the driver.
        var (service, driver) = NewService();
        var profile = driver.AddProfile(Exe, "Test Game");
        driver.SaveError = "NVAPI_ACCESS_DENIED";

        var applied = service.Apply(@"C:\Games\Test\game.exe", "Test Game");

        Assert.False(applied.Succeeded);
        Assert.Contains("NVAPI_ACCESS_DENIED", applied.Error);
        Assert.Empty(applied.Records);
        Assert.Empty(profile.Settings);
        Assert.Equal(0, driver.SaveCount);
    }

    [Fact]
    public void AnUnavailableDriver_FailsWithItsOwnReason()
    {
        var driver = new FakeDrsBackend { OpenError = "no NVIDIA display driver" };
        var service = new DlssOverrideService(driver);

        var applied = service.Apply(@"C:\Games\Test\game.exe", "Test Game");

        Assert.False(applied.Succeeded);
        Assert.Contains("no NVIDIA display driver", applied.Error);
    }


    // --- Undo bookkeeping ------------------------------------------------------------------

    [Fact]
    public void UndoWithNothingRecorded_SucceedsWithoutTouchingTheDriver()
    {
        var (service, driver) = NewService();

        var undone = service.Undo(Array.Empty<DlssSettingRecord>());

        Assert.True(undone.Succeeded);
        Assert.Equal(0, driver.SaveCount);
    }

    [Fact]
    public void UndoKeepsTheRecordsItCouldNotApply_AndDropsTheOnesItDid()
    {
        // A skipped setting is still one TrayTrigger wrote; forgetting it would lose the only
        // evidence of what it owns.
        var (service, driver) = NewService();
        var profile = driver.AddProfile(Exe, "Test Game");

        var applied = service.Apply(@"C:\Games\Test\game.exe", "Test Game");
        profile.Settings[SrPreset] = (0x0000000D, false);   // changed elsewhere

        var undone = service.Undo(applied.Records);

        var kept = Assert.Single(undone.Records);
        Assert.Equal(SrPreset, kept.SettingId);
    }

    [Fact]
    public void UndoWhenTheProfileIsGone_IsNotAnError()
    {
        // A driver reset removes the profile. There is nothing to put back and nothing to warn about.
        var (service, driver) = NewService();
        driver.AddProfile(Exe, "Test Game");
        var applied = service.Apply(@"C:\Games\Test\game.exe", "Test Game");

        driver.Profiles.Clear();
        var undone = service.Undo(applied.Records);

        Assert.True(undone.Succeeded);
        Assert.False(undone.HadForeignChanges);
    }

    [Fact]
    public void ReApplying_RecapturesAgainstTheCurrentState()
    {
        // The second capture must describe the state immediately before the second write - which,
        // after a first apply, is TrayTrigger's own value.
        var (service, _) = NewService();
        var first = service.Apply(@"C:\Games\Test\game.exe", "Test Game");
        var second = service.Apply(@"C:\Games\Test\game.exe", "Test Game");

        Assert.Equal(DlssSettingOrigin.Absent, first.Records.First(r => r.SettingId == SrEnable).PreviousOrigin);
        Assert.Equal(DlssSettingOrigin.UserSet, second.Records.First(r => r.SettingId == SrEnable).PreviousOrigin);
        Assert.Equal(1u, second.Records.First(r => r.SettingId == SrEnable).PreviousValue);
    }

    // --- Naming -----------------------------------------------------------------------------


    // --- Layer 1: the write-back check ------------------------------------------------------

    [Fact]
    public void ApplyVerifiesAgainstAFreshSession_AndReportsWhenAValueDidNotLand()
    {
        // The driver reports the save succeeded but the value is not there afterwards. Saying
        // "applied" would be the most misleading thing the card could do.
        var driver = new FakeDrsBackend();
        var profile = driver.AddProfile(Exe, "Test Game");
        driver.AfterSave = () => profile.Settings.Remove(SrEnable);
        var service = new DlssOverrideService(driver);

        var applied = service.Apply(@"C:\Games\Test\game.exe", "Test Game");

        Assert.True(applied.HadWriteBackFailures);
        Assert.Contains(applied.Details, d => d.SettingId == SrEnable && d.Outcome == DlssSettingOutcome.WriteBackFailed);
        Assert.Contains(applied.Details, d => d.SettingId == SrPreset && d.Outcome == DlssSettingOutcome.Applied);
    }

    [Fact]
    public void AWriteThatLands_ReportsNoVerificationProblem()
    {
        var (service, driver) = NewService();
        driver.AddProfile(Exe, "Test Game");

        var applied = service.Apply(@"C:\Games\Test\game.exe", "Test Game");

        Assert.False(applied.HadWriteBackFailures);
        Assert.All(applied.Details, d => Assert.Equal(DlssSettingOutcome.Applied, d.Outcome));
    }

    [Fact]
    public void TheWriteBackCheckUsesASecondSession()
    {
        // NVIDIA's own documentation says DRS sessions do not merge, so reading through the
        // session that just wrote would only show its own in-memory copy and confirm nothing.
        var (service, driver) = NewService();
        driver.AddProfile(Exe, "Test Game");

        service.Apply(@"C:\Games\Test\game.exe", "Test Game");

        Assert.Equal(2, driver.SessionsOpened);
    }

    [Fact]
    public void AValueThatReadsBackAsInherited_IsNotTreatedAsLanded()
    {
        // Same number, wrong layer: our write is not there, the Global profile's is.
        var driver = new FakeDrsBackend();
        var profile = driver.AddProfile(Exe, "Test Game");
        driver.AfterSave = () =>
        {
            profile.Settings.Remove(SrEnable);
            driver.GlobalProfile[SrEnable] = 1;
        };
        var service = new DlssOverrideService(driver);

        var applied = service.Apply(@"C:\Games\Test\game.exe", "Test Game");

        Assert.Contains(applied.Details, d => d.SettingId == SrEnable && d.Outcome == DlssSettingOutcome.WriteBackFailed);
    }

    // --- Setting ids this driver does not have ---------------------------------------------

    [Fact]
    public void AnIdTheDriverDoesNotHave_IsSteppedOver_NotFailed()
    {
        // The recipe carried 0x00634291 - "the gate" - for months. No driver has ever had it:
        // GetSettingNameFromId does not recognise it and SetSetting refuses it. An id NVIDIA
        // retires later should behave the same way, quietly, rather than looking like a fault.
        var driver = new FakeDrsBackend();
        driver.AddProfile(Exe, "Test Game");
        driver.UnknownSettingIds.Add(SrPreset);
        var service = new DlssOverrideService(driver);

        var applied = service.Apply(TestExe, "Test Game");

        Assert.True(applied.Succeeded);
        Assert.Contains(applied.Details, d => d.SettingId == SrPreset && d.Outcome == DlssSettingOutcome.NotSupportedByDriver);
        Assert.DoesNotContain(applied.Records, r => r.SettingId == SrPreset);
    }



    // --- Step 5: the pre-launch re-apply and its three-way rule -----------------------------

    [Fact]
    public void Reapply_WhenNothingChanged_WritesNothing()
    {
        var (service, driver) = NewService();
        driver.AddProfile(Exe, "Test Game");
        var applied = service.Apply(TestExe, "Test Game");
        int savesAfterApply = driver.SaveCount;

        var result = service.Reapply(applied.Records);

        Assert.True(result.Succeeded);
        Assert.True(result.WasAlreadyCorrect);
        Assert.Equal(savesAfterApply, driver.SaveCount);
    }

    [Fact]
    public void Reapply_WhenSomethingRevertedUsToTheCapturedValue_PutsItBack()
    {
        // The NVIDIA App case the step exists for: it reverts overrides on games it does not list.
        var (service, driver) = NewService();
        var profile = driver.AddProfile(Exe, "Test Game");
        var applied = service.Apply(TestExe, "Test Game");

        foreach (var record in applied.Records) profile.Settings.Remove(record.SettingId);

        var result = service.Reapply(applied.Records);

        Assert.True(result.Succeeded);
        Assert.False(result.HadForeignChanges);
        Assert.Equal(1u, profile.Settings[SrEnable].Value);
        Assert.Equal(0x00FFFFFFu, profile.Settings[SrPreset].Value);
    }

    [Fact]
    public void Reapply_RestoresAnInheritedCapture_ByWritingOurValueBack()
    {
        // Captured as Inherited, reverted to Inherited: still "what we captured", so re-apply.
        var (service, driver) = NewService();
        var profile = driver.AddProfile(Exe, "Test Game");
        driver.GlobalProfile[SrEnable] = 1;
        var applied = service.Apply(TestExe, "Test Game");
        profile.Settings.Remove(SrEnable);   // back to inheriting

        var result = service.Reapply(applied.Records);

        Assert.False(result.HadForeignChanges);
        Assert.Equal(1u, profile.Settings[SrEnable].Value);
        Assert.False(profile.Settings[SrEnable].IsPredefined);
    }

    [Fact]
    public void Reapply_WhenAValueIsNeitherOursNorTheCapture_WritesNothingAtAll()
    {
        // One conflicting setting means something else is managing this game. Writing the others
        // would be exactly the overwrite the rule exists to prevent, so everything is read before
        // anything is written.
        var (service, driver) = NewService();
        var profile = driver.AddProfile(Exe, "Test Game");
        var applied = service.Apply(TestExe, "Test Game");

        profile.Settings.Remove(SrEnable);              // reverted - would normally be re-applied
        profile.Settings[SrPreset] = (0x0000000D, false); // but this one is a stranger's
        int savesBefore = driver.SaveCount;

        var result = service.Reapply(applied.Records);

        Assert.True(result.HadForeignChanges);
        Assert.Equal(savesBefore, driver.SaveCount);
        Assert.False(profile.Settings.ContainsKey(SrEnable));
        Assert.Equal(0x0000000Du, profile.Settings[SrPreset].Value);
    }

    [Fact]
    public void Reapply_WhenConflicted_AppliesNothing_ButStillReportsEachSettingTruthfully()
    {
        // The conflict is per game - nothing is written - but a setting that is still exactly as
        // TrayTrigger left it should not be reported as though a stranger had touched it.
        var (service, driver) = NewService();
        var profile = driver.AddProfile(Exe, "Test Game");
        var applied = service.Apply(TestExe, "Test Game");
        profile.Settings[SrPreset] = (0x0000000D, false);

        var result = service.Reapply(applied.Records);

        Assert.True(result.HadForeignChanges);
        Assert.DoesNotContain(result.Details, d => d.Outcome == DlssSettingOutcome.Applied);
        Assert.Contains(result.Details, d => d.SettingId == SrPreset && d.Outcome == DlssSettingOutcome.SkippedForeignChange);
        Assert.Contains(result.Details, d => d.SettingId == SrEnable && d.Outcome == DlssSettingOutcome.AlreadyCorrect);
    }

    [Fact]
    public void Reapply_KeepsTheRecords_SoTheUserCanStillUndo()
    {
        var (service, driver) = NewService();
        var profile = driver.AddProfile(Exe, "Test Game");
        var applied = service.Apply(TestExe, "Test Game");
        profile.Settings[SrPreset] = (0x0000000D, false);

        var result = service.Reapply(applied.Records);

        Assert.Equal(applied.Records.Count, result.Records.Count);
    }

    [Fact]
    public void Reapply_WithNothingRecorded_IsANoOp()
    {
        var (service, driver) = NewService();

        var result = service.Reapply(Array.Empty<DlssSettingRecord>());

        Assert.True(result.Succeeded);
        Assert.Equal(0, driver.SessionsOpened);
    }

    [Fact]
    public void Reapply_WhenTheDriverIsUnavailable_FailsWithoutThrowing()
    {
        var service = new DlssOverrideService(new FakeDrsBackend { OpenError = "no driver" });

        var result = service.Reapply(new[] { new DlssSettingRecord { ApplicationName = Exe, SettingId = SrEnable, WrittenValue = 1 } });

        Assert.False(result.Succeeded);
        Assert.Contains("no driver", result.Error);
    }

    [Theory]
    [InlineData(DlssSettingOrigin.Absent, null, null, true)]           // captured absent, still absent
    [InlineData(DlssSettingOrigin.Absent, 1u, DlssSettingOrigin.Inherited, false)]  // absent then inherited is NOT the capture
    [InlineData(DlssSettingOrigin.Inherited, 1u, DlssSettingOrigin.Inherited, true)]
    [InlineData(DlssSettingOrigin.Inherited, 2u, DlssSettingOrigin.Inherited, false)] // right layer, wrong value
    [InlineData(DlssSettingOrigin.UserSet, 5u, DlssSettingOrigin.UserSet, true)]
    [InlineData(DlssSettingOrigin.UserSet, 5u, DlssSettingOrigin.Inherited, false)]   // right value, wrong layer
    public void LooksLikeWhatWeCaptured_ComparesLayerAsWellAsValue(
        DlssSettingOrigin capturedOrigin, uint? currentValue, DlssSettingOrigin? currentOrigin, bool expected)
    {
        var record = new DlssSettingRecord
        {
            PreviousOrigin = capturedOrigin,
            PreviousValue = capturedOrigin == DlssSettingOrigin.Absent ? null : (capturedOrigin == DlssSettingOrigin.UserSet ? 5u : 1u)
        };
        var current = currentValue == null ? null : new DrsSettingReading(currentValue.Value, currentOrigin!.Value);

        Assert.Equal(expected, DlssOverrideService.LooksLikeWhatWeCaptured(current, record));
    }
}
