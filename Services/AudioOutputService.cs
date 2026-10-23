using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>A playback device (speakers, headphones, a monitor's speakers).</summary>
public readonly record struct AudioDevice(string Id, string Name);

/// <summary>The three jobs Windows gives a sound device: games and system sounds, music and video, and calls.</summary>
public enum AudioRole { Console = 0, Multimedia = 1, Communications = 2 }

/// <summary>The few sound calls the service needs, so tests can stand in for Windows.</summary>
internal interface IAudioBackend
{
    /// <summary>Every playback device that is plugged in and enabled.</summary>
    IReadOnlyList<AudioDevice> PlaybackDevices();
    /// <summary>The device now doing a job, or null.</summary>
    string? DefaultId(AudioRole role);
    /// <summary>Makes the device do a job.</summary>
    bool SetDefault(string id, AudioRole role);
}

/// <summary>What was changed, written down before the sound is touched so a crash can be recovered from.</summary>
public sealed class AudioSessionRecord
{
    public string GameId { get; set; } = string.Empty;
    public string GameName { get; set; } = string.Empty;
    /// <summary>The device the game's sound was sent to.</summary>
    public string Device { get; set; } = string.Empty;
    /// <summary>The device that had each job before; empty where nothing was changed.</summary>
    public string PreviousConsole { get; set; } = string.Empty;
    public string PreviousMultimedia { get; set; } = string.Empty;
}

/// <summary>Why a game's sound output was, or wasn't, changed.</summary>
public enum AudioApply { NotRequested, Applied, AlreadyThere, Busy, DeviceMissing, Failed }

/// <summary>
/// Per-game sound output: the playback device a game plays through while it runs, put back when its session ends.
/// Windows has no public call for this; the call used is the one the system's own sound settings use, and it is
/// not documented, so a Windows update can change it. A failure is told to Activity & History and never stops a game.
///
/// <para>A device's job is put back only while it still has the job TrayTrigger gave it: a device the user picked
/// in between is left. A record of what was changed is kept until it is put back, and put back at the next start
/// if TrayTrigger stopped during the game. A per-app output chosen in Windows' volume mixer overrides this for that
/// app.</para>
/// </summary>
internal sealed class AudioOutputService
{
    internal const string SnapshotFileName = "audio-session.json";
    /// <summary>
    /// The jobs a game's sound moves: games and system sounds, and music and video. Not the communications job, so
    /// a voice chat stays on the headset it was on.
    /// </summary>
    private static readonly AudioRole[] Roles = [AudioRole.Console, AudioRole.Multimedia];

    private static AudioOutputService _shared = new(new Win32AudioBackend(), DefaultSnapshotPath());

    /// <summary>The service the launcher uses.</summary>
    public static AudioOutputService Shared => _shared;

    /// <summary>Test seam: stands another backend and record file in for the real ones (null puts them back).</summary>
    internal static void UseBackendForTests(IAudioBackend? backend, string? snapshotPath = null) =>
        _shared = new AudioOutputService(backend ?? new Win32AudioBackend(), snapshotPath ?? DefaultSnapshotPath());

    private static string DefaultSnapshotPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrayTrigger", SnapshotFileName);

    private readonly IAudioBackend _backend;
    private readonly string _snapshotPath;
    private readonly Lock _lock = new();
    private string? _ownerId;
    private string? _ownerName;
    private bool _ending;
    private AudioSessionRecord? _record;

    internal AudioOutputService(IAudioBackend backend, string snapshotPath)
    {
        _backend = backend;
        _snapshotPath = snapshotPath;
    }

    public static bool Wants(GameEntry game) => !string.IsNullOrWhiteSpace(game.AudioOutputDeviceId);

    /// <summary>The playback devices that are plugged in, for Edit Game's list; empty when they can't be read.</summary>
    public IReadOnlyList<AudioDevice> AvailableDevices()
    {
        try { return _backend.PlaybackDevices(); }
        catch (Exception ex)
        {
            LoggingService.Swallowed("Audio", ex, "listing the playback devices; Edit Game shows none");
            return [];
        }
    }

    /// <summary>Before the game launches. Never throws: a game must start whatever the sound does.</summary>
    public AudioApply Begin(GameEntry game)
    {
        if (!Wants(game)) return AudioApply.NotRequested;
        try
        {
            return BeginCore(game);
        }
        catch (Exception ex)
        {
            LoggingService.Error("Audio", $"Setting the sound output for '{game.Name}' failed: {ex.Message}", ex);
            Problem(game.Id, game.Name, $"{game.Name}: the sound output couldn't be set", "The game starts with the sound as it is.");
            return AudioApply.Failed;
        }
    }

    private AudioApply BeginCore(GameEntry game)
    {
        string wanted = game.AudioOutputDeviceId;
        lock (_lock)
        {
            if (_ownerId != null && _ownerId != game.Id)
            {
                Skip(game, $"{_ownerName} already has the sound output set");
                return AudioApply.Busy;
            }
            if (_ownerId == game.Id) return AudioApply.Applied;

            var device = _backend.PlaybackDevices().Where(d => string.Equals(d.Id, wanted, StringComparison.OrdinalIgnoreCase)).Cast<AudioDevice?>().FirstOrDefault();
            if (device is not { } target)
            {
                Skip(game, "the sound output it is set up for isn't connected");
                Problem(game.Id, game.Name, $"{game.Name}: its sound output isn't connected", "The game starts with the sound as it is. Pick a sound output in Edit Game > Display.");
                return AudioApply.DeviceMissing;
            }

            var current = Roles.ToDictionary(r => r, r => _backend.DefaultId(r) ?? string.Empty);
            if (Roles.All(r => string.Equals(current[r], target.Id, StringComparison.OrdinalIgnoreCase)))
            {
                LoggingService.Verbose("Audio", $"'{game.Name}': the sound already plays through {target.Name}.");
                return AudioApply.AlreadyThere;
            }

            // Written before the sound is touched: a change that couldn't be put back after a crash is never made.
            var record = new AudioSessionRecord
            {
                GameId = game.Id,
                GameName = game.Name,
                Device = target.Id,
                PreviousConsole = Differs(current[AudioRole.Console], target.Id) ? current[AudioRole.Console] : string.Empty,
                PreviousMultimedia = Differs(current[AudioRole.Multimedia], target.Id) ? current[AudioRole.Multimedia] : string.Empty,
            };
            if (!WriteSnapshot(record))
            {
                Skip(game, "TrayTrigger couldn't write down what it was about to change");
                Problem(game.Id, game.Name, $"{game.Name}: the sound output wasn't changed", "TrayTrigger couldn't save a note of the change, so it left the sound alone.");
                return AudioApply.Failed;
            }

            bool any = false, failed = false;
            foreach (var role in Roles)
            {
                if (!Differs(current[role], target.Id)) continue;
                if (_backend.SetDefault(target.Id, role)) any = true;
                else failed = true;
            }
            if (failed)
            {
                // Some of it may have taken: put every job back where it was, then give up.
                Restore(record, game.Name);
                DeleteSnapshot();
                Skip(game, "Windows wouldn't change the sound output");
                Problem(game.Id, game.Name, $"{game.Name}: Windows wouldn't change the sound output", "The game starts with the sound as it is.");
                return AudioApply.Failed;
            }
            _ = any;

            _ownerId = game.Id;
            _ownerName = game.Name;
            _record = record;
            LoggingService.Info("Audio", $"Set the sound output for '{game.Name}' to {target.Name}.");
            LaunchRecord.Note(game.Id, LaunchRecord.Profile, $"sound output set to {target.Name}");
            ActivityService.Add(ActivityLevel.Change, $"{game.Name}: sound output set to {target.Name} while it runs", subject: game.Name,
                detail: "It is put back when the game ends.");
            return AudioApply.Applied;
        }
    }

    private static bool Differs(string a, string b) => !string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The game's session ended (or its launch failed). Safe to call for any game, any number of times.</summary>
    public void End(string gameId)
    {
        AudioSessionRecord record;
        string name;
        lock (_lock)
        {
            if (_ownerId == null || _ownerId != gameId || _ending || _record == null) return;
            _ending = true;
            record = _record;
            name = _ownerName ?? gameId;
        }
        try
        {
            if (Restore(record, name))
            {
                DeleteSnapshot();
                LoggingService.Info("Audio", $"Put the sound output back after '{name}'.");
                LaunchRecord.Note(gameId, LaunchRecord.Profile, LaunchRecord.PutBackPrefix + "sound output");
                ActivityService.Add(ActivityLevel.Change, $"Put back the sound output after {name}", subject: name);
            }
            else
            {
                Problem(gameId, name, $"{name}: the sound output couldn't be put back", "TrayTrigger tries again at the next start. Windows' sound settings can also change it by hand.");
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error("Audio", $"Putting the sound output back after '{name}' failed: {ex.Message}", ex);
            Problem(gameId, name, $"{name}: the sound output couldn't be put back", "TrayTrigger tries again at the next start.");
        }
        finally
        {
            lock (_lock)
            {
                _ownerId = _ownerName = null;
                _record = null;
                _ending = false;
            }
        }
    }

    /// <summary>TrayTrigger is closing.</summary>
    public void ReleaseAll()
    {
        string? id;
        lock (_lock) id = _ownerId;
        if (id != null) End(id);
    }

    /// <summary>
    /// Gives each job back to the device that had it, but only while the game's device still has it, and only if the
    /// old device is still there. False if one that should have been put back couldn't be.
    /// </summary>
    private bool Restore(AudioSessionRecord record, string name)
    {
        var present = _backend.PlaybackDevices().Select(d => d.Id).ToList();
        bool ok = true;
        foreach (var (role, previous) in new[]
                 {
                     (AudioRole.Console, record.PreviousConsole),
                     (AudioRole.Multimedia, record.PreviousMultimedia),
                 })
        {
            if (string.IsNullOrEmpty(previous)) continue;
            if (!string.Equals(_backend.DefaultId(role), record.Device, StringComparison.OrdinalIgnoreCase))
            {
                LoggingService.Info("Audio", $"The {role} sound output changed since '{name}' set it; left as it is.");
                continue;
            }
            if (!present.Any(p => string.Equals(p, previous, StringComparison.OrdinalIgnoreCase)))
            {
                LoggingService.Info("Audio", $"The sound output that had the {role} job before '{name}' isn't connected; left as it is.");
                continue;
            }
            if (!_backend.SetDefault(previous, role))
            {
                LoggingService.Warn("Audio", $"Putting the {role} sound output back after '{name}' failed.");
                ok = false;
            }
        }
        return ok;
    }

    /// <summary>Call once at startup: puts the sound output back if TrayTrigger stopped while a game's was set.</summary>
    public void RecoverIfNeeded()
    {
        try
        {
            if (!File.Exists(_snapshotPath)) return;
            AudioSessionRecord? record = null;
            try
            {
                record = JsonSerializer.Deserialize(File.ReadAllText(_snapshotPath), AppJsonContext.Default.AudioSessionRecord);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                LoggingService.Warn("Audio", $"The note of the sound output a game set couldn't be read ({ex.Message}); discarded.");
            }
            if (record == null || string.IsNullOrWhiteSpace(record.Device))
            {
                DeleteSnapshot();
                return;
            }
            if (!Restore(record, record.GameName))
            {
                Problem(record.GameId, record.GameName, $"{record.GameName}: the sound output couldn't be put back", "TrayTrigger tries again at the next start.");
                return;
            }
            DeleteSnapshot();
            LoggingService.Warn("Audio", $"Put the sound output back: TrayTrigger had stopped while '{record.GameName}' was running.");
            ActivityService.Add(ActivityLevel.Change, "Put back the sound output after TrayTrigger stopped during a game", subject: record.GameName);
        }
        catch (Exception ex)
        {
            LoggingService.Error("Audio", $"Checking for a sound output left set failed: {ex.Message}", ex);
        }
    }

    private bool WriteSnapshot(AudioSessionRecord record)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_snapshotPath)!);
            string temp = _snapshotPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(record, AppJsonContext.Default.AudioSessionRecord));
            File.Move(temp, _snapshotPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggingService.Warn("Audio", $"Couldn't write the note of the sound change: {ex.Message}");
            return false;
        }
    }

    private void DeleteSnapshot()
    {
        try { if (File.Exists(_snapshotPath)) File.Delete(_snapshotPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggingService.Warn("Audio", $"Couldn't remove the note of the sound change: {ex.Message}");
        }
    }

    private static void Skip(GameEntry game, string why)
    {
        LoggingService.Info("Audio", $"'{game.Name}': sound output not changed, {why}.");
        LaunchRecord.Note(game.Id, LaunchRecord.Profile, LaunchRecord.SkippedPrefix + $"sound output ({why})");
    }

    private static void Problem(string gameId, string gameName, string text, string detail) =>
        ActivityService.Add(ActivityLevel.Problem, text, subject: gameName, detail: detail, groupKey: $"audio|{gameId}");
}
