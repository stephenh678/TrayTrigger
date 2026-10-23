using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>One resolution and refresh rate of a display.</summary>
public readonly record struct DisplayMode(int Width, int Height, int RefreshHz)
{
    public override string ToString() => $"{Width} × {Height} @ {RefreshHz} Hz";
}

/// <summary>What was set, written down before the display is touched so a crash can be recovered from.</summary>
public sealed class DisplaySessionRecord
{
    public string GameId { get; set; } = string.Empty;
    public string GameName { get; set; } = string.Empty;
    public string Device { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public int RefreshHz { get; set; }
}

/// <summary>What Windows said to a display change.</summary>
public enum DisplayChange { Ok, BadMode, Failed }

/// <summary>The few display calls the service needs, so tests can stand in for Windows.</summary>
internal interface IDisplayBackend
{
    /// <summary>A remote desktop session: Windows does not change the real display from there.</summary>
    bool IsRemoteSession { get; }
    /// <summary>The device name of the main display ("\\.\DISPLAY1"), or null when there is none.</summary>
    string? PrimaryDevice();
    /// <summary>The display is still attached to the desktop.</summary>
    bool IsAttached(string device);
    /// <summary>Every mode the display lists, each once.</summary>
    IReadOnlyList<DisplayMode> Modes(string device);
    DisplayMode? Current(string device);
    /// <summary>Asks Windows whether it would accept the mode, without changing anything.</summary>
    DisplayChange Test(string device, DisplayMode mode);
    /// <summary>Sets the mode for as long as this process lives: Windows puts the desktop's own mode back when it ends.</summary>
    DisplayChange ApplyTemporary(string device, DisplayMode mode);
    /// <summary>Back to the mode the desktop is set to, which also ends any temporary mode.</summary>
    DisplayChange RestoreDefault(string device);
}

/// <summary>Why a game's display mode was, or wasn't, applied.</summary>
public enum DisplayApply
{
    /// <summary>The game has no display mode set.</summary>
    NotRequested,
    Applied,
    /// <summary>The display already runs the mode, so there is nothing to change or put back.</summary>
    AlreadyInMode,
    /// <summary>Another game holds a display mode right now; one at a time.</summary>
    Busy,
    /// <summary>A remote desktop session.</summary>
    Remote,
    NoDisplay,
    /// <summary>The display doesn't list the mode, or Windows would refuse it.</summary>
    Unsupported,
    Failed,
}

/// <summary>
/// Per-game display mode: the main display's resolution and refresh rate while a game runs, put back
/// when its session ends.
///
/// <para>The mode is set as a temporary one (Windows' CDS_FULLSCREEN): it is never written to the
/// desktop's own setting, so a restart brings the desktop's mode back, and Windows undoes it when this
/// process ends (checked on 2026-10-23 by killing a process that held a mode: the display was back
/// within two seconds, three times in three). That held only once the mode was passed back without the
/// driver's private-data count; an earlier version of the call left the display in the mode after a
/// kill. So a small record of what was set is also written first, and <see cref="RecoverIfNeeded"/>
/// uses it at the next start. It does nothing if the display is already back.</para>
///
/// <para>Putting the display back always means asking for the desktop's own mode, never re-applying a
/// remembered one: that is always a mode the display supports, even if the monitor was replaced, and a
/// setting the user changed in between is not undone. Recovery only does it when the display is still
/// in the mode that was set; if it isn't, someone has changed it since and it is left alone.</para>
///
/// <para>Only a mode the display itself lists is ever asked for, and Windows is asked to test it
/// first. One game at a time holds the display: a second game that wants a different mode leaves it
/// alone, and only the game that set the mode puts it back.</para>
/// </summary>
internal sealed class DisplayModeService
{
    internal const string SnapshotFileName = "display-session.json";

    private static DisplayModeService _shared = new(new Win32DisplayBackend(), DefaultSnapshotPath());

    /// <summary>The service the launcher uses.</summary>
    public static DisplayModeService Shared => _shared;

    /// <summary>Test seam: stands another backend and record file in for the real ones (null puts them back).</summary>
    internal static void UseBackendForTests(IDisplayBackend? backend, string? snapshotPath = null) =>
        _shared = new DisplayModeService(backend ?? new Win32DisplayBackend(), snapshotPath ?? DefaultSnapshotPath());

    /// <summary>On this PC only, with the other state that describes it: not in a backup.</summary>
    private static string DefaultSnapshotPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrayTrigger", SnapshotFileName);

    private readonly IDisplayBackend _backend;
    private readonly string _snapshotPath;
    private readonly Lock _lock = new();
    private string? _ownerId;
    private string? _ownerName;
    private string? _device;
    private DisplayMode _applied;
    private bool _ending;

    internal DisplayModeService(IDisplayBackend backend, string snapshotPath)
    {
        _backend = backend;
        _snapshotPath = snapshotPath;
    }

    /// <summary>The game asks for a display mode: all three numbers are set.</summary>
    public static bool Wants(GameEntry game) => game.DisplayWidth > 0 && game.DisplayHeight > 0 && game.DisplayRefreshHz > 0;

