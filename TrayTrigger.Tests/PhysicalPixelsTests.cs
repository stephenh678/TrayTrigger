using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using TrayTrigger.Services;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>
/// The thread works in physical pixels for the length of a block and goes back after it: the mouse confinement and the
/// overlay windows take their rectangles there, on a process that is only system DPI aware.
/// </summary>
public class PhysicalPixelsTests
{
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    private static int Awareness() => GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());

    /// <summary>
    /// Runs on a thread of its own that is set to system DPI awareness first, as TrayTrigger's window thread is: a thread
    /// with none of its own follows the process, which other tests in the run (opening windows) can change under it.
    /// </summary>
    private static void OnASystemAwareThread(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                SetThreadDpiAwarenessContext(new IntPtr(-2));
                body();
            }
            catch (Exception ex) { error = ex; }
        });
        thread.Start();
        thread.Join();
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Fact]
    public void InsideTheBlock_TheThreadIsPerMonitorAware_AndAfterItIsAsItWas() => OnASystemAwareThread(() =>
    {
        Assert.Equal(1, Awareness());

        using (new PhysicalPixels())
        {
            Assert.Equal(2, Awareness());
        }

        Assert.Equal(1, Awareness());
    });

    [Fact]
    public void Nested_ItStillPutsBackWhatWasThereWhenEachBlockBegan() => OnASystemAwareThread(() =>
    {
        using (new PhysicalPixels())
        {
            using (new PhysicalPixels())
            {
                Assert.Equal(2, Awareness());
            }
            Assert.Equal(2, Awareness());
        }

        Assert.Equal(1, Awareness());
    });

    [Fact]
    public void AMonitorRectangle_IsTheSameSizeAsTheMainMonitorsModeOnTheMainMonitor()
    {
        var all = new Win32CursorBackend().AllMonitors();
        var monitors = new Win32DisplayBackend().Monitors();
        // A machine with no display (a build server) has nothing to compare.
        if (all.Count == 0 || !monitors.Any(m => m.IsPrimary)) return;
        var main = monitors.First(m => m.IsPrimary);

        // The main monitor is never scaled, so the two agree there; the block exists for the others.
        Assert.Contains(all, r => r.Right - r.Left == main.Bounds.Right - main.Bounds.Left && r.Bottom - r.Top == main.Bounds.Bottom - main.Bounds.Top);
    }
}
