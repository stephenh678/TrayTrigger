using System;
using System.IO;
using Microsoft.Win32;

namespace TrayTrigger.Services;

/// <summary>
/// Checks whether a Windows URL protocol handler (e.g. "origin2", "com.epicgames.launcher",
/// "uplay") is registered and its target exe still exists - the shared "is this platform's client
/// installed" check for every client-launch platform integration. Extracted once EA, Epic, and
/// Ubisoft all needed the identical check (see docs/adding-a-platform-integration.md).
/// </summary>
public static class UrlProtocolHelper
{
    public static string? GetHandlerExecutablePath(string scheme, string callerTag = "UrlProtocolHelper")
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey($@"{scheme}\shell\open\command");
            if (key?.GetValue(null) is string command && !string.IsNullOrWhiteSpace(command))
            {
                // Expected form: "<exe path>" "%1" - take the quoted exe path.
                string trimmed = command.Trim();
                if (trimmed.StartsWith('"'))
                {
                    int closingQuote = trimmed.IndexOf('"', 1);
                    if (closingQuote > 1)
                    {
                        string exePath = trimmed[1..closingQuote];
                        return File.Exists(exePath) ? exePath : null;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn(callerTag, $"Error reading '{scheme}' protocol handler: {ex.Message}");
        }
        return null;
    }

    public static bool IsHandlerInstalled(string scheme, string callerTag = "UrlProtocolHelper") =>
        GetHandlerExecutablePath(scheme, callerTag) != null;
}
