using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>
/// Turning loaded modules into per-feature observations.
///
/// <para>The rule that matters most here is what the service must NOT say. A loaded runtime is a
/// reading, not a cause, and a failed read is not a failed override - the plan rewrote this
/// section once for overclaiming both ways.</para>
/// </summary>
public class DlssVerificationServiceTests
{
    private const string Store = @"C:\ProgramData\NVIDIA\NGX\models";

    private static DlssProbeService.LoadedRuntime FromStore(string feature, uint encodedVersion) =>
        new("160_E658700.bin", $@"{Store}\{feature}\versions\{encodedVersion}\files\160_E658700.bin",
            null, "NVIDIA NGX", FromDriverStore: true);

    private static DlssProbeService.LoadedRuntime FromGame(string dll, string version) =>
        new(dll, $@"C:\Games\Test\bin\x64\{dll}", version, "NVIDIA DLSS", FromDriverStore: false);

    private static List<DlssObservation> Interpret(
        IEnumerable<DlssProbeService.LoadedRuntime> modules, string? note = null, string? driver = "616.64") =>
        DlssVerificationService.Interpret(modules.ToList(), note, driver, new DateTime(2026, 9, 20));

    [Fact]
    public void ARuntimeFromTheDriverStore_IsReportedWithItsVersionAndSource()
    {
        var observations = Interpret(new[] { FromStore("dlss", 20318464) });
        var sr = observations.Single(o => o.Feature == "SR");

        Assert.Equal(DlssObservationState.RuntimeObserved, sr.State);
        Assert.Equal("310.9.0", sr.Version);
        Assert.True(sr.FromDriverStore);
        Assert.Contains("from NVIDIA", DlssVerificationService.Describe(sr));
    }

    [Fact]
    public void TheStoreVersionComesFromThePath_BecauseTheBinHasNoVersionResource()
    {
        // The substituted runtime is a hashed .bin with no version of its own; the only place the
        // version exists is the versions\<n> directory.
        var observations = Interpret(new[] { FromStore("dlss", 131844) });

        Assert.Equal("2.3.4", observations.Single(o => o.Feature == "SR").Version);
    }

