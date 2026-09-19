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
/// opens, placed as a right-click on the tray icon places it, with the search box
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

    private void OnTrayMenuHotkeyTriggered()
    {
        if (_isShuttingDown || _trayIcon?.ContextMenu is not { } menu) return;
        if (menu.IsOpen)
        {
            menu.IsOpen = false;
            return;
        }

        // The menu is shared with the tray icon's right-click, so whatever placement this sets is
        // put back when it closes; the right-click must keep opening where it always has.
        var saved = (menu.Placement, menu.PlacementTarget, menu.PlacementRectangle, menu.HorizontalOffset, menu.VerticalOffset, menu.CustomPopupPlacementCallback);
        PlaceAsIfRightClicked(menu);
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

        void Restore(object? sender, RoutedEventArgs e)
        {
            menu.Closed -= Restore;
            (menu.Placement, menu.PlacementTarget, menu.PlacementRectangle, menu.HorizontalOffset, menu.VerticalOffset, menu.CustomPopupPlacementCallback) = saved;
        }
        menu.Closed += Restore;
    }

    /// <summary>
    /// Places the menu where a right-click on the tray icon puts it: a right-click opens it at the
    /// pointer, which is over the icon, so this opens it with a corner at the icon's centre, trying
    /// the corners in the order a pointer placement does (below-right, below-left, above-right,
    /// above-left) and taking the first that fits - above the taskbar, for a taskbar at the bottom. An icon kept in the overflow area
    /// has no spot of its own, so the notification area by the clock stands in; if neither can be
    /// found, the menu opens at the pointer.
    /// </summary>
    private void PlaceAsIfRightClicked(ContextMenu menu)
    {
        if (!TryGetTrayAnchor(out NativeRect anchor))
        {
            menu.Placement = PlacementMode.MousePoint;
            return;
        }

        // Without a PlacementTarget, WPF takes the rectangle in physical screen pixels, the same
        // units the shell reports the icon in (measured by --test-tray-search at 125 %).
        menu.PlacementTarget = null;
        menu.HorizontalOffset = 0;
        menu.VerticalOffset = 0;
        menu.PlacementRectangle = new Rect((anchor.Left + anchor.Right) / 2.0, (anchor.Top + anchor.Bottom) / 2.0, 0, 0);
        menu.Placement = PlacementMode.Custom;
        menu.CustomPopupPlacementCallback = (popupSize, _, _) =>
        [
            new CustomPopupPlacement(new Point(0, 0), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(-popupSize.Width, 0), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(0, -popupSize.Height), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(-popupSize.Width, -popupSize.Height), PopupPrimaryAxis.Horizontal),
        ];
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
