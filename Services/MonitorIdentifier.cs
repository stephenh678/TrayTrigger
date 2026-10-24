using System;
using System.Collections.Generic;

namespace TrayTrigger.Services;

/// <summary>
/// "Identify monitors": a large number on each monitor for a couple of seconds, the same number Windows' own Identify
/// shows (the one in the monitor's device name), with its name under it. The windows can't take the focus or the mouse.
/// </summary>
internal static class MonitorIdentifier
{
    private static readonly TimeSpan ShownFor = TimeSpan.FromMilliseconds(2500);
    private const byte Alpha = 235;

    /// <summary>Shows the numbers now and removes them by itself. Returns at once.</summary>
    public static void Flash(IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0) return;
        WindowThread.Post(() =>
        {
            var windows = new List<IntPtr>();
            foreach (var m in monitors)
            {
                // A box in the middle of the monitor, not the whole of it: enough to read, and the screen stays usable.
                int width = Math.Min(900, m.Bounds.Right - m.Bounds.Left), height = Math.Min(480, m.Bounds.Bottom - m.Bounds.Top);
                int left = m.Bounds.Left + (m.Bounds.Right - m.Bounds.Left - width) / 2;
                int top = m.Bounds.Top + (m.Bounds.Bottom - m.Bounds.Top - height) / 2;
                IntPtr hwnd = NativeOverlay.Create(new ScreenRect(left, top, left + width, top + height), Alpha,
                    m.Number > 0 ? m.Number.ToString() : "?", string.IsNullOrWhiteSpace(m.Name) ? null : m.Name);
                if (hwnd != IntPtr.Zero) windows.Add(hwnd);
            }

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                // No window thread to time it on (not the running app): nothing is left on screen.
                foreach (var hwnd in windows) NativeOverlay.Destroy(hwnd);
                return;
            }
            var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal, dispatcher) { Interval = ShownFor };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                foreach (var hwnd in windows) NativeOverlay.Destroy(hwnd);
            };
            timer.Start();
        });
    }
}
