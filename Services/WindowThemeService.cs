using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace TrayTrigger.Services;

public static partial class WindowThemeService
{
    // Owner.Left/Owner.Top report stale/incorrect values when the owner window is
    // maximized, which throws off manual centering (dialogs land near the bottom-right
    // instead of centered). PointToScreen reflects the owner's actual on-screen
    // position regardless of window state, so use that instead.
    public static void CenterOverOwner(Window window)
    {
        if (window == null) return;

        if (window.Owner == null)
        {
            // No owner to center over - fall back to centering on the work area.
            window.Left = (SystemParameters.WorkArea.Width - window.ActualWidth) / 2;
            window.Top = (SystemParameters.WorkArea.Height - window.ActualHeight) / 2;
            return;
        }

        Point ownerTopLeftScreen = window.Owner.PointToScreen(new Point(0, 0));
        Matrix transformFromDevice = PresentationSource.FromVisual(window.Owner)?.CompositionTarget?.TransformFromDevice
            ?? Matrix.Identity;
        Point ownerTopLeftDip = transformFromDevice.Transform(ownerTopLeftScreen);

        window.Left = ownerTopLeftDip.X + (window.Owner.ActualWidth - window.ActualWidth) / 2;
        window.Top = ownerTopLeftDip.Y + (window.Owner.ActualHeight - window.ActualHeight) / 2;
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, in int attrValue, int attrSize);

    public static void ApplyDarkTitleBar(Window window)
    {
        if (window == null) return;

        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
        {
            SetDarkAttributes(window);
        }
        else
        {
            window.SourceInitialized += (s, e) => SetDarkAttributes(window);
        }
    }

    private static void SetDarkAttributes(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            int useDarkMode = 1;
            // Windows 11 & Windows 10 20H1+
            DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, in useDarkMode, sizeof(int));
            // Windows 10 1809 - 1909 fallback
            DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, in useDarkMode, sizeof(int));

            // Windows 11 Build 22000+ support custom caption color (COLORREF: 0x00BBGGRR)
            // MainWindow: #121214 -> 0x00141212
            // Dialogs / Popups: #1D1D25 -> R=29 (0x1D), G=29 (0x1D), B=37 (0x25) -> 0x00251D1D
            bool isMain = window is MainWindow;
            int captionColor = isMain ? 0x00141212 : 0x00251D1D;
            DwmSetWindowAttribute(handle, DWMWA_CAPTION_COLOR, in captionColor, sizeof(int));

            int textColor = 0x00FFFFFF;
            DwmSetWindowAttribute(handle, DWMWA_TEXT_COLOR, in textColor, sizeof(int));
        }
        catch (Exception ex)
        {
            LoggingService.Warn("WindowThemeService", $"Failed to set dark title bar: {ex.Message}");
        }
    }
}
