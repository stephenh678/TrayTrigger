using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TrayTrigger.Services;

/// <summary>
/// Centralizes the "use the main window as a dialog owner only if it's actually visible"
/// expression that used to be repeated at every dialog call site (a hidden/minimized-to-tray
/// main window is not a usable owner). See L-16.
/// </summary>
public static class WindowHelper
{
    public static Window? ActiveOwner() =>
        Application.Current?.MainWindow is { IsVisible: true } w ? w : null;

    /// <summary>
    /// Takes the minimize and maximize buttons off a modal dialog's title bar while leaving it
    /// resizable. ResizeMode="NoResize" removes them too, but also the resize border, and the edit
    /// dialogs are meant to be stretched. A modal dialog minimized behind its owner leaves the main
    /// window looking frozen. Call from the constructor, before the window is shown.
    /// </summary>
    public static void RemoveMinimizeAndMaximize(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            long style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
            style &= ~(WsMinimizeBox | WsMaximizeBox);
            SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(style));
        };
    }

    private const int GwlStyle = -16;
    private const long WsMinimizeBox = 0x00020000;
    private const long WsMaximizeBox = 0x00010000;

    // TrayTrigger is 64-bit only (win-x64), where the *Ptr entry points exist.
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
