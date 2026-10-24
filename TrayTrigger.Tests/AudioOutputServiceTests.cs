using System.Collections.Generic;
using System.Linq;
using TrayTrigger.Models;
using TrayTrigger.Services;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>Windows' sound devices and which one does each job, kept in memory.</summary>
internal sealed class FakeAudio : IAudioBackend
{
    public List<AudioDevice> Devices = [new("speakers", "Speakers"), new("headset", "Headset"), new("tv", "TV")];
    public Dictionary<AudioRole, string?> Defaults = new()
    {
        [AudioRole.Console] = "speakers", [AudioRole.Multimedia] = "speakers", [AudioRole.Communications] = "speakers",
    };
    public List<AudioDevice> Mics = [new("desk-mic", "Desk microphone"), new("headset-mic", "Headset microphone")];
    public Dictionary<AudioRole, string?> MicDefaults = new()
    {
        [AudioRole.Console] = "desk-mic", [AudioRole.Multimedia] = "desk-mic", [AudioRole.Communications] = "desk-mic",
    };
    public List<string> Calls = [];
    public bool FailSet;
    public AudioRole? FailOnlyRole;
    /// <summary>Windows moves the games job and the music-and-video job together: setting one sets the other.</summary>
    public bool LinkConsoleAndMultimedia;
    public System.Action? DuringSet;

    public IReadOnlyList<AudioDevice> PlaybackDevices() => Devices;
    public string? DefaultId(AudioRole role) => Defaults[role];
    public IReadOnlyList<AudioDevice> RecordingDevices() => Mics;
    public string? DefaultRecordingId(AudioRole role) => MicDefaults[role];

    public bool SetDefault(string id, AudioRole role)
    {
        Calls.Add($"set {id} {role}");
        DuringSet?.Invoke();
        if (FailSet || FailOnlyRole == role) return false;
        // A device id says which side it is on, as it does in Windows.
        var table = Mics.Any(m => m.Id == id) ? MicDefaults : Defaults;
        table[role] = id;
        if (LinkConsoleAndMultimedia && role is AudioRole.Console or AudioRole.Multimedia)
            table[role == AudioRole.Console ? AudioRole.Multimedia : AudioRole.Console] = id;
        return true;
    }
}

/// <summary>Per-game sound output: set before a game, put back after, never left set.</summary>
public sealed class AudioOutputServiceTests : System.IDisposable
{
    private readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TrayTriggerAudio_" + System.Guid.NewGuid().ToString("N"));
    private string SnapshotPath => System.IO.Path.Combine(_dir, "audio-session.json");
    public void Dispose() { try { System.IO.Directory.Delete(_dir, true); } catch (System.IO.IOException) { } }

    private static GameEntry Game(string id = "a", string device = "headset") => new() { Id = id, Name = "Game " + id, AudioOutputDeviceId = device };

    private (AudioOutputService Service, FakeAudio Audio) Make() { var a = new FakeAudio(); return (new AudioOutputService(a, SnapshotPath), a); }

    /// <summary>The two jobs a game's sound moves; the communications job is never touched.</summary>
    private static bool AllRolesAre(FakeAudio a, string id) => a.Defaults[AudioRole.Console] == id && a.Defaults[AudioRole.Multimedia] == id;

    /// <summary>
    /// Windows sets the games job and the music-and-video job together, so putting one back moves the other with it. The result has to be
    /// the same: both back, the communications job never touched.
    /// </summary>
    [Fact]
    public void WhenWindowsLinksTheTwoJobs_BothAreSetAndBothPutBack()
    {
        var (service, audio) = Make();
        audio.LinkConsoleAndMultimedia = true;

        Assert.Equal(AudioApply.Applied, service.Begin(Game()));
        Assert.True(AllRolesAre(audio, "headset"));

        service.End("a");
        Assert.True(AllRolesAre(audio, "speakers"));
        Assert.Equal("speakers", audio.Defaults[AudioRole.Communications]);
        Assert.False(System.IO.File.Exists(SnapshotPath));
    }

