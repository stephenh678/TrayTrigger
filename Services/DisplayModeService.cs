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

/// <summary>One attached monitor.</summary>
/// <param name="Device">Windows' name for it right now ("\\.\DISPLAY2"); it can change when cables are swapped.</param>
/// <param name="Id">The monitor's own interface path, which stays with the monitor and its connection; this is what a game remembers.</param>
/// <param name="Name">What the monitor calls itself.</param>
public readonly record struct MonitorInfo(string Device, string Id, string Name, bool IsPrimary, ScreenRect Bounds)
{
    /// <summary>The number Windows' own Identify shows: the one in the device name.</summary>
    public int Number => int.TryParse(new string(Device.Where(char.IsDigit).ToArray()), out int n) ? n : 0;

    /// <summary>"Display 2: LG ULTRAGEAR (2560 × 1440)".</summary>
    public string Label => $"Display {Number}: {(string.IsNullOrWhiteSpace(Name) ? "Monitor" : Name)} ({Bounds.Right - Bounds.Left} × {Bounds.Bottom - Bounds.Top})";
}

/// <summary>Where a monitor sits on the desktop, in pixels; the main display is at 0, 0.</summary>
public readonly record struct MonitorPlacement(string Device, int X, int Y);

/// <summary>Moving the origin of the desktop to another monitor, as Windows needs it to make that monitor the main one.</summary>
internal static class DisplayLayout
{
    /// <summary>
    /// Every placement shifted so <paramref name="newPrimary"/> sits at 0, 0, which is what being the main display
    /// means, with each monitor keeping its place relative to the others. Null when it isn't in the list.
    /// </summary>
    public static IReadOnlyList<MonitorPlacement>? WithPrimary(IReadOnlyList<MonitorPlacement> placements, string newPrimary)
    {
        var target = placements.FirstOrDefault(p => string.Equals(p.Device, newPrimary, StringComparison.OrdinalIgnoreCase));
        if (target.Device == null) return null;
        return placements.Select(p => new MonitorPlacement(p.Device, p.X - target.X, p.Y - target.Y)).ToList();
    }

    /// <summary>
    /// The order and flags for staging a layout: the monitor at 0, 0 first and marked as the main display, then the others.
    /// Windows checks the shape of the desktop as each is staged, and it is the main display's place that the others are given against.
    /// </summary>
    public static IReadOnlyList<(MonitorPlacement Placement, bool SetPrimary)> StagingPlan(IReadOnlyList<MonitorPlacement> placements) =>
        placements.Select(p => (Placement: p, SetPrimary: p.X == 0 && p.Y == 0))
            .OrderByDescending(x => x.SetPrimary)
            .ToList();

    /// <summary>The monitor sitting at 0, 0, or null.</summary>
    public static string? PrimaryOf(IReadOnlyList<MonitorPlacement> placements) =>
        placements.Where(p => p.X == 0 && p.Y == 0).Select(p => p.Device).FirstOrDefault();
}

