using System.Runtime.InteropServices;

namespace TrayTrigger.Services;

/// <summary>
/// Reads the Windows "redirection trust" mitigation of this process. When it is on, Windows refuses to follow a
/// junction that a standard user could have created (error 448, "untrusted mount point"), so a tool TrayTrigger
/// starts - which inherits the setting - fails on junctions that work from a normal shell. TrayTrigger never turns
/// it on; it arrives from whatever started TrayTrigger (for example an installer launched by a Chromium host), and
/// Windows offers no way to switch it off in a running process. The fix is a fresh start from the Start menu.
/// </summary>
internal static class ProcessMitigation
{
    private const int ProcessRedirectionTrustPolicy = 16;
    private const uint EnforceRedirectionTrust = 0x1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessMitigationPolicy(IntPtr hProcess, int policy, out uint buffer, UIntPtr length);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>True when this process enforces redirection trust. False when it does not, or when Windows can't say (older builds).</summary>
    internal static bool IsRedirectionTrustEnforced()
    {
        try
        {
            return GetProcessMitigationPolicy(GetCurrentProcess(), ProcessRedirectionTrustPolicy, out uint flags, (UIntPtr)sizeof(uint))
                   && (flags & EnforceRedirectionTrust) != 0;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            LoggingService.Swallowed("App", ex, "reading the redirection trust mitigation");
            return false;
        }
    }
}
