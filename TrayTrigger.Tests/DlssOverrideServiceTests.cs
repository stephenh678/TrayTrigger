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
        // DLSS settings can already live on the Global profile, put there by another tool.
        // Writing the inherited value back onto the game's profile would pin it there, so
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
    public void NvidiasOwnPredefinedValue_IsCapturedAsPredefined_AndUndoRestoresItAsNvidias()
    {
        // Writing the same number back would convert NVIDIA's value into a user-set one, which is
        // a different state even though it reads the same - and deleting would leave nothing where
        // NVIDIA had a value. The driver has a call for exactly this, and undo has to use it.
        var (service, driver) = NewService();
        var profile = driver.AddProfile(Exe, "Test Game");
        profile.Settings[SrPreset] = (0x00000002, IsPredefined: true);

        var applied = service.Apply(@"C:\Games\Test\game.exe", "Test Game");
        var record = applied.Records.First(r => r.SettingId == SrPreset);

        Assert.Equal(DlssSettingOrigin.Predefined, record.PreviousOrigin);

        var undone = service.Undo(applied.Records);

        Assert.Equal((0x00000002u, true), profile.Settings[SrPreset]);
        Assert.Contains(undone.Details, d => d.SettingId == SrPreset && d.Outcome == DlssSettingOutcome.Restored);
        Assert.DoesNotContain(undone.Records, r => r.SettingId == SrPreset);
    }

    // --- Putting it back exactly ------------------------------------------------------------

    [Fact]
    public void AProfileTrayTriggerCreated_IsRemovedAgainOnUndo()
    {
        // Otherwise every game ever overridden leaves an empty profile in the driver for good.
        var (service, driver) = NewService();

        var applied = service.Apply(TestExe, "Test Game");
        Assert.All(applied.Records, r => Assert.True(r.ProfileCreated));
        Assert.True(driver.Profiles.ContainsKey(Exe));

        service.Undo(applied.Records);

        Assert.False(driver.Profiles.ContainsKey(Exe));
    }

    [Fact]
    public void AProfileNvidiaShipped_IsNeverRemoved()
    {
        var (service, driver) = NewService();
        driver.AddProfile(Exe, "Test Game");

        var applied = service.Apply(TestExe, "Test Game");
        Assert.All(applied.Records, r => Assert.False(r.ProfileCreated));

        service.Undo(applied.Records);

        Assert.True(driver.Profiles.ContainsKey(Exe));
    }

    [Fact]
    public void ACreatedProfileTheUserHasSincePutSomethingIn_IsLeftAlone()
    {
        var (service, driver) = NewService();
        var applied = service.Apply(TestExe, "Test Game");
        driver.Profiles[Exe].Settings[0x12345678] = (1, false);   // theirs, added afterwards

        service.Undo(applied.Records);

        Assert.True(driver.Profiles.ContainsKey(Exe));
        Assert.Equal(1u, driver.Profiles[Exe].Settings[0x12345678].Value);
        Assert.False(driver.Profiles[Exe].Settings.ContainsKey(SrEnable));
    }

    [Fact]
    public void ProfilesAreLookedUpByFullPath_NotByBareFileName()
    {
        // NVIDIA's own entries can be qualified by folder or launcher, so a bare game.exe can
        // match another game's profile. The record keeps the path so undo asks the same question.
        var (service, driver) = NewService();
        driver.AddProfile(Exe, "Test Game");

        var applied = service.Apply(TestExe, "Test Game");
        Assert.Equal(TestExe, driver.FindRequests[0]);
        Assert.All(applied.Records, r => Assert.Equal(TestExe, r.ExecutablePath));

        driver.FindRequests.Clear();
        service.Undo(applied.Records);
        Assert.Equal(TestExe, driver.FindRequests[0]);
    }

    [Fact]
    public void AFailedLookup_IsNotAMissingProfile_SoApplyCreatesNothing()
    {
        var (service, driver) = NewService();
        driver.FindError = "NVAPI_ERROR (-1)";

        var result = service.Apply(TestExe, "Test Game");

        Assert.False(result.Succeeded);
        Assert.Empty(driver.CreatedProfiles);
        Assert.Equal(0, driver.SaveCount);
    }

    [Fact]
    public void AFailedLookupDuringUndo_KeepsEveryRecord()
    {
        // Reading "profile gone" into a failed lookup would drop the only means of undoing values
        // that are still sitting in the driver.
        var (service, driver) = NewService();
        driver.AddProfile(Exe, "Test Game");
        var applied = service.Apply(TestExe, "Test Game");
        driver.FindError = "NVAPI_ERROR (-1)";

        var undone = service.Undo(applied.Records);

        Assert.Equal(applied.Records.Count, undone.Records.Count);
        Assert.All(undone.Details, d => Assert.Equal(DlssSettingOutcome.Failed, d.Outcome));
    }

    [Fact]
    public void ASettingThatCannotBeRead_IsNotCapturedAsAbsent()
    {
        // Captured as absent, undo would later delete whatever was really there.
        var (service, driver) = NewService();
        var profile = driver.AddProfile(Exe, "Test Game");
        profile.Settings[SrPreset] = (0x0000000B, false);
        driver.UnreadableSettingIds.Add(SrPreset);

        var applied = service.Apply(TestExe, "Test Game");

        Assert.DoesNotContain(applied.Records, r => r.SettingId == SrPreset);
        Assert.Equal(0x0000000Bu, profile.Settings[SrPreset].Value);
        Assert.Contains(applied.Details, d => d.SettingId == SrPreset && d.Outcome == DlssSettingOutcome.Failed);
    }

    [Fact]
    public void Reapply_WhenAReadFails_WritesNothing()
    {
        var (service, driver) = NewService();
        var profile = driver.AddProfile(Exe, "Test Game");
        var applied = service.Apply(TestExe, "Test Game");
        profile.Settings.Remove(SrEnable);
        driver.UnreadableSettingIds.Add(SrPreset);
        int saves = driver.SaveCount;

        var result = service.Reapply(applied.Records);

        Assert.False(result.Succeeded);
        Assert.Equal(saves, driver.SaveCount);
    }

    [Fact]
    public void ApplyingAgainOverOurOwnValues_KeepsTheOriginalCapture()
    {
        // "The state before this write" is then TrayTrigger's own value. Capturing that would make
        // undo restore the override instead of what the user had.
        var (service, driver) = NewService();
        var profile = driver.AddProfile(Exe, "Test Game");
        profile.Settings[SrPreset] = (0x0000000B, false);

        var first = service.Apply(TestExe, "Test Game");
        var second = service.Apply(TestExe, "Test Game", existing: first.Records);

        Assert.Equal(0x0000000Bu, second.Records.First(r => r.SettingId == SrPreset).PreviousValue);

        service.Undo(second.Records);
        Assert.Equal(0x0000000Bu, profile.Settings[SrPreset].Value);
        Assert.False(profile.Settings.ContainsKey(SrEnable));
    }

    [Fact]
    public void ApplyingAgainOverAStrangersValue_CapturesTheirs()
    {
        // Taking over from a conflict: what goes back on undo is what they had set.
        var (service, driver) = NewService();
        var profile = driver.AddProfile(Exe, "Test Game");
        var first = service.Apply(TestExe, "Test Game");
        profile.Settings[SrPreset] = (0x0000000D, false);

        var second = service.Apply(TestExe, "Test Game", existing: first.Records);
        service.Undo(second.Records);

        Assert.Equal(0x0000000Du, profile.Settings[SrPreset].Value);
    }

    [Fact]
    public void ARecordWhoseWriteNeverLanded_UndoesToNothing_AndIsDropped()
    {
        // Something reverted every value: the driver is already as it was.
        var (service, driver) = NewService();
        var profile = driver.AddProfile(Exe, "Test Game");
        var applied = service.Apply(TestExe, "Test Game");
        foreach (var r in applied.Records) profile.Settings.Remove(r.SettingId);
        int saves = driver.SaveCount;

        var undone = service.Undo(applied.Records);

        Assert.Empty(undone.Records);
        Assert.False(undone.HadForeignChanges);
        Assert.Equal(saves, driver.SaveCount);
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


    // --- The write-back check ---------------------------------------------------------------

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
        // 0x00634291 is the real example: widely listed as a DLSS setting, and no driver has it -
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
    public void Reapply_WhenTheWholeProfileIsGone_RecreatesIt()
    {
        // A clean driver install drops a profile TrayTrigger created. That is not a conflict, and
        // leaving it would keep the switch on over a driver that has nothing.
        var (service, driver) = NewService();
        var applied = service.Apply(TestExe, "Test Game");
        driver.Profiles.Remove(Exe);

        var result = service.Reapply(applied.Records);

        Assert.True(result.Succeeded);
        Assert.False(result.HadForeignChanges);
        Assert.Equal(1u, driver.Profiles[Exe].Settings[SrEnable].Value);
        Assert.Equal(applied.Records[0].ProfileName, driver.Profiles[Exe].Name);
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

    // --- Restore All ------------------------------------------------------------------------

    [Fact]
    public void RestoreAll_PutsEveryGameBack_AndLeavesNothingHeld()
    {
        var (service, driver) = NewService();
        var known = driver.AddProfile(Exe, "Test Game");
        known.Settings[SrPreset] = (0x0000000B, false);

        var first = new GameEntry { Name = "Known" };
        first.DlssSettings.AddRange(service.Apply(TestExe, "Known").Records);
        var second = new GameEntry { Name = "Unknown" };
        second.DlssSettings.AddRange(service.Apply(@"C:\Games\Other\other.exe", "Unknown").Records);
        var untouched = new GameEntry { Name = "Never on" };

        var result = service.RestoreAll(new[] { first, second, untouched });

        Assert.Equal(2, result.Games);
        Assert.Equal(0, result.Failed);
        Assert.Empty(first.DlssSettings);
        Assert.Empty(second.DlssSettings);
        Assert.Equal(0x0000000Bu, known.Settings[SrPreset].Value);
        Assert.False(driver.Profiles.ContainsKey("other.exe"));   // the profile TrayTrigger created
    }

    [Fact]
    public void RestoreAll_WhenTheDriverRefuses_KeepsTheRecords_SoItCanBeTriedAgain()
    {
        var (service, driver) = NewService();
        driver.AddProfile(Exe, "Test Game");
        var game = new GameEntry { Name = "Known" };
        game.DlssSettings.AddRange(service.Apply(TestExe, "Known").Records);
        int held = game.DlssSettings.Count;
        driver.SaveError = "NVAPI_ACCESS_DENIED";

        var result = service.RestoreAll(new[] { game });

        Assert.Equal(1, result.Failed);
        Assert.Equal(held, game.DlssSettings.Count);
    }
}