/// <summary>One placement in the record of what was changed.</summary>
public sealed class DisplayPlacementRecord
{
    public string Device { get; set; } = string.Empty;
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>What was set, written down before the display is touched so a crash can be recovered from.</summary>
public sealed class DisplaySessionRecord
{
    public string GameId { get; set; } = string.Empty;
    public string GameName { get; set; } = string.Empty;
    /// <summary>The monitor the mode was set on; empty when only the main display was changed.</summary>
    public string Device { get; set; } = string.Empty;
    /// <summary>0 when no mode was set.</summary>
    public int Width { get; set; }
    public int Height { get; set; }
    public int RefreshHz { get; set; }
    /// <summary>The monitor made the main display, if one was; empty otherwise.</summary>
    public string MadePrimary { get; set; } = string.Empty;
    /// <summary>The monitor that was the main display before, and where every monitor sat.</summary>
    public string PrimaryBefore { get; set; } = string.Empty;
    public List<DisplayPlacementRecord> LayoutBefore { get; set; } = [];
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
    /// <summary>Every attached monitor, the main display first.</summary>
    IReadOnlyList<MonitorInfo> Monitors();
    /// <summary>Where every attached monitor sits now.</summary>
    IReadOnlyList<MonitorPlacement> Placements();
    /// <summary>
    /// Puts the monitors at these places and makes the one at 0, 0 the main display, as the desktop's own setting
    /// (it lasts until it is changed back). Every attached monitor is in the list.
    /// </summary>
    DisplayChange SetLayout(IReadOnlyList<MonitorPlacement> placements);
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

/// <summary>Why a game's display settings were, or weren't, applied.</summary>
public enum DisplayApply
{
    /// <summary>The game has no display setting that changes anything.</summary>
    NotRequested,
    Applied,
    /// <summary>The display already runs the mode and is already the main display, so there is nothing to change or put back.</summary>
    AlreadyInMode,
    /// <summary>Another game holds the display right now; one at a time.</summary>
    Busy,
    /// <summary>A remote desktop session.</summary>
    Remote,
    NoDisplay,
    /// <summary>The monitor the game was set up for isn't connected.</summary>
    MonitorMissing,
    /// <summary>The display doesn't list the mode, or Windows would refuse it.</summary>
    Unsupported,
    Failed,
}

/// <summary>
/// Per-game display settings: the resolution and refresh rate of the game's monitor (the main display unless
/// another is chosen) and, if asked, making that monitor the main display, while a game runs; all put back when
/// its session ends.
///
/// <para>The mode is set as a temporary one (Windows' CDS_FULLSCREEN): it is never written to the
/// desktop's own setting, so a restart brings the desktop's mode back, and Windows undoes it when this
/// process ends (checked on 2026-10-23 by killing a process that held a mode: the display was back
/// within two seconds, three times in three). That held only once the mode was passed back without the
/// driver's private-data count; an earlier version of the call left the display in the mode after a
/// kill. So a small record of what was set is also written first, and <see cref="RecoverIfNeeded"/>
/// uses it at the next start. It does nothing if the display is already back.</para>
///
/// <para>Making a monitor the main display is different: Windows has no temporary form of it, so the
/// desktop's own layout is changed, and the record is what puts it back, by hand if need be: it holds where
/// every monitor sat. It is put back when the game ends, when TrayTrigger closes and at the next start, and
/// only when the monitor that was made main still is.</para>
///
/// <para>Putting the display mode back always means asking for the desktop's own mode, never re-applying a
/// remembered one: that is always a mode the display supports, even if the monitor was replaced, and a
/// setting the user changed in between is not undone. Only a mode the display itself lists is ever asked for,
/// and Windows is asked to test it first. One game at a time holds the display: a second game that wants a
/// change leaves it alone, and only the game that made the change puts it back.</para>
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
    private bool _ending;
    private DisplaySessionRecord? _record;

    internal DisplayModeService(IDisplayBackend backend, string snapshotPath)
    {
        _backend = backend;
        _snapshotPath = snapshotPath;
    }

    /// <summary>The game asks for a display mode: all three numbers are set.</summary>
    public static bool Wants(GameEntry game) => game.DisplayWidth > 0 && game.DisplayHeight > 0 && game.DisplayRefreshHz > 0;

    /// <summary>The game asks for the monitor to be made the main display, or for a mode.</summary>
    public static bool WantsAnyChange(GameEntry game) => Wants(game) || game.MakeMainDisplay;

    /// <summary>The mode a game asks for, or null.</summary>
    public static DisplayMode? RequestedMode(GameEntry game) =>
        Wants(game) ? new DisplayMode(game.DisplayWidth, game.DisplayHeight, game.DisplayRefreshHz) : null;

    /// <summary>The attached monitors, the main display first; empty in a remote session or when they can't be read.</summary>
    public IReadOnlyList<MonitorInfo> AvailableMonitors()
    {
        try
        {
            if (_backend.IsRemoteSession) return [];
            return _backend.Monitors();
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed("Display", ex, "listing the monitors; Edit Game shows none");
            return [];
        }
    }

    /// <summary>The modes of a monitor (the main display when <paramref name="monitorId"/> is empty), best first (largest, then fastest), for Edit Game's lists; empty when it can't be read.</summary>
    public IReadOnlyList<DisplayMode> AvailableModes(string? monitorId = null)
    {
        try
        {
            if (_backend.IsRemoteSession) return [];
            string? device = string.IsNullOrEmpty(monitorId) ? _backend.PrimaryDevice() : FindDevice(monitorId);
            if (device == null) return [];
            return _backend.Modes(device)
                .OrderByDescending(m => (long)m.Width * m.Height).ThenByDescending(m => m.RefreshHz).ToList();
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed("Display", ex, "listing the display's modes; Edit Game shows none");
            return [];
        }
    }

    private string? FindDevice(string monitorId) =>
        _backend.Monitors().Where(m => string.Equals(m.Id, monitorId, StringComparison.OrdinalIgnoreCase)).Select(m => m.Device).FirstOrDefault();