    /// <summary>The mode a game asks for, or null.</summary>
    public static DisplayMode? RequestedMode(GameEntry game) =>
        Wants(game) ? new DisplayMode(game.DisplayWidth, game.DisplayHeight, game.DisplayRefreshHz) : null;

    /// <summary>The main display's modes, best first (largest, then fastest), for Edit Game's list; empty when it can't be read.</summary>
    public IReadOnlyList<DisplayMode> AvailableModes()
    {
        try
        {
            if (_backend.IsRemoteSession || _backend.PrimaryDevice() is not { } device) return [];
            return _backend.Modes(device)
                .OrderByDescending(m => (long)m.Width * m.Height).ThenByDescending(m => m.RefreshHz).ToList();
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed("Display", ex, "listing the display's modes; Edit Game shows none");
            return [];
        }
    }

    /// <summary>Before the game launches. Never throws: a game must start whatever the display does.</summary>
    public DisplayApply Begin(GameEntry game)
    {
        if (RequestedMode(game) is not { } wanted) return DisplayApply.NotRequested;
        try
        {
            return BeginCore(game, wanted);
        }
        catch (Exception ex)
        {
            LoggingService.Error("Display", $"Setting the display for '{game.Name}' failed: {ex.Message}", ex);
            Problem(game, $"{game.Name}: the display mode couldn't be set", "The game starts with the display as it is.");
            return DisplayApply.Failed;
        }
    }

    private DisplayApply BeginCore(GameEntry game, DisplayMode wanted)
    {
        if (_backend.IsRemoteSession)
        {
            Skip(game, "a remote desktop session can't change the display");
            return DisplayApply.Remote;
        }

        lock (_lock)
        {
            if (_ownerId != null && _ownerId != game.Id)
            {
                Skip(game, $"{_ownerName} already has the display set");
                return DisplayApply.Busy;
            }
            if (_ownerId == game.Id) return DisplayApply.Applied;

            if (_backend.PrimaryDevice() is not { } device)
            {
                Skip(game, "no main display was found");
                return DisplayApply.NoDisplay;
            }

            if (_backend.Current(device) == wanted)
            {
                LoggingService.Verbose("Display", $"'{game.Name}': the display already runs {wanted}.");
                return DisplayApply.AlreadyInMode;
            }

            // Only a mode the display lists, and only one Windows says it would accept. Never one made up.
            if (!_backend.Modes(device).Contains(wanted) || _backend.Test(device, wanted) != DisplayChange.Ok)
            {
                Skip(game, $"the display doesn't support {wanted}");
                Problem(game, $"{game.Name}: the display doesn't support {wanted}",
                    "The game starts with the display as it is. Pick another mode in Edit Game > Display.");
                return DisplayApply.Unsupported;
            }

            // Written before the display is touched: a mode that couldn't be put back after a crash is
            // never set.
            if (!WriteSnapshot(new DisplaySessionRecord { GameId = game.Id, GameName = game.Name, Device = device, Width = wanted.Width, Height = wanted.Height, RefreshHz = wanted.RefreshHz }))
            {
                Skip(game, "TrayTrigger couldn't write down what it was about to change");
                Problem(game, $"{game.Name}: the display mode wasn't set", "TrayTrigger couldn't save a note of the change, so it left the display alone.");
                return DisplayApply.Failed;
            }

            if (_backend.ApplyTemporary(device, wanted) != DisplayChange.Ok)
            {
                DeleteSnapshot();
                Skip(game, $"Windows wouldn't set {wanted}");
                Problem(game, $"{game.Name}: Windows wouldn't set the display to {wanted}", "The game starts with the display as it is.");
                return DisplayApply.Failed;
            }

            _ownerId = game.Id;
            _ownerName = game.Name;
            _device = device;
            _applied = wanted;
        }

        LoggingService.Info("Display", $"Set the display to {wanted} for '{game.Name}'.");
        LaunchRecord.Note(game.Id, LaunchRecord.Profile, $"display set to {wanted}");
        ActivityService.Add(ActivityLevel.Change, $"{game.Name}: display set to {wanted} while it runs", subject: game.Name,
            detail: "It is put back when the game ends.");
        return DisplayApply.Applied;
    }

    /// <summary>
    /// The game's session ended (or its launch failed): puts the display back if this game set it.
    /// Safe to call for any game, any number of times.
    /// </summary>
    public void End(string gameId)
    {
        string? device;
        string name;
        DisplayMode applied;
        lock (_lock)
        {
            if (_ownerId == null || _ownerId != gameId || _ending) return;
            // The owner stays until the display is back: a game starting meanwhile is told it is busy
            // instead of setting a mode the put-back below would then undo.
            _ending = true;
            device = _device;
            name = _ownerName ?? gameId;
            applied = _applied;
        }
        try
        {
            PutBack(gameId, name, device, applied);
        }
        finally
        {
            lock (_lock)
            {
                _ownerId = _ownerName = _device = null;
                _ending = false;
            }
        }
    }

    /// <summary>TrayTrigger is closing: puts the display back whoever set it.</summary>
    public void ReleaseAll()
    {
        string? id;
        lock (_lock) id = _ownerId;
        if (id != null) End(id);
    }