    [Fact]
    public void ARuntimeFromTheGameFolder_IsReportedAsSuch_WithoutSayingTheOverrideFailed()
    {
        // Seeing the game's own DLL does not establish that the game refused the override: the
        // wrong profile, an external change, or an intermediate load state all look the same.
        var observations = Interpret(new[] { FromGame("nvngx_dlss.dll", "310.1.0") });
        var sr = observations.Single(o => o.Feature == "SR");

        Assert.Equal(DlssObservationState.RuntimeObserved, sr.State);
        Assert.False(sr.FromDriverStore);
        string text = DlssVerificationService.Describe(sr);
        Assert.Contains("the game's own files", text);
        Assert.DoesNotContain("fail", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("disallow", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryFeatureGetsAnObservation_EvenWithNothingLoaded()
    {
        // Silence reads as failure, so a feature with nothing to say still says why.
        var observations = Interpret(Array.Empty<DlssProbeService.LoadedRuntime>());

        Assert.Equal(3, observations.Count);
        Assert.All(observations, o => Assert.Equal(DlssObservationState.UnableToVerify, o.State));
        Assert.All(observations, o => Assert.False(string.IsNullOrWhiteSpace(o.Note)));
    }

    [Fact]
    public void AntiCheatRefusal_IsCarriedAsTheReason_NotAsNotInUse()
    {
        // "Enumeration denied" and "this feature is not in use" are different facts, and merging
        // them would turn a protected game into an apparent failure.
        var observations = Interpret(Array.Empty<DlssProbeService.LoadedRuntime>(),
            note: "Module enumeration denied (Access is denied). Expected on protected titles.");

        Assert.All(observations, o => Assert.Contains("denied", o.Note!));
        Assert.All(observations, o => Assert.Equal(DlssObservationState.UnableToVerify, o.State));
        Assert.All(observations, o => Assert.DoesNotContain("not in use", o.Note!));
    }

    [Fact]
    public void FeaturesAreMatchedByStoreFolder_NotByFileName()
    {
        // All three store runtimes share the file name 160_E658700.bin; only the folder separates
        // them. Matching on the name would report the same runtime for every feature.
        var observations = Interpret(new[] { FromStore("dlssg", 20318464) });

        Assert.Equal(DlssObservationState.RuntimeObserved, observations.Single(o => o.Feature == "FG").State);
        Assert.Equal(DlssObservationState.UnableToVerify, observations.Single(o => o.Feature == "SR").State);
        Assert.Equal(DlssObservationState.UnableToVerify, observations.Single(o => o.Feature == "RR").State);
    }

    [Fact]
    public void NvngxDlssDll_DoesNotAlsoMatchRayReconstructionOrFrameGeneration()
    {
        // "nvngx_dlss" is a prefix of "nvngx_dlssd" and "nvngx_dlssg", so a prefix match would
        // report all three from one file.
        var observations = Interpret(new[] { FromGame("nvngx_dlss.dll", "310.1.0") });

        Assert.Equal(DlssObservationState.RuntimeObserved, observations.Single(o => o.Feature == "SR").State);
        Assert.Equal(DlssObservationState.UnableToVerify, observations.Single(o => o.Feature == "RR").State);
        Assert.Equal(DlssObservationState.UnableToVerify, observations.Single(o => o.Feature == "FG").State);
    }


    [Fact]
    public void ADriverChange_MakesAnObservationStale_NotWrong()
    {
        var observation = new DlssObservation { DriverVersion = "616.64" };

        Assert.True(DlssVerificationService.IsStale(observation, "620.10"));
        Assert.False(DlssVerificationService.IsStale(observation, "616.64"));
        Assert.False(DlssVerificationService.IsStale(observation, null));
    }

    [Fact]
    public void AGamePatch_InvalidatesAnObservationOutright()
    {
        var observation = new DlssObservation { GameRuntimeVersion = "310.1.0" };

        Assert.True(DlssVerificationService.IsInvalidated(observation, "310.5.0"));
        Assert.False(DlssVerificationService.IsInvalidated(observation, "310.1.0"));
    }


    [Fact]
    public void ShippedVersionsAreRecordedPerFeature_SoAPatchCanBeDetectedLater()
    {
        var shipped = new[]
        {
            new DlssProbeService.ShippedRuntime("Super Resolution", "nvngx_dlss.dll", "nvngx_dlss.dll", "310.1.0", 1),
            new DlssProbeService.ShippedRuntime("Frame Generation", "nvngx_dlssg.dll", "nvngx_dlssg.dll", "310.2.0", 1)
        };

        var observations = DlssVerificationService.Interpret(
            new[] { FromStore("dlss", 20318464) }, null, "616.64", DateTime.UtcNow, shipped);

        Assert.Equal("310.1.0", observations.Single(o => o.Feature == "SR").GameRuntimeVersion);
        Assert.Equal("310.2.0", observations.Single(o => o.Feature == "FG").GameRuntimeVersion);
        Assert.Null(observations.Single(o => o.Feature == "RR").GameRuntimeVersion);
    }

    [Fact]
    public void ANullProcess_ProducesUnableToVerify_RatherThanThrowing()
    {
        var observations = DlssVerificationService.Observe(null, "616.64");

        Assert.Equal(3, observations.Count);
        Assert.All(observations, o => Assert.Equal(DlssObservationState.UnableToVerify, o.State));
    }

    [Theory]
    [InlineData(@"C:\ProgramData\NVIDIA\NGX\models\dlss\versions\20318464\files\x.bin", "310.9.0")]
    [InlineData(@"C:\ProgramData\NVIDIA\NGX\models\dlss\versions\65644\files\x.bin", "1.0.108")]
    [InlineData(@"C:\Games\Test\nvngx_dlss.dll", null)]
    [InlineData(@"C:\models\versions\notanumber\x.bin", null)]
    public void VersionFromStorePath_ReadsTheVersionDirectory(string path, string? expected)
    {
        Assert.Equal(expected, DlssVerificationService.VersionFromStorePath(path));
    }
}