    /// <summary>Before the game launches. Never throws: a game must start whatever the display does.</summary>
    public DisplayApply Begin(GameEntry game)
    {
        if (!WantsAnyChange(game)) return DisplayApply.NotRequested;
        try
        {
            return BeginCore(game);
        }
        catch (Exception ex)
        {
            LoggingService.Error("Display", $"Setting the display for '{game.Name}' failed: {ex.Message}", ex);
            Problem(game, $"{game.Name}: the display couldn't be set", "The game starts with the display as it is.");
            return DisplayApply.Failed;
        }
    }

    private DisplayApply BeginCore(GameEntry game)
    {
        if (_backend.IsRemoteSession)
        {
            Skip(game, "a remote desktop session can't change the display");
            return DisplayApply.Remote;
        }

        string description;
        lock (_lock)
        {
            if (_ownerId != null && _ownerId != game.Id)
            {
                Skip(game, $"{_ownerName} already has the display set");
                return DisplayApply.Busy;
            }
            if (_ownerId == game.Id) return DisplayApply.Applied;

            // A put-back that failed earlier left its note for the next start. Finish it now, and change nothing while it
            // can't be finished: a new note would take the old one's place, and what it says to put back would be lost.
            if (File.Exists(_snapshotPath))
            {
                RecoverIfNeeded();
                if (File.Exists(_snapshotPath))
                {
                    Skip(game, "an earlier change to the display hasn't been put back yet");
                    return DisplayApply.Failed;
                }
            }

            // The monitor the game is set up for: the main display unless it names another.
            string? device;
            if (string.IsNullOrEmpty(game.DisplayMonitorId))
            {
                device = _backend.PrimaryDevice();
                if (device == null)
                {
                    Skip(game, "no main display was found");
                    return DisplayApply.NoDisplay;
                }
            }
            else
            {
                device = FindDevice(game.DisplayMonitorId);
                if (device == null)
                {
                    Skip(game, "the monitor it is set up for isn't connected");
                    Problem(game, $"{game.Name}: its monitor isn't connected", "The game starts with the display as it is. Pick a monitor in Edit Game > Display.");
                    return DisplayApply.MonitorMissing;
                }
            }

            var wantedMode = RequestedMode(game);
            bool setMode = false;
            if (wantedMode is { } wanted)
            {
                if (_backend.Current(device) == wanted)
                {
                    LoggingService.Verbose("Display", $"'{game.Name}': the display already runs {wanted}.");
                }
                // Only a mode the display lists, and only one Windows says it would accept. Never one made up.
                else if (!_backend.Modes(device).Contains(wanted) || _backend.Test(device, wanted) != DisplayChange.Ok)
                {
                    Skip(game, $"the display doesn't support {wanted}");
                    Problem(game, $"{game.Name}: the display doesn't support {wanted}",
                        "The game starts with the display as it is. Pick another mode in Edit Game > Display.");
                    return DisplayApply.Unsupported;
                }
                else
                {
                    setMode = true;
                }
            }

            // The layout as it is now, and what changing the main display would make of it.
            var before = _backend.Placements();
            string? primaryBefore = DisplayLayout.PrimaryOf(before);
            IReadOnlyList<MonitorPlacement>? newLayout = null;
            if (game.MakeMainDisplay && !string.Equals(device, primaryBefore, StringComparison.OrdinalIgnoreCase))
            {
                // Monitors shown as one picture (duplicated) sit at the same place; none of them can be made the main one.
                if (before.GroupBy(p => (p.X, p.Y)).Any(g => g.Count() > 1))
                {
                    Skip(game, "the monitors show the same picture, so none can be made the main display");
                    Problem(game, $"{game.Name}: the monitors are duplicated", "The game starts with the display as it is. Extend the monitors in Windows Settings > System > Display to use this option.");
                    return DisplayApply.Unsupported;
                }
                newLayout = DisplayLayout.WithPrimary(before, device);
                if (newLayout == null || primaryBefore == null)
                {
                    Skip(game, "the monitors' places couldn't be read");
                    return DisplayApply.Failed;
                }
            }

            if (!setMode && newLayout == null)
            {
                return DisplayApply.AlreadyInMode;
            }

            // Written before the display is touched: a change that couldn't be put back after a crash is never made.
            var record = new DisplaySessionRecord
            {
                GameId = game.Id,
                GameName = game.Name,
                Device = setMode ? device : string.Empty,
                Width = setMode ? wantedMode!.Value.Width : 0,
                Height = setMode ? wantedMode!.Value.Height : 0,
                RefreshHz = setMode ? wantedMode!.Value.RefreshHz : 0,
                MadePrimary = newLayout != null ? device : string.Empty,
                PrimaryBefore = newLayout != null ? primaryBefore! : string.Empty,
                LayoutBefore = newLayout != null ? before.Select(p => new DisplayPlacementRecord { Device = p.Device, X = p.X, Y = p.Y }).ToList() : [],
            };
            if (!WriteSnapshot(record))
            {
                Skip(game, "TrayTrigger couldn't write down what it was about to change");
                Problem(game, $"{game.Name}: the display wasn't changed", "TrayTrigger couldn't save a note of the change, so it left the display alone.");
                return DisplayApply.Failed;
            }

            if (newLayout != null && _backend.SetLayout(newLayout) != DisplayChange.Ok)
            {
                // Some of it may have been staged: put every monitor back where it was.
                if (_backend.SetLayout(before) == DisplayChange.Ok)
                {
                    DeleteSnapshot();
                    Skip(game, "Windows wouldn't make the monitor the main display");
                    Problem(game, $"{game.Name}: Windows wouldn't make its monitor the main display", "The game starts with the display as it is.");
                }
                else
                {
                    // Not even the old layout went back: the note stays, so the next start tries again.
                    Skip(game, "Windows wouldn't make the monitor the main display, or put the layout back");
                    Problem(game, $"{game.Name}: the monitor layout may be wrong",
                        "Windows wouldn't make the monitor the main display and then wouldn't put the layout back. TrayTrigger tries again at the next start; Windows Settings > System > Display sets it by hand.");
                }
                return DisplayApply.Failed;
            }

            if (setMode && _backend.ApplyTemporary(device, wantedMode!.Value) != DisplayChange.Ok)
            {
                if (newLayout != null)
                {
                    // The main display was changed for this game; keep that, and say the mode wasn't.
                    record.Device = string.Empty;
                    record.Width = record.Height = record.RefreshHz = 0;
                    WriteSnapshot(record);
                }
                else
                {
                    DeleteSnapshot();
                }
                Skip(game, $"Windows wouldn't set {wantedMode}");
                Problem(game, $"{game.Name}: Windows wouldn't set the display to {wantedMode}", "The game starts with the display as it is.");
                if (newLayout == null) return DisplayApply.Failed;
                setMode = false;
            }

            _ownerId = game.Id;
            _ownerName = game.Name;
            _record = record;
            description = Describe(device, setMode ? wantedMode : null, newLayout != null);
        }

        LoggingService.Info("Display", $"Set the display for '{game.Name}': {description}.");
        LaunchRecord.Note(game.Id, LaunchRecord.Profile, $"display {description}");
        ActivityService.Add(ActivityLevel.Change, $"{game.Name}: display {description} while it runs", subject: game.Name,
            detail: "It is put back when the game ends.");
        return DisplayApply.Applied;
    }