    private void PutBack(string gameId, string name, string? device, DisplayMode applied)
    {
        try
        {
            // The display may be gone (unplugged, lid closed): Windows has already moved on, and
            // there is nothing of ours left to put back.
            if (device == null || !_backend.IsAttached(device))
            {
                DeleteSnapshot();
                LoggingService.Info("Display", $"The display set for '{name}' is no longer attached; nothing to put back.");
                LaunchRecord.Note(gameId, LaunchRecord.Profile, LaunchRecord.PutBackPrefix + "display (it was unplugged)");
                return;
            }

            var result = _backend.RestoreDefault(device);
            if (result != DisplayChange.Ok)
            {
                LoggingService.Warn("Display", $"Putting the display back after '{name}' failed ({result}).");
                // The record stays, so the next start tries again.
                Problem(gameId, name, $"{name}: the display couldn't be put back from {applied}",
                    "TrayTrigger tries again at the next start. Display settings in Windows can also change it by hand.");
                return;
            }

            DeleteSnapshot();
            LoggingService.Info("Display", $"Put the display back after '{name}'.");
            LaunchRecord.Note(gameId, LaunchRecord.Profile, LaunchRecord.PutBackPrefix + "display");
            ActivityService.Add(ActivityLevel.Change, $"Put back the display after {name}", subject: name);
        }
        catch (Exception ex)
        {
            LoggingService.Error("Display", $"Putting the display back after '{name}' failed: {ex.Message}", ex);
            Problem(gameId, name, $"{name}: the display couldn't be put back", "TrayTrigger tries again at the next start.");
        }
    }

    /// <summary>
    /// Call once at startup: if TrayTrigger stopped while a game's display mode was set, puts the display
    /// back. Asks for the desktop's own mode, and only when the display is still in the mode that was
    /// set - one that has changed since (a restart, the user, another tool) is left alone.
    /// </summary>
    public void RecoverIfNeeded()
    {
        try
        {
            if (!File.Exists(_snapshotPath)) return;
            DisplaySessionRecord? record = null;
            try
            {
                record = JsonSerializer.Deserialize(File.ReadAllText(_snapshotPath), AppJsonContext.Default.DisplaySessionRecord);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                LoggingService.Warn("Display", $"The note of the display mode a game set couldn't be read ({ex.Message}); discarded.");
            }
            if (record == null || string.IsNullOrWhiteSpace(record.Device))
            {
                DeleteSnapshot();
                return;
            }

            var set = new DisplayMode(record.Width, record.Height, record.RefreshHz);
            bool attached = _backend.IsAttached(record.Device);
            if (!attached || _backend.Current(record.Device) != set)
            {
                LoggingService.Info("Display", $"The display mode set for '{record.GameName}' is no longer in effect; nothing to put back.");
                DeleteSnapshot();
                return;
            }

            var result = _backend.RestoreDefault(record.Device);
            if (result != DisplayChange.Ok)
            {
                LoggingService.Warn("Display", $"Putting the display back after '{record.GameName}' at start failed ({result}).");
                Problem(record.GameId, record.GameName, $"{record.GameName}: the display couldn't be put back from {set}",
                    "TrayTrigger tries again at the next start. Display settings in Windows can also change it by hand.");
                return;
            }

            DeleteSnapshot();
            LoggingService.Warn("Display", $"Put the display back from {set}: TrayTrigger had stopped while '{record.GameName}' was running.");
            ActivityService.Add(ActivityLevel.Change, "Put back the display after TrayTrigger stopped during a game",
                subject: record.GameName,
                detail: $"The display was still at {set} for {record.GameName} when TrayTrigger started, so it was put back to the desktop's own mode.");
        }
        catch (Exception ex)
        {
            LoggingService.Error("Display", $"Checking for a display mode left set failed: {ex.Message}", ex);
        }
    }

    private bool WriteSnapshot(DisplaySessionRecord record)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_snapshotPath)!);
            string temp = _snapshotPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(record, AppJsonContext.Default.DisplaySessionRecord));
            File.Move(temp, _snapshotPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggingService.Warn("Display", $"Couldn't write the note of the display change: {ex.Message}");
            return false;
        }
    }

    private void DeleteSnapshot()
    {
        try { if (File.Exists(_snapshotPath)) File.Delete(_snapshotPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggingService.Warn("Display", $"Couldn't remove the note of the display change: {ex.Message}");
        }
    }

    private static void Skip(GameEntry game, string why)
    {
        LoggingService.Info("Display", $"'{game.Name}': display mode not set, {why}.");
        LaunchRecord.Note(game.Id, LaunchRecord.Profile, LaunchRecord.SkippedPrefix + $"display ({why})");
    }

    private static void Problem(GameEntry game, string text, string detail) => Problem(game.Id, game.Name, text, detail);

    private static void Problem(string gameId, string gameName, string text, string detail) =>
        ActivityService.Add(ActivityLevel.Problem, text, subject: gameName, detail: detail, groupKey: $"display|{gameId}");
}
