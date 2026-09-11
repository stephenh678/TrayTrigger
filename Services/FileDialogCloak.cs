using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace TrayTrigger.Services;

/// <summary>
/// Hides the Windows common file/folder dialog until its Explorer view has painted.
///
/// The dialog's own chrome (title bar, File name row) comes up dark, but the shell view hosted
/// inside it - the folder tree and file list - does its first erase in the light default window
/// colour and only then takes the dark theme, so a white box shows for a few frames on a cold
/// open. That view belongs to Explorer, not to TrayTrigger, and the dialog HWND only exists
/// inside <c>ShowDialog()</c>, so the DWM-cloak trick used for the app's own windows
/// (<see cref="WindowThemeService"/>) has nothing to attach to up front. This helper listens for
/// the dialog being shown on the UI thread, cloaks it the instant it appears, and uncloaks it a
/// beat after the shell view child exists - or unconditionally at a hard fail-safe - so the
/// dialog can never be left hidden. Process-level dark-mode opt-in (uxtheme SetPreferredAppMode)
/// was measured and does not prevent the flash, which is why this takes the cloak route.
///
/// Scoped on purpose: the hook only lives for the duration of one <see cref="Show"/> call so a
/// MessageBox or any other "#32770" window opened elsewhere is never affected.
/// </summary>
public sealed class FileDialogCloak : IDisposable
{
    /// <summary>How long after the shell view child appears before the dialog is revealed.</summary>
    private const int ViewSettleMs = 120;
    /// <summary>Reveal no matter what after this long - covers a dialog whose view never shows up.</summary>
    private const int FailSafeMs = 450;
    private const int PollMs = 15;

    // Hooked at creation, not at show: the dialog is created hidden at its default size and only
    // shown (and resized to the remembered size) ~170 ms later. Hook callbacks arrive
    // asynchronously, so cloaking on the show event was one frame too late - the small default-
    // size window was presented white before the cloak landed. At creation there is ample time.
    private const uint EVENT_OBJECT_CREATE = 0x8000;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const int OBJID_WINDOW = 0;
    private const uint GA_ROOT = 2;
    private const int DWMWA_CLOAK = 13;

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowName);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, in int attrValue, int attrSize);

    // Kept as a field so the GC cannot collect the delegate while the hook holds its pointer.
    private readonly WinEventDelegate _callback;
    private IntPtr _hook;
    private IntPtr _dialog;
    private Timer? _reveal;
    private int _revealed;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>Runs <c>dialog.ShowDialog()</c> with the dialog cloaked until its view has painted.</summary>
    public static bool? Show(CommonDialog dialog)
    {
        using var guard = new FileDialogCloak();
        return dialog.ShowDialog();
    }

    private FileDialogCloak()
    {
        _callback = OnWinEvent;
        try
        {
            _hook = SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_CREATE, IntPtr.Zero, _callback,
                (uint)Environment.ProcessId, GetCurrentThreadId(), WINEVENT_OUTOFCONTEXT);
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("FileDialogCloak", $"SetWinEventHook failed: {ex.Message}");
            _hook = IntPtr.Zero;
        }
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (_dialog != IntPtr.Zero || idObject != OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;
        if (GetAncestor(hwnd, GA_ROOT) != hwnd) return;

        var cls = new StringBuilder(64);
        GetClassName(hwnd, cls, cls.Capacity);
        if (cls.ToString() != "#32770") return; // the common dialog frame; WPF windows are HwndWrapper[...]

        _dialog = hwnd;
        int cloak = 1;
        int hr = DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, in cloak, sizeof(int));
        if (hr != 0)
        {
            LoggingService.Verbose("FileDialogCloak", $"Cloak failed (0x{hr:X8}); dialog shown normally.");
            _dialog = IntPtr.Zero;
            return;
        }
        LoggingService.Verbose("FileDialogCloak", $"File dialog cloaked at creation, {_clock.ElapsedMilliseconds} ms.");

        // Both clocks start when the dialog is actually shown, since it sits hidden for a while
        // after creation; the fail-safe also covers a dialog that is somehow never shown at all.
        long createdAt = _clock.ElapsedMilliseconds;
        long shownAt = -1;
        long viewSeenAt = -1;
        _reveal = new Timer(_ =>
        {
            long now = _clock.ElapsedMilliseconds;
            if (shownAt < 0 && IsWindowVisible(hwnd)) shownAt = now;
            if (shownAt >= 0 && viewSeenAt < 0 && FindWindowEx(hwnd, IntPtr.Zero, "DirectUIHWND", null) != IntPtr.Zero)
            {
                viewSeenAt = now;
            }
            bool settled = viewSeenAt >= 0 && now - viewSeenAt >= ViewSettleMs;
            bool failSafe = (shownAt >= 0 && now - shownAt >= FailSafeMs) || now - createdAt >= FailSafeMs * 4;
            if (settled || failSafe)
            {
                Reveal(settled ? $"view painted, revealed at {now} ms" : $"fail-safe at {now} ms");
            }
        }, null, PollMs, PollMs);
    }

    private void Reveal(string why)
    {
        if (Interlocked.Exchange(ref _revealed, 1) != 0) return;
        _reveal?.Change(Timeout.Infinite, Timeout.Infinite);
        if (_dialog != IntPtr.Zero && IsWindow(_dialog))
        {
            int cloak = 0;
            DwmSetWindowAttribute(_dialog, DWMWA_CLOAK, in cloak, sizeof(int));
        }
        LoggingService.Verbose("FileDialogCloak", $"File dialog {why}.");
    }

    public void Dispose()
    {
        // The dialog has closed (or never appeared): make sure nothing stays cloaked and stop listening.
        if (_dialog != IntPtr.Zero) Reveal("closed");
        _reveal?.Dispose();
        _reveal = null;
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
