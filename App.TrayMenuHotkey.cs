using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using TrayTrigger.Services;

namespace TrayTrigger;

/// <summary>
/// The open-tray-menu hotkey (default Ctrl+Alt+T): the same menu the tray icon's right-click
/// opens, placed against the tray icon as a right-click would place it, with the search box
/// focused by its Opened handler. Pressed again while the menu is open, it closes it.
/// </summary>
public partial class App
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out NativeRect iconLocation);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? windowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowName);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    private void OnTrayMenuHotkeyTriggered()
    {
        if (_isShuttingDown || _trayIcon?.ContextMenu is not { } menu) return;
        if (menu.IsOpen)
        {
            menu.IsOpen = false;
            return;
        }

        PlaceAgainstTrayIcon(menu);
        menu.IsOpen = true;

        // A popup opened by a background app doesn't get the keyboard, so typing would go to
        // whatever was in front. Receiving the hotkey lets TrayTrigger take the foreground, which is
        // what the tray icon's own right-click does too. Activating the popup's window moves WPF's
        // keyboard focus out of the menu, which closes it; a popup keeps its window for a moment
        // after closing, so opening it again at once reuses the window that now has the foreground.
        if (PresentationSource.FromVisual(menu) is HwndSource source)
        {
            SetForegroundWindow(source.Handle);
            if (!menu.IsOpen) menu.IsOpen = true;
        }
    }

    /// <summary>
    /// Anchors the menu to the tray icon: above it with the taskbar at the bottom (below, left or
    /// right of it for a taskbar on the other edges), so it opens where a right-click opens it rather
    /// than wherever the pointer happens to be. An icon kept in the overflow area has no spot of
    /// its own, so the menu anchors to the notification area by the clock instead.
    /// </summary>
    private void PlaceAgainstTrayIcon(ContextMenu menu)
    {
        menu.PlacementTarget = null;
        menu.HorizontalOffset = 0;
        menu.VerticalOffset = 0;

        if (!TryGetTrayAnchor(out NativeRect anchor))
        {
            menu.Placement = PlacementMode.MousePoint;
            return;
        }

        var info = new MonitorInfo { cbSize = (uint)Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(MonitorFromRect(ref anchor, MONITOR_DEFAULTTONEAREST), ref info);
        var work = info.rcWork;

        // The taskbar is on the edge the anchor sits outside the work area on.
        menu.Placement =
            anchor.Top >= work.Bottom ? PlacementMode.Top :
            anchor.Bottom <= work.Top ? PlacementMode.Bottom :
            anchor.Left >= work.Right ? PlacementMode.Left :
            anchor.Right <= work.Left ? PlacementMode.Right :
            PlacementMode.Top;

        // Without a PlacementTarget, WPF takes the rectangle in physical screen pixels, the same
        // units the shell reports the icon in (measured by --test-tray-search at 125 %).
        menu.PlacementRectangle = new Rect(anchor.Left, anchor.Top,
            Math.Max(1, anchor.Right - anchor.Left), Math.Max(1, anchor.Bottom - anchor.Top));
    }

    private bool TryGetTrayAnchor(out NativeRect anchor)
    {
        anchor = default;
        try
        {
            var tray = _trayIcon?.TrayIcon;
            if (tray != null)
            {
                var id = new NotifyIconIdentifier
                {
                    cbSize = (uint)Marshal.SizeOf<NotifyIconIdentifier>(),
                    hWnd = tray.WindowHandle,
                    guidItem = tray.Id,
                };
                if (Shell_NotifyIconGetRect(ref id, out anchor) == 0 && anchor.Right > anchor.Left) return true;
            }

            // In the overflow area, or the shell didn't say: the notification area by the clock.
            IntPtr notify = FindWindowEx(FindWindow("Shell_TrayWnd", null), IntPtr.Zero, "TrayNotifyWnd", null);
            if (notify != IntPtr.Zero && GetWindowRect(notify, out anchor) && anchor.Right > anchor.Left) return true;
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("App", $"Could not find the tray icon's position: {ex.Message}");
        }
        return false;
    }
}