    [Fact]
    public void AGameWithNoOutput_TouchesNothing()
    {
        var (service, audio) = Make();

        Assert.Equal(AudioApply.NotRequested, service.Begin(Game(device: "")));
        Assert.Empty(audio.Calls);
    }

    [Fact]
    public void ALaunch_SendsEveryJobToTheDevice_AndEndGivesThemBack()
    {
        var (service, audio) = Make();

        Assert.Equal(AudioApply.Applied, service.Begin(Game()));
        Assert.True(AllRolesAre(audio, "headset"));

        service.End("a");
        Assert.True(AllRolesAre(audio, "speakers"));
        Assert.False(System.IO.File.Exists(SnapshotPath));
    }

    [Fact]
    public void JobsThatHadDifferentDevices_EachGoBackToTheirOwn()
    {
        var (service, audio) = Make();
        audio.Defaults[AudioRole.Multimedia] = "tv";

        service.Begin(Game());
        service.End("a");

        Assert.Equal("speakers", audio.Defaults[AudioRole.Console]);
        Assert.Equal("tv", audio.Defaults[AudioRole.Multimedia]);
    }

    [Fact]
    public void TheCommunicationsJob_IsNeverTouched_SoVoiceChatStaysOnItsHeadset()
    {
        var (service, audio) = Make();
        audio.Defaults[AudioRole.Communications] = "tv";

        service.Begin(Game());
        Assert.Equal("tv", audio.Defaults[AudioRole.Communications]);
        service.End("a");

        Assert.Equal("tv", audio.Defaults[AudioRole.Communications]);
        Assert.DoesNotContain(audio.Calls, c => c.EndsWith("Communications"));
    }

    [Fact]
    public void ADeviceThatAlreadyDoesEveryJob_IsNotTouched()
    {
        var (service, audio) = Make();
        audio.Defaults[AudioRole.Console] = "headset";
        audio.Defaults[AudioRole.Multimedia] = "headset";

        Assert.Equal(AudioApply.AlreadyThere, service.Begin(Game()));
        service.End("a");
        Assert.Empty(audio.Calls);
    }

    [Fact]
    public void ADeviceThatIsNotConnected_ChangesNothing()
    {
        var (service, audio) = Make();

        Assert.Equal(AudioApply.DeviceMissing, service.Begin(Game(device: "gone")));
        Assert.Empty(audio.Calls);
    }

    [Fact]
    public void ADeviceTheUserPickedMeanwhile_IsLeftAlone()
    {
        var (service, audio) = Make();
        service.Begin(Game());
        audio.Defaults[AudioRole.Console] = "tv";

        service.End("a");

        Assert.Equal("tv", audio.Defaults[AudioRole.Console]);
        Assert.Equal("speakers", audio.Defaults[AudioRole.Multimedia]);
    }

    [Fact]
    public void AnOldDeviceThatIsNoLongerConnected_IsNotSetBack()
    {
        var (service, audio) = Make();
        service.Begin(Game());
        audio.Devices.RemoveAll(d => d.Id == "speakers");

        service.End("a");

        Assert.DoesNotContain(audio.Calls.Skip(3), c => c.StartsWith("set speakers"));
        Assert.True(AllRolesAre(audio, "headset"));
    }

    [Fact]
    public void AChangeWindowsRefusesPartway_IsRolledBack_AndLeavesNoNote()
    {
        var (service, audio) = Make();
        audio.FailOnlyRole = AudioRole.Multimedia;

        Assert.Equal(AudioApply.Failed, service.Begin(Game()));

        Assert.True(AllRolesAre(audio, "speakers"));
        Assert.False(System.IO.File.Exists(SnapshotPath));
    }

    [Fact]
    public void AChangeThatCannotBeRolledBackEither_KeepsTheNote()
    {
        var (service, audio) = Make();
        audio.FailOnlyRole = AudioRole.Multimedia;
        // The console job is set, the multimedia job refused; putting the console job back is refused too.
        audio.DuringSet = () => { if (audio.Defaults[AudioRole.Console] == "headset") audio.FailSet = true; };

        Assert.Equal(AudioApply.Failed, service.Begin(Game()));

        Assert.True(System.IO.File.Exists(SnapshotPath));
    }

