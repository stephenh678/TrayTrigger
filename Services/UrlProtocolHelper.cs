using System;
using System.IO;
using System.Linq;
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
    /// <summary>
    /// URL schemes a library entry is allowed to launch through ShellExecute: the game launchers'
    /// own protocols plus plain web links. Anything else (ms-msdt:, search-ms:, javascript:,
    /// arbitrary third-party handlers) is refused both at import and at launch - a dropped .url
    /// file is untrusted input, and ShellExecute would otherwise hand its argument string to
    /// whatever handler the scheme resolves to.
    /// </summary>
    private static readonly string[] AllowedLaunchSchemes =
    [
        "steam", "com.epicgames.launcher", "origin2", "uplay", "goggalaxy", "http", "https"
    ];

    /// <summary>True if <paramref name="url"/> is an absolute non-file URL whose scheme is in the
    /// launch allow-list. False for filesystem paths and for anything unparseable.</summary>
    public static bool IsAllowedLaunchUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.IsFile) return false;
        return Array.Exists(AllowedLaunchSchemes, s => string.Equals(s, uri.Scheme, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True if the value is a plausible Steam App ID (digits only, non-empty) - the only
    /// shape ever embedded in a cache filename or query string.</summary>
    public static bool IsValidSteamAppId(string? appId)
        => !string.IsNullOrWhiteSpace(appId) && appId.Length <= 12 && appId.All(char.IsDigit);

    public static string? GetHandlerExecutablePath(string scheme, string callerTag = "UrlProtocolHelper")
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey($@"{scheme}\shell\open\command");
            if (key?.GetValue(null) is string command && !string.IsNullOrWhiteSpace(command))
            {
                string? exePath = ParseHandlerCommand(command);
                return exePath != null && File.Exists(exePath) ? exePath : null;
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn(callerTag, $"Error reading '{scheme}' protocol handler: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// The executable in a shell "open" command. Steam, Epic and Ubisoft register
    /// <c>"C:\...\client.exe" "%1"</c>; the EA app registers
    /// <c>C:\Program Files\Electronic Arts\EA Desktop\EA Desktop\EALauncher.exe "%1"</c> with no
    /// quotes around a path full of spaces. Reading only the quoted form reported the EA app as
    /// not installed, which sent EA games down the direct-exe route and printed "no" in the
    /// diagnostic report. Returns null when no .exe can be found in the command.
    /// </summary>
    internal static string? ParseHandlerCommand(string command)
    {
        string trimmed = command.Trim();
        if (trimmed.Length == 0) return null;

        if (trimmed.StartsWith('"'))
        {
            int closingQuote = trimmed.IndexOf('"', 1);
            return closingQuote > 1 ? trimmed[1..closingQuote] : null;
        }

        // Unquoted: the path runs up to the first ".exe" (the arguments that follow it - "%1",
        // -flags - never contain one). Comparison is case-insensitive: some installers write .EXE.
        int exe = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe < 0) return null;
        string path = trimmed[..(exe + 4)];
        return path.Length > 4 ? path : null;
    }

    public static bool IsHandlerInstalled(string scheme, string callerTag = "UrlProtocolHelper") =>
        GetHandlerExecutablePath(scheme, callerTag) != null;
}
