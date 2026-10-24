using System.Collections.Generic;
using System.Linq;
using TrayTrigger.Models;
using TrayTrigger.Services;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>Per-game microphone: the same behavior as the sound output, on the recording side.</summary>
[Collection(ActivityCurrentCollection.Name)]
public sealed class AudioInputServiceTests : System.IDisposable
{
    private readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TrayTriggerMic_" + System.Guid.NewGuid().ToString("N"));
    private string SnapshotPath => System.IO.Path.Combine(_dir, "audio-input-session.json");
    public void Dispose() { try { System.IO.Directory.Delete(_dir, true); } catch (System.IO.IOException) { } }

    private static GameEntry Game(string id = "a", string mic = "headset-mic") => new() { Id = id, Name = "Game " + id, AudioInputDeviceId = mic };

    private (AudioOutputService Service, FakeAudio Audio) Make()
    {
        var audio = new FakeAudio();
        return (new AudioOutputService(audio, SnapshotPath, AudioFlow.Recording), audio);
    }

    [Fact]
    public void AGameWithNoMicrophone_TouchesNothing()
    {
        var (service, audio) = Make();

        Assert.Equal(AudioApply.NotRequested, service.Begin(Game(mic: "")));
        Assert.Empty(audio.Calls);
    }

    [Fact]
    public void ALaunch_MovesTheMicrophoneThenPutsItBack_AndNeverTheCommunicationsJob()
    {
        var (service, audio) = Make();

        Assert.Equal(AudioApply.Applied, service.Begin(Game()));
        Assert.Equal("headset-mic", audio.MicDefaults[AudioRole.Console]);
        Assert.Equal("headset-mic", audio.MicDefaults[AudioRole.Multimedia]);
        Assert.Equal("desk-mic", audio.MicDefaults[AudioRole.Communications]);

        service.End("a");
        Assert.Equal("desk-mic", audio.MicDefaults[AudioRole.Console]);
        Assert.Equal("desk-mic", audio.MicDefaults[AudioRole.Multimedia]);
        Assert.False(System.IO.File.Exists(SnapshotPath));
    }

    [Fact]
    public void TheSoundOutputIsNotTouched()
    {
        var (service, audio) = Make();

        service.Begin(Game());

        Assert.Equal("speakers", audio.Defaults[AudioRole.Console]);
        Assert.Equal("speakers", audio.Defaults[AudioRole.Multimedia]);
    }

    [Fact]
    public void AMicrophoneThatIsNotConnected_IsLeftAlone_AndTheGameStillLaunches()
    {
        var (service, audio) = Make();
        audio.Mics.RemoveAll(m => m.Id == "headset-mic");

        Assert.Equal(AudioApply.DeviceMissing, service.Begin(Game()));
        Assert.Empty(audio.Calls);
        Assert.Equal("desk-mic", audio.MicDefaults[AudioRole.Console]);
    }

    [Fact]
    public void ThePutBack_LeavesAMicrophoneThePlayerPickedInBetween()
    {
        var (service, audio) = Make();
        service.Begin(Game());
        audio.MicDefaults[AudioRole.Console] = "desk-mic";
        audio.MicDefaults[AudioRole.Multimedia] = "other";

        service.End("a");

        Assert.Equal("desk-mic", audio.MicDefaults[AudioRole.Console]);
        Assert.Equal("other", audio.MicDefaults[AudioRole.Multimedia]);
    }

    [Fact]
    public void AfterACrash_TheNextStartPutsTheMicrophoneBack()
    {
        var (service, audio) = Make();
        service.Begin(Game());
        Assert.True(System.IO.File.Exists(SnapshotPath));

        var restarted = new AudioOutputService(audio, SnapshotPath, AudioFlow.Recording);
        restarted.RecoverIfNeeded();

        Assert.Equal("desk-mic", audio.MicDefaults[AudioRole.Console]);
        Assert.False(System.IO.File.Exists(SnapshotPath));
    }

    [Fact]
    public void TheOutputAndTheMicrophone_AreSetAndPutBackIndependently()
    {
        var audio = new FakeAudio();
        string outputNote = System.IO.Path.Combine(_dir, "audio-session.json");
        var output = new AudioOutputService(audio, outputNote);
        var mic = new AudioOutputService(audio, SnapshotPath, AudioFlow.Recording);
        var game = new GameEntry { Id = "a", Name = "Game a", AudioOutputDeviceId = "headset", AudioInputDeviceId = "headset-mic" };

        Assert.Equal(AudioApply.Applied, output.Begin(game));
        Assert.Equal(AudioApply.Applied, mic.Begin(game));
        Assert.Equal("headset", audio.Defaults[AudioRole.Console]);
        Assert.Equal("headset-mic", audio.MicDefaults[AudioRole.Console]);

        mic.End("a");
        Assert.Equal("headset", audio.Defaults[AudioRole.Console]);
        Assert.Equal("desk-mic", audio.MicDefaults[AudioRole.Console]);
        output.End("a");
        Assert.Equal("speakers", audio.Defaults[AudioRole.Console]);
    }

    [Fact]
    public void TheMessagesSayMicrophone_NotSoundOutput()
    {
        var (service, audio) = Make();
        audio.Mics.RemoveAll(m => m.Id == "headset-mic");
        var activity = new ActivityService(_dir, readOnly: false);
        ActivityService.Current = activity;
        try
        {
            service.Begin(Game());

            var problem = Assert.Single(activity.Entries, e => e.Level == ActivityLevel.Problem);
            Assert.Contains("microphone", problem.Text);
            Assert.DoesNotContain("sound output", problem.Text);
            Assert.Contains("microphone", problem.Detail);
        }
        finally { ActivityService.Current = null; }
    }

    [Fact]
    public void TheMicrophoneList_StartsWithDefault_ThenEachDevice_AndKeepsOneThatIsNotConnected()
    {
        var options = ViewModels.GameEditViewModel.BuildAudioOptions(
            [new("desk-mic", "Desk microphone")], "gone", ViewModels.GameEditViewModel.MissingMicrophoneLabel);

        Assert.Equal([ViewModels.GameEditViewModel.NoAudioChangeLabel, "Desk microphone", "A microphone that isn't connected"], options.Select(o => o.Label).ToList());
    }

    [Fact]
    public void TheMicrophoneAndTheSoundOutput_ReportTheirProblemsInSeparateRows()
    {
        var audio = new FakeAudio();
        audio.Devices.RemoveAll(d => d.Id == "headset");
        audio.Mics.RemoveAll(m => m.Id == "headset-mic");
        var output = new AudioOutputService(audio, System.IO.Path.Combine(_dir, "audio-session.json"));
        var mic = new AudioOutputService(audio, SnapshotPath, AudioFlow.Recording);
        var activity = new ActivityService(_dir, readOnly: false);
        ActivityService.Current = activity;
        try
        {
            var game = new GameEntry { Id = "a", Name = "Game a", AudioOutputDeviceId = "headset", AudioInputDeviceId = "headset-mic" };
            output.Begin(game);
            mic.Begin(game);

            var rows = activity.Entries.Where(e => e.Level == ActivityLevel.Problem).Select(e => e.GroupKey).Distinct().ToList();
            Assert.Equal(2, rows.Count);
        }
        finally { ActivityService.Current = null; }
    }
}