    private string Describe(string device, DisplayMode? mode, bool madePrimary)
    {
        string monitor = _backend.Monitors().Where(m => m.Device == device).Select(m => m.Number > 0 ? $"Display {m.Number}" : "the monitor").FirstOrDefault() ?? "the monitor";
        var parts = new List<string>();
        if (madePrimary) parts.Add($"{monitor} made the main display");
        if (mode is { } m2) parts.Add($"set to {m2}");
        return string.Join(" and ", parts);
    }

    /// <summary>
    /// The game's session ended (or its launch failed): puts the display back if this game changed it.
    /// Safe to call for any game, any number of times.
    /// </summary>
    public void End(string gameId)
    {
        DisplaySessionRecord record;
        string name;
        lock (_lock)
        {
            if (_ownerId == null || _ownerId != gameId || _ending || _record == null) return;
            // The owner stays until the display is back: a game starting meanwhile is told it is busy
            // instead of changing something the put-back below would then undo.
            _ending = true;
            record = _record;
            name = _ownerName ?? gameId;
        }
        try
        {
            PutBack(gameId, name, record);
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

    /// <summary>TrayTrigger is closing: puts the display back whoever changed it.</summary>
    public void ReleaseAll()
    {
        string? id;
        lock (_lock) id = _ownerId;
        if (id != null) End(id);
    }

    private void PutBack(string gameId, string name, DisplaySessionRecord record)
    {
        try
        {
            bool ok = Restore(record, name, out bool anythingToReport);
            if (!ok)
            {
                // The record stays, so the next start tries again.
                Problem(gameId, name, $"{name}: the display couldn't be put back", "TrayTrigger tries again at the next start. Display settings in Windows can also change it by hand.");
                return;
            }

            DeleteSnapshot();
            if (anythingToReport)
            {
                LoggingService.Info("Display", $"Put the display back after '{name}'.");
                LaunchRecord.Note(gameId, LaunchRecord.Profile, LaunchRecord.PutBackPrefix + "display");
                ActivityService.Add(ActivityLevel.Change, $"Put back the display after {name}", subject: name);
            }
            else
            {
                LaunchRecord.Note(gameId, LaunchRecord.Profile, LaunchRecord.PutBackPrefix + "display (nothing left to put back)");
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error("Display", $"Putting the display back after '{name}' failed: {ex.Message}", ex);
            Problem(gameId, name, $"{name}: the display couldn't be put back", "TrayTrigger tries again at the next start.");
        }
    }

    /// <summary>
    /// Undoes what a record says was changed: the mode first, then the main display. Returns false if something
    /// that should have been put back couldn't be. <paramref name="didSomething"/> is false when there was nothing
    /// left to undo (the monitor is gone, or the display has changed since).
    /// </summary>
    private bool Restore(DisplaySessionRecord record, string name, out bool didSomething)
    {
        didSomething = false;
        bool ok = true;

        if (record.Width > 0 && !string.IsNullOrEmpty(record.Device))
        {
            var set = new DisplayMode(record.Width, record.Height, record.RefreshHz);
            // Only while the display is still in the mode that was set: if it isn't, something else changed it since.
            if (_backend.IsAttached(record.Device) && _backend.Current(record.Device) == set)
            {
                if (_backend.RestoreDefault(record.Device) == DisplayChange.Ok) didSomething = true;
                else
                {
                    LoggingService.Warn("Display", $"Putting the display mode back after '{name}' failed.");
                    ok = false;
                }
            }
            else
            {
                LoggingService.Info("Display", $"The display mode set for '{name}' is no longer in effect; nothing to put back.");
            }
        }

        if (!string.IsNullOrEmpty(record.MadePrimary) && record.LayoutBefore.Count > 0)
        {
            var original = record.LayoutBefore.Select(p => new MonitorPlacement(p.Device, p.X, p.Y)).ToList();
            var now = _backend.Placements();
            bool stillMain = string.Equals(DisplayLayout.PrimaryOf(now), record.MadePrimary, StringComparison.OrdinalIgnoreCase);
            // Every monitor that was there must still be, or the old places mean nothing.
            bool allThere = original.All(p => now.Any(n => string.Equals(n.Device, p.Device, StringComparison.OrdinalIgnoreCase)));
            if (!stillMain || !allThere)
            {
                LoggingService.Info("Display", $"The main display made for '{name}' has changed since, or a monitor is gone; the layout is left as it is.");
            }
            else if (_backend.SetLayout(original) == DisplayChange.Ok)
            {
                didSomething = true;
            }
            else
            {
                LoggingService.Warn("Display", $"Putting the main display back after '{name}' failed.");
                ok = false;
            }
        }

        return ok;
    }

    /// <summary>
    /// Call once at startup: if TrayTrigger stopped while a game's display settings were in effect, puts the display
    /// back. The mode only if the display is still in the mode that was set, the main display only if the monitor
    /// that was made main still is: one that has changed since (a restart, the user, another tool) is left alone.
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
                LoggingService.Warn("Display", $"The note of the display change a game made couldn't be read ({ex.Message}); discarded.");
            }
            if (record == null || (string.IsNullOrWhiteSpace(record.Device) && string.IsNullOrWhiteSpace(record.MadePrimary)))
            {
                DeleteSnapshot();
                return;
            }
            // A note edited by hand, or half written, may have no list.
            record.LayoutBefore ??= [];

            if (!Restore(record, record.GameName, out bool didSomething))
            {
                Problem(record.GameId, record.GameName, $"{record.GameName}: the display couldn't be put back",
                    "TrayTrigger tries again at the next start. Display settings in Windows can also change it by hand.");
                return;
            }

            DeleteSnapshot();
            if (didSomething)
            {
                LoggingService.Warn("Display", $"Put the display back: TrayTrigger had stopped while '{record.GameName}' was running.");
                ActivityService.Add(ActivityLevel.Change, "Put back the display after TrayTrigger stopped during a game",
                    subject: record.GameName,
                    detail: $"The display was still as it had been set for {record.GameName} when TrayTrigger started, so it was put back.");
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error("Display", $"Checking for a display change left in place failed: {ex.Message}", ex);
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
        LoggingService.Info("Display", $"'{game.Name}': display not changed, {why}.");
        LaunchRecord.Note(game.Id, LaunchRecord.Profile, LaunchRecord.SkippedPrefix + $"display ({why})");
    }

    private static void Problem(GameEntry game, string text, string detail) => Problem(game.Id, game.Name, text, detail);

    private static void Problem(string gameId, string gameName, string text, string detail) =>
        ActivityService.Add(ActivityLevel.Problem, text, subject: gameName, detail: detail, groupKey: $"display|{gameId}");
}
