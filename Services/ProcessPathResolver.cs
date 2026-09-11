using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace TrayTrigger.Services;

/// <summary>
/// Cheap, exception-free process identity lookups for the launcher's "is the game running?" and
/// "which process under this install folder is the game?" questions.
///
/// <see cref="Process.MainModule"/> opens the target with PROCESS_QUERY_INFORMATION | PROCESS_VM_READ,
/// which is denied for every elevated, protected, or anti-cheat-guarded process - i.e. for many
/// games and for dozens of system processes on any machine - and throws for each one. The old
/// fallback was a separate WMI query per failing process on every 2-second poll, which is dozens
/// of WMI round-trips per tick exactly while a game is compiling shaders. QueryFullProcessImageName
/// with PROCESS_QUERY_LIMITED_INFORMATION is allowed from a normal-integrity caller against an
/// elevated target and costs microseconds.
/// </summary>
public static partial class ProcessPathResolver
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, [Out] char[] lpExeName, ref uint lpdwSize);

    /// <summary>Full image path of a process, or null if it has exited or is a protected
    /// (PPL/system) process that even limited-information queries cannot open.</summary>
    public static string? GetProcessPath(int pid)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (handle == IntPtr.Zero) return null;

            var buffer = new char[1024];
            uint size = (uint)buffer.Length;
            if (!QueryFullProcessImageName(handle, 0, buffer, ref size) || size == 0) return null;
            return new string(buffer, 0, (int)size);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero) CloseHandle(handle);
        }
    }

    /// <summary>
    /// Executables that live inside a game's install folder but are never the game: crash
    /// reporters, anti-cheat bootstrappers/services, redistributable installers, and launcher-side
    /// helpers. Matching one of these as "the game" either makes a launch think the game is already
    /// running (a lingering crash handler) or ties the session's exit to the wrong process.
    /// Compared case-insensitively against the file name without extension.
    /// </summary>
    private static readonly HashSet<string> KnownHelperProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "UnityCrashHandler64", "UnityCrashHandler32", "CrashReportClient", "CrashSender", "CrashSender1403",
        "crashpad_handler", "CrashHandler", "CrashReporter", "BsSndRpt64", "BsSndRpt",
        "EasyAntiCheat", "EasyAntiCheat_EOS", "EasyAntiCheat_Setup", "EasyAntiCheat_EOS_Setup",
        "BEService", "BEService_x64", "BEService_x86", "BattlEye",
        "vcredist_x64", "vcredist_x86", "VC_redist.x64", "VC_redist.x86", "vc_redist.x64", "vc_redist.x86",
        "dxsetup", "DXSETUP", "oalinst", "dotNetFx40_Full_x86_x64", "ndp48-x86-x64-allos-enu",
        "GalaxyCommunication", "GalaxyClientService", "EALaunchHelper", "EABackgroundService", "EAAntiCheat.GameServiceLauncher",
        "UbisoftGameLauncher", "UbisoftGameLauncher64", "upc", "uplay_bootstrapper", "SteamService", "steamerrorreporter", "steamerrorreporter64",
        "EOSOverlayRenderer-Win64-Shipping", "EOSOverlayRenderer-Win32-Shipping", "EpicOnlineServicesHost", "EpicWebHelper",
        "gameoverlayui", "GameBarPresenceWriter", "nvcontainer", "NVIDIA Share", "RzSynapse", "vconsole2", "vconsole",
        // Windows' own stub that every GDK (Game Pass) package registers as its entry point; it
        // spawns the real game exe and exits.
        "gamelaunchhelper"
    };

    public static bool IsKnownHelperProcess(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        string name = Path.GetFileNameWithoutExtension(exePath);
        if (KnownHelperProcessNames.Contains(name)) return true;
        // Generic patterns: anything self-describing as a crash tool, an installer, or an uninstaller.
        return name.Contains("crash", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("setup", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("installer", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One running process whose image lives under a watched install folder.</summary>
    public readonly record struct ProcessUnderDirectory(int Pid, string Path, bool HasMainWindow, DateTime StartTime);

    /// <summary>
    /// Every non-helper process whose executable lives under <paramref name="normalizedDir"/>
    /// (already normalized with a trailing separator), best candidate first: a process that owns a
    /// visible main window wins over one that doesn't, and among equals the most recently started
    /// wins - a prelauncher that spawned the real game is older than the game it spawned.
    /// The returned entries are plain data; callers re-open a <see cref="Process"/> by PID.
    /// </summary>
    public static List<ProcessUnderDirectory> FindProcessesUnderDirectory(string normalizedDir, bool includeHelpers = false)
    {
        var results = new List<ProcessUnderDirectory>();
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch { return results; }

        foreach (var proc in all)
        {
            try
            {
                string? path = GetProcessPath(proc.Id);
                if (path == null || !path.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase)) continue;
                if (!includeHelpers && IsKnownHelperProcess(path)) continue;

                bool hasWindow = false;
                DateTime start = DateTime.MinValue;
                try { hasWindow = proc.MainWindowHandle != IntPtr.Zero; } catch { }
                try { start = proc.StartTime; } catch { }
                results.Add(new ProcessUnderDirectory(proc.Id, path, hasWindow, start));
            }
            catch
            {
                // Process vanished between enumeration and inspection - not a candidate.
            }
            finally
            {
                proc.Dispose();
            }
        }

        return results
            .OrderByDescending(r => r.HasMainWindow)
            .ThenByDescending(r => r.StartTime)
            .ToList();
    }

    /// <summary>Best candidate under the directory as a live <see cref="Process"/>, or null.</summary>
    public static Process? FindBestProcessUnderDirectory(string normalizedDir)
    {
        foreach (var candidate in FindProcessesUnderDirectory(normalizedDir))
        {
            try
            {
                var proc = Process.GetProcessById(candidate.Pid);
                if (!proc.HasExited) return proc;
                proc.Dispose();
            }
            catch
            {
                // Exited in the meantime; try the next one.
            }
        }
        return null;
    }

    /// <summary>Normalizes a folder for prefix matching: trailing separator, no trailing slashes before it.</summary>
    public static string NormalizeDirectory(string dir) => dir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;

    /// <summary>True if <paramref name="path"/> and <paramref name="expectedPath"/> name the same file.</summary>
    public static bool IsSamePath(string? path, string expectedPath)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(path), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(path, expectedPath, StringComparison.OrdinalIgnoreCase);
        }
    }
}
