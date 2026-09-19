using System;
using System.Windows.Threading;

namespace TrayTrigger.ViewModels;

/// <summary>
/// The time left on an undo toast, kept apart from any timer so it can be tested with plain
/// timestamps. The window runs only while nobody is holding the toast (pointer over it, or
/// keyboard focus in it); holding pauses it and letting go resumes with what was left.
/// </summary>
public sealed class UndoCountdown
{
    /// <summary>How long a removal stays undoable. Was a fixed 6 seconds before 1.5.0.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(10);

    private TimeSpan _remaining;
    private DateTime? _runningSince;

    public bool IsActive { get; private set; }
    public bool IsPaused => IsActive && _runningSince == null;

    public void Start(DateTime now, TimeSpan? window = null)
    {
        _remaining = window ?? DefaultWindow;
        _runningSince = now;
        IsActive = true;
    }

    public void Pause(DateTime now)
    {
        if (!IsActive || _runningSince is not DateTime since) return;
        _remaining -= now - since;
        _runningSince = null;
    }

    public void Resume(DateTime now)
    {
        if (!IsActive || _runningSince != null) return;
        _runningSince = now;
    }

    public TimeSpan Remaining(DateTime now) => !IsActive ? TimeSpan.Zero
        : _runningSince is DateTime since ? _remaining - (now - since)
        : _remaining;

    public bool IsExpired(DateTime now) => IsActive && Remaining(now) <= TimeSpan.Zero;

    public void Stop()
    {
        IsActive = false;
        _runningSince = null;
    }
}

/// <summary>
/// Drives an <see cref="UndoCountdown"/> on the UI thread: calls back once when the window runs
/// out, and pauses while the toast is held. One per toast (Library, Settings).
/// </summary>
public sealed class UndoTimer
{
    private readonly UndoCountdown _countdown = new();
    private DispatcherTimer? _ticker;
    private Action? _onExpired;
    private bool _held;

    /// <summary>Starts a fresh window. Any earlier one is dropped without its callback; callers
    /// finalize the earlier removal themselves first.</summary>
    public void Start(Action onExpired)
    {
        Stop();
        _onExpired = onExpired;
        _countdown.Start(DateTime.UtcNow);
        if (_held) _countdown.Pause(DateTime.UtcNow);
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _ticker.Tick += (s, e) =>
        {
            if (!_countdown.IsExpired(DateTime.UtcNow)) return;
            var callback = _onExpired;
            Stop();
            callback?.Invoke();
        };
        _ticker.Start();
    }

    public void Stop()
    {
        _ticker?.Stop();
        _ticker = null;
        _countdown.Stop();
        _onExpired = null;
    }

    /// <summary>True while the pointer is over the toast or it has keyboard focus.</summary>
    public void Hold(bool held)
    {
        _held = held;
        if (held) _countdown.Pause(DateTime.UtcNow);
        else _countdown.Resume(DateTime.UtcNow);
    }
}
