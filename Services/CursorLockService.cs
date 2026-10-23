using System;
using System.Collections.Generic;
using System.Linq;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>A rectangle in physical screen pixels.</summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    /// <summary>The other rectangle lies inside this one (or is the same).</summary>
    public bool Contains(ScreenRect other) => other.Left >= Left && other.Top >= Top && other.Right <= Right && other.Bottom <= Bottom;

    public override string ToString() => $"({Left},{Top})-({Right},{Bottom})";
}

/// <summary>The foreground window: which process it belongs to, and the window itself.</summary>
public readonly record struct ForegroundWindow(int ProcessId, IntPtr Handle);

/// <summary>The few mouse and window calls the lock needs, so tests can stand in for Windows.</summary>
internal interface ICursorBackend
{
    /// <summary>Raised when the foreground window, the displays or the session changed, and now and then as a safety net. May come from any thread.</summary>
    event Action? Changed;
    /// <summary>Starts, or stops, watching for those changes. Returns at once; never waits on another thread.</summary>
    void Watch(bool on);
    /// <summary>Forgets what was learned about this process id, which Windows may give to another program once it ends.</summary>
    void Forget(int processId);
    /// <summary>The foreground window, or null when there is none or it can't be read (another desktop, a secure prompt).</summary>
    ForegroundWindow? Foreground();
    /// <summary>The process <paramref name="pid"/> is <paramref name="ancestor"/> or was started by it, directly or through others.</summary>
    bool IsSameOrDescendant(int ancestor, int pid);
    /// <summary>The whole monitor the window is on.</summary>
    ScreenRect? MonitorOf(IntPtr window);
    /// <summary>Every attached monitor.</summary>
    IReadOnlyList<ScreenRect> AllMonitors();
    /// <summary>Where the mouse is confined now; the whole desktop when it is free.</summary>
    ScreenRect? CurrentClip();
    /// <summary>Confines the mouse to the rectangle, or frees it for null.</summary>
    bool Clip(ScreenRect? rect);
}

/// <summary>
/// "Keep the mouse on the game's screen": while a game that has the option on is the window in front,
/// the mouse is confined to the monitor that window is on. Alt+Tab, the Windows key, a prompt, or
/// anything else that takes the focus frees it at once, and it comes back when the game does.
///
/// <para>It is never held without the game being in front, so there is nothing to put back after a
/// crash that the focus change would not undo; it is also released when the game ends and when
/// TrayTrigger closes. A confinement the game sets itself (a tighter one, for a camera) is left alone,
/// and only a confinement this service set is released.</para>
/// </summary>
internal sealed class CursorLockService
{
    private static CursorLockService _shared = new(new Win32CursorBackend());

    /// <summary>The service the launcher uses.</summary>
    public static CursorLockService Shared => _shared;

    /// <summary>Test seam: stands another backend in for Windows (null puts the real one back).</summary>
    internal static void UseBackendForTests(ICursorBackend? backend) => _shared = new CursorLockService(backend ?? new Win32CursorBackend());

    private readonly ICursorBackend _backend;
    private readonly Lock _lock = new();
    /// <summary>Orders the start and stop of watching, which is done outside <c>_lock</c>: the backend may call back into <see cref="Evaluate"/> from another thread.</summary>
    private readonly Lock _watchGate = new();
    private readonly Dictionary<string, (string Name, int Pid)> _tracked = new(StringComparer.Ordinal);
    private ScreenRect? _ours;
    private bool _watching;

    internal CursorLockService(ICursorBackend backend)
    {
        _backend = backend;
        _backend.Changed += Evaluate;
    }

    /// <summary>The game has the option on.</summary>
    public static bool Wants(GameEntry game) => game.KeepMouseOnGameScreen;

    /// <summary>The game's process is known: lock the mouse to its screen whenever its window is in front. Never throws.</summary>
    public void Track(GameEntry game, int processId)
    {
        if (!Wants(game)) return;
        try
        {
            lock (_watchGate)
            {
                bool start;
                lock (_lock)
                {
                    _tracked[game.Id] = (game.Name, processId);
                    start = !_watching;
                    _watching = true;
                }
                if (start) _backend.Watch(true);
            }
            Evaluate();
        }
        catch (Exception ex)
        {
            LoggingService.Error("CursorLock", $"Starting to keep the mouse on the screen of '{game.Name}' failed: {ex.Message}", ex);
        }
    }

    /// <summary>The game's session ended: frees the mouse if only this game held it. Safe to call for any game, any number of times.</summary>
    public void Untrack(string gameId)
    {
        try
        {
            int pid;
            lock (_watchGate)
            {
                bool stop = false;
                lock (_lock)
                {
                    if (!_tracked.Remove(gameId, out var gone)) return;
                    pid = gone.Pid;
                    if (_tracked.Count == 0 && _watching)
                    {
                        _watching = false;
                        stop = true;
                    }
                }
                _backend.Forget(pid);
                if (stop) _backend.Watch(false);
            }
            Evaluate();
        }
        catch (Exception ex)
        {
            LoggingService.Error("CursorLock", $"Freeing the mouse after '{gameId}' failed: {ex.Message}", ex);
        }
    }

    /// <summary>TrayTrigger is closing: frees the mouse.</summary>
    public void ReleaseAll()
    {
        string[] ids;
        lock (_lock) ids = _tracked.Keys.ToArray();
        foreach (var id in ids) Untrack(id);
    }

    /// <summary>Looks at what is in front and confines or frees the mouse to match. Called when anything may have changed.</summary>
    internal void Evaluate()
    {
        try
        {
            lock (_lock)
            {
                var target = TargetMonitor();
                if (target is not { } monitor)
                {
                    Release();
                    return;
                }

                var current = _backend.CurrentClip();
                // A tighter confinement is the game's own (a camera lock): leave it, it is not ours to widen.
                if (current is { } held && held != monitor && monitor.Contains(held) && _ours != held)
                {
                    return;
                }
                if (_ours == monitor && current == monitor) return;

                if (_backend.Clip(monitor))
                {
                    if (_ours == null) LoggingService.Verbose("CursorLock", $"Keeping the mouse on the screen at {monitor}.");
                    _ours = monitor;
                }
                else
                {
                    LoggingService.Verbose("CursorLock", "Windows wouldn't confine the mouse.");
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error("CursorLock", $"Checking the mouse failed: {ex.Message}", ex);
            try { lock (_lock) Release(); }
            catch (Exception inner) { LoggingService.Swallowed("CursorLock", inner, "freeing the mouse after a failed check"); }
        }
    }

    /// <summary>The monitor to hold the mouse on, or null: no tracked game's window is in front.</summary>
    private ScreenRect? TargetMonitor()
    {
        if (_tracked.Count == 0) return null;
        if (_backend.Foreground() is not { } front) return null;
        bool game = _tracked.Values.Any(t => _backend.IsSameOrDescendant(t.Pid, front.ProcessId));
        return game ? _backend.MonitorOf(front.Handle) : null;
    }

    /// <summary>Frees the mouse if this service confined it.</summary>
    private void Release()
    {
        if (_ours == null) return;
        _backend.Clip(null);
        LoggingService.Verbose("CursorLock", "Freed the mouse.");
        _ours = null;
    }
}