    [Fact]
    public void AGameStartingWhileAnEarlierPutBackIsStillPending_ChangesNothing_UntilThatIsDone()
    {
        var (service, audio) = Make();
        service.Begin(Game("a", "headset"));
        audio.FailSet = true;
        service.End("a");
        string pending = System.IO.File.ReadAllText(SnapshotPath);
        int calls = audio.Calls.Count;

        Assert.Equal(AudioApply.Failed, service.Begin(Game("b", "tv")));
        Assert.Equal(pending, System.IO.File.ReadAllText(SnapshotPath));
        Assert.True(AllRolesAre(audio, "headset"));

        audio.FailSet = false;
        Assert.Equal(AudioApply.Applied, service.Begin(Game("b", "tv")));
        Assert.True(AllRolesAre(audio, "tv"));
        Assert.True(audio.Calls.Count > calls);
    }

    [Fact]
    public void TheNoteIsWrittenBeforeTheSoundChanges()
    {
        var (service, audio) = Make();
        bool existed = false;
        audio.DuringSet = () => existed |= System.IO.File.Exists(SnapshotPath);

        service.Begin(Game());

        Assert.True(existed);
    }

    [Fact]
    public void TwoGamesAtOnce_OnlyTheFirstSetsTheSound_AndOnlyItPutsItBack()
    {
        var (service, audio) = Make();
        service.Begin(Game("a", "headset"));

        Assert.Equal(AudioApply.Busy, service.Begin(Game("b", "tv")));
        service.End("b");
        Assert.True(AllRolesAre(audio, "headset"));

        service.End("a");
        Assert.True(AllRolesAre(audio, "speakers"));
    }

    [Fact]
    public void AfterACrash_TheNextStartPutsTheSoundBack()
    {
        var (first, audio) = Make();
        first.Begin(Game());

        new AudioOutputService(audio, SnapshotPath).RecoverIfNeeded();

        Assert.True(AllRolesAre(audio, "speakers"));
        Assert.False(System.IO.File.Exists(SnapshotPath));
    }

    [Fact]
    public void AfterACrash_ASoundOutputTheUserChangedSince_IsLeftAlone()
    {
        var (first, audio) = Make();
        first.Begin(Game());
        audio.Defaults[AudioRole.Console] = "tv";
        audio.Defaults[AudioRole.Multimedia] = "tv";

        new AudioOutputService(audio, SnapshotPath).RecoverIfNeeded();

        Assert.True(AllRolesAre(audio, "tv"));
        Assert.False(System.IO.File.Exists(SnapshotPath));
    }

    [Fact]
    public void AFailedPutBack_KeepsTheNote_AndIsTriedAgainAtTheNextStart()
    {
        var (service, audio) = Make();
        service.Begin(Game());
        audio.FailSet = true;
        service.End("a");
        Assert.True(System.IO.File.Exists(SnapshotPath));

        audio.FailSet = false;
        new AudioOutputService(audio, SnapshotPath).RecoverIfNeeded();

        Assert.True(AllRolesAre(audio, "speakers"));
        Assert.False(System.IO.File.Exists(SnapshotPath));
    }

    [Fact]
    public void ClosingTrayTrigger_PutsTheSoundBack_AndEndTwiceDoesNothingTwice()
    {
        var (service, audio) = Make();
        service.Begin(Game());

        service.ReleaseAll();
        int calls = audio.Calls.Count;
        service.End("a");

        Assert.True(AllRolesAre(audio, "speakers"));
        Assert.Equal(calls, audio.Calls.Count);
    }

    [Fact]
    public void AnUnreadableNote_IsDiscarded()
    {
        var (service, audio) = Make();
        System.IO.Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(SnapshotPath, "{ nope");

        service.RecoverIfNeeded();

        Assert.Empty(audio.Calls);
        Assert.False(System.IO.File.Exists(SnapshotPath));
    }
}
