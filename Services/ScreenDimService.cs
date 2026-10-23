using System;
using System.Collections.Generic;
using System.Linq;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>Whatever puts the dimming on screen; a test stands in for it.</summary>
internal interface IDimSurface
{
    /// <summary>Dims exactly these areas, and nothing else. May be called again with other areas.</summary>
    void Show(IReadOnlyList<ScreenRect> areas);
    /// <summary>Takes every dimming away.</summary>
    void Hide();
}

/// <summary>
/// "Dim the other screens": while a game that has the option on is the window in front, every monitor but the one
/// it is on is dimmed, so a second screen doesn't compete with the game. Alt+Tab, the Windows key or anything else
/// that takes the focus lifts it at once, and it is lifted when the game ends and when TrayTrigger closes.
///
/// <para>The dimming is windows drawn over the other monitors, which can't take the focus or the mouse; they belong
/// to this process, so a crash takes them with it. It watches the window in front the same way
/// <see cref="CursorLockService"/> does.</para>
/// </summary>
internal sealed class ScreenDimService
{
    /// <summary>The service the launcher uses.</summary>
    public static ScreenDimService Shared { get; } = new(new Win32CursorBackend(), new OverlayDimSurface());

    private readonly ICursorBackend _backend;
    private readonly IDimSurface _surface;
    private readonly Lock _lock = new();
    private readonly Lock _watchGate = new();
    private readonly Dictionary<string, int> _tracked = new(StringComparer.Ordinal);
    private IReadOnlyList<ScreenRect>? _shown;
    private bool _watching;

    internal ScreenDimService(ICursorBackend backend, IDimSurface surface)
    {
        _backend = backend;
        _surface = surface;
        _backend.Changed += Evaluate;
    }

    /// <summary>The game has the option on.</summary>
    public static bool Wants(GameEntry game) => game.DimOtherScreens;

    /// <summary>The game's process is known: dim the other screens whenever its window is in front. Never throws.</summary>
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
                    _tracked[game.Id] = processId;
                    start = !_watching;
                    _watching = true;
                }
                if (start) _backend.Watch(true);
            }
            Evaluate();
        }
        catch (Exception ex)
        {
            LoggingService.Error("ScreenDim", $"Starting to dim the other screens for '{game.Name}' failed: {ex.Message}", ex);
        }
    }

    /// <summary>The game's session ended. Safe to call for any game, any number of times.</summary>
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
                    if (!_tracked.Remove(gameId, out pid)) return;
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
            LoggingService.Error("ScreenDim", $"Lifting the dimming after '{gameId}' failed: {ex.Message}", ex);
        }
    }

    /// <summary>TrayTrigger is closing.</summary>
    public void ReleaseAll()
    {
        string[] ids;
        lock (_lock) ids = _tracked.Keys.ToArray();
        foreach (var id in ids) Untrack(id);
    }

    internal void Evaluate()
    {
        try
        {
            lock (_lock)
            {
                var areas = AreasToDim();
                if (areas == null || areas.Count == 0)
                {
                    Lift();
                    return;
                }
                if (_shown != null && _shown.SequenceEqual(areas)) return;
                _surface.Show(areas);
                _shown = areas;
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error("ScreenDim", $"Checking the screens failed: {ex.Message}", ex);
            try { lock (_lock) Lift(); }
            catch (Exception inner) { LoggingService.Swallowed("ScreenDim", inner, "lifting the dimming after a failed check"); }
        }
    }

    /// <summary>The monitors to dim, or null when no tracked game's window is in front.</summary>
    private List<ScreenRect>? AreasToDim()
    {
        if (_tracked.Count == 0) return null;
        if (_backend.Foreground() is not { } front) return null;
        if (!_tracked.Values.Any(pid => _backend.IsSameOrDescendant(pid, front.ProcessId))) return null;
        if (_backend.MonitorOf(front.Handle) is not { } gameScreen) return null;
        return _backend.AllMonitors().Where(m => m != gameScreen).ToList();
    }

    private void Lift()
    {
        if (_shown == null) return;
        _surface.Hide();
        _shown = null;
    }
}

/// <summary>The dimming as black windows, one over each monitor to dim.</summary>
internal sealed class OverlayDimSurface : IDimSurface
{
    /// <summary>How dark: 75% black, so the screen can still be read.</summary>
    private const byte Alpha = 191;

    private readonly object _desiredLock = new();
    private IReadOnlyList<ScreenRect> _desired = [];
    private readonly Dictionary<ScreenRect, IntPtr> _windows = new();

    public void Show(IReadOnlyList<ScreenRect> areas)
    {
        lock (_desiredLock) _desired = areas.ToList();
        WindowThread.Post(Apply);
    }

    public void Hide()
    {
        lock (_desiredLock) _desired = [];
        WindowThread.Post(Apply);
    }

    /// <summary>Runs on the window thread, one after another, each time for what was asked last.</summary>
    private void Apply()
    {
        IReadOnlyList<ScreenRect> wanted;
        lock (_desiredLock) wanted = _desired;

        foreach (var gone in _windows.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            NativeOverlay.Destroy(_windows[gone]);
            _windows.Remove(gone);
        }
        foreach (var area in wanted.Where(a => !_windows.ContainsKey(a)))
        {
            IntPtr hwnd = NativeOverlay.Create(area, Alpha);
            if (hwnd != IntPtr.Zero) _windows[area] = hwnd;
            else LoggingService.Warn("ScreenDim", $"Windows wouldn't draw the dimming over {area}.");
        }
    }
}

/// <summary>Runs work on the thread with the message loop, which the overlay windows and the foreground hook belong to.</summary>
internal static class WindowThread
{
    /// <summary>Queues the work and returns at once; without a message loop (a test), runs it here.</summary>
    public static void Post(Action work)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.CheckAccess()) work();
        else dispatcher.BeginInvoke(work);
    }
}
