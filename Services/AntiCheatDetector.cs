using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TrayTrigger.Services;

/// <summary>
/// Whether a game is protected by a kernel anti-cheat, which Suspend refuses: a game that stops
/// answering its anti-cheat's heartbeat is disconnected at best, and can be flagged. Read from what
/// each anti-cheat leaves in the game's folder, plus the anti-cheat services that run only while a
/// protected game does. A miss is possible - a game that embeds its anti-cheat leaves no folder -
/// which is why Suspend also warns that online games disconnect.
/// </summary>
internal static class AntiCheatDetector
{
    /// <summary>Folders an anti-cheat installs into a game, by name.</summary>
    private static readonly (string Folder, string Name)[] MarkerFolders =
    [
        ("EasyAntiCheat", "Easy Anti-Cheat"),
        ("EasyAntiCheat_EOS", "Easy Anti-Cheat"),
        ("BattlEye", "BattlEye"),
        ("EAAntiCheat", "EA Javelin Anti-Cheat"),
        ("GameGuard", "nProtect GameGuard"),
        ("XIGNCODE", "XIGNCODE3"),
        ("AntiCheatExpert", "Anti-Cheat Expert"),
    ];

    /// <summary>Files an anti-cheat puts beside a game's executable or at its root.</summary>
    private static readonly (string File, string Name)[] MarkerFiles =
    [
        ("start_protected_game.exe", "Easy Anti-Cheat"),
        ("EasyAntiCheat_launcher.exe", "Easy Anti-Cheat"),
        ("EasyAntiCheat_EOS_Setup.exe", "Easy Anti-Cheat"),
        ("BEClient_x64.dll", "BattlEye"),
        ("BEService_x64.exe", "BattlEye"),
        ("EAAntiCheat.GameServiceLauncher.exe", "EA Javelin Anti-Cheat"),
        ("mhypbase.dll", "HoYoverse's anti-cheat"),
    ];

    /// <summary>Anti-cheat services that start with a protected game and stop with it.</summary>
    private static readonly (string Process, string Name)[] ServiceProcesses =
    [
        ("EasyAntiCheat", "Easy Anti-Cheat"),
        ("EasyAntiCheat_EOS", "Easy Anti-Cheat"),
        ("BEService", "BattlEye"),
        ("BEService_x64", "BattlEye"),
        ("EAAntiCheat.GameService", "EA Javelin Anti-Cheat"),
    ];

    /// <summary>
    /// The anti-cheat protecting the game, or null when none is found. Looks in
    /// <paramref name="installDir"/> and one level of folders under it, and beside the game's
    /// executable; then for an anti-cheat service started since the game was launched, which is
    /// this game's - not one left from a game played earlier.
    /// </summary>
    internal static string? Find(string? installDir, string? executablePath, DateTime launchedAtUtc,
        Func<IEnumerable<(string Name, DateTime StartedUtc)>>? runningProcesses = null)
    {
        var folders = new List<string>();
        if (!string.IsNullOrWhiteSpace(installDir) && Directory.Exists(installDir)) folders.Add(installDir);
        string? exeDir = SafeDirectoryName(executablePath);
        if (exeDir != null && Directory.Exists(exeDir) && !folders.Contains(exeDir, StringComparer.OrdinalIgnoreCase)) folders.Add(exeDir);

        // One level of subfolders under the install folder: an Unreal game keeps EasyAntiCheat at
        // its root and its executable two folders down, in <Project>\Binaries\Win64.
        foreach (var folder in folders)
        {
            string? found = FindInFolder(folder, lookInSubfolders: folder == installDir);
            if (found != null) return found;
        }

        foreach (var (name, startedUtc) in (runningProcesses ?? RunningProcesses)())
        {
            if (startedUtc < launchedAtUtc.AddSeconds(-10)) continue;
            foreach (var (process, acName) in ServiceProcesses)
            {
                if (string.Equals(name, process, StringComparison.OrdinalIgnoreCase)) return acName;
            }
        }
        return null;
    }

    private static string? FindInFolder(string folder, bool lookInSubfolders)
    {
        try
        {
            foreach (var (file, name) in MarkerFiles)
            {
                if (File.Exists(Path.Combine(folder, file))) return name;
            }
            foreach (var dir in Directory.EnumerateDirectories(folder))
            {
                string leaf = Path.GetFileName(dir);
                foreach (var (marker, name) in MarkerFolders)
                {
                    if (string.Equals(leaf, marker, StringComparison.OrdinalIgnoreCase)) return name;
                }
                if (!lookInSubfolders) continue;
                foreach (var inner in Directory.EnumerateDirectories(dir))
                {
                    string innerLeaf = Path.GetFileName(inner);
                    foreach (var (marker, name) in MarkerFolders)
                    {
                        if (string.Equals(innerLeaf, marker, StringComparison.OrdinalIgnoreCase)) return name;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggingService.Verbose("Suspend", $"Could not look through '{folder}' for an anti-cheat: {ex.Message}");
        }
        return null;
    }

    private static string? SafeDirectoryName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || ProcessLauncherService.IsNonFileProtocolUrl(path)) return null;
        try { return Path.GetDirectoryName(path); }
        catch (ArgumentException) { /* not a path at all; there is no folder to look in */ return null; }
    }

    /// <summary>The anti-cheat services running now, from one pass over the process list.</summary>
    private static List<(string Name, DateTime StartedUtc)> RunningProcesses()
    {
        var found = new List<(string Name, DateTime StartedUtc)>();
        var services = new HashSet<string>(ServiceProcesses.Select(s => s.Process), StringComparer.OrdinalIgnoreCase);
        System.Diagnostics.Process[] all;
        try { all = System.Diagnostics.Process.GetProcesses(); }
        catch (InvalidOperationException) { return found; /* the process list could not be read; nothing to report */ }

        foreach (var process in all)
        {
            using (process)
            {
                string name;
                try { name = process.ProcessName; }
                catch (InvalidOperationException) { continue; /* exited while the list was being read */ }
                if (!services.Contains(name)) continue;

                DateTime started;
                try { started = process.StartTime.ToUniversalTime(); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // A protected service won't say when it started. It is running, and that is
                    // enough to be careful about: count it as started now.
                    started = DateTime.UtcNow;
                }
                found.Add((name, started));
            }
        }
        return found;
    }
}
