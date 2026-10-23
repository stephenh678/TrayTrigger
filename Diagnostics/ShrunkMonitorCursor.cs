#if DEBUG
using System;

namespace TrayTrigger.Services;

/// <summary>
/// For the Debug --test-cursor capture only: the real backend with every monitor 200 pixels smaller on each
/// side, so the confinement shows on a single monitor, where the whole monitor is the whole desktop.
/// </summary>
internal sealed class ShrunkMonitorCursor : ICursorBackend
{
    private readonly ICursorBackend _real;

    public ShrunkMonitorCursor(ICursorBackend real)
    {
        _real = real;
        _real.Changed += () => Changed?.Invoke();
    }

    public event Action? Changed;
    public void Watch(bool on) => _real.Watch(on);
    public void Forget(int processId) => _real.Forget(processId);
    public ForegroundWindow? Foreground() => _real.Foreground();
    public bool IsSameOrDescendant(int ancestor, int pid) => _real.IsSameOrDescendant(ancestor, pid);
    public ScreenRect? CurrentClip() => _real.CurrentClip();
    public bool Clip(ScreenRect? rect) => _real.Clip(rect);

    public ScreenRect? MonitorOf(IntPtr window) =>
        _real.MonitorOf(window) is { } m ? new ScreenRect(m.Left + 200, m.Top + 200, m.Right - 200, m.Bottom - 200) : null;
}
#endif
