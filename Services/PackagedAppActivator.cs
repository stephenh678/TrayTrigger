using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TrayTrigger.Services;

/// <summary>
/// Launches a packaged (MSIX/UWP/GDK) app by Application User Model ID. GDK game executables
/// refuse to start without package identity, so <c>Process.Start</c> on the exe is not an
/// option; the shell's <c>IApplicationActivationManager</c> is the documented way in, and it
/// hands back the PID of the process it started. Falls back to
/// <c>explorer.exe shell:AppsFolder\&lt;AUMID&gt;</c> (the same thing Start-menu tiles do, and what
/// Playnite uses) if the COM call fails.
/// </summary>
public static class PackagedAppActivator
{
    /// <returns>The activated process ID, or 0 when the fallback path had to be used (it can't report one).</returns>
    public static uint Activate(string aumid, string? arguments = null)
    {
        try
        {
            var manager = (IApplicationActivationManager)new ApplicationActivationManager();
            manager.ActivateApplication(aumid, arguments ?? string.Empty, ActivateOptions.None, out uint pid);
            return pid;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("PackagedAppActivator", $"IApplicationActivationManager failed for '{aumid}' ({ex.Message}); falling back to shell:AppsFolder.");
        }

        using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{aumid}") { UseShellExecute = true });
        return 0;
    }

    private enum ActivateOptions
    {
        None = 0,
        DesignMode = 0x1,
        NoErrorUI = 0x2,
        NoSplashScreen = 0x4
    }

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        void ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments,
            ActivateOptions options,
            out uint processId);

        void ActivateForFile(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            IntPtr itemArray,
            [MarshalAs(UnmanagedType.LPWStr)] string verb,
            out uint processId);

        void ActivateForProtocol(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            IntPtr itemArray,
            out uint processId);
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager
    {
    }
}
