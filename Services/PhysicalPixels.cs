using System;
using System.Runtime.InteropServices;

namespace TrayTrigger.Services;

/// <summary>
/// For the length of a <c>using</c>: the calling thread works in physical screen pixels, whatever the process's own DPI
/// awareness. TrayTrigger is system DPI aware, so on a PC whose monitors have different scaling Windows hands it
/// scaled ("virtualised") rectangles for every monitor but the main one, and takes them back the same way. The mouse
/// confinement and the overlay windows must be in the same pixels as the monitor modes and places (which Windows
/// always reports in physical pixels), so they take them here. Put back on dispose, on the thread it was set on.
///
/// <para>Only for short blocks of Win32 calls that coordinates go into and out of: nothing in the block may wait on, or
/// pump messages for, WPF.</para>
/// </summary>
internal readonly struct PhysicalPixels : IDisposable
{
    // Pseudo-handles Windows gives for the awareness levels.
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);
    private static readonly IntPtr PerMonitorAware = new(-3);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    private readonly IntPtr _previous;

    public PhysicalPixels()
    {
        try
        {
            // Version 2 needs Windows 10 1703; version 1 does the same job here on 1607. Null means it didn't take.
            _previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
            if (_previous == IntPtr.Zero) _previous = SetThreadDpiAwarenessContext(PerMonitorAware);
        }
        catch (EntryPointNotFoundException)
        {
            // Before Windows 10 1607 there is no per-thread awareness; nothing to change.
            _previous = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_previous != IntPtr.Zero) SetThreadDpiAwarenessContext(_previous);
    }
}
