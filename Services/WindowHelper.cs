using System.Windows;

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
}
