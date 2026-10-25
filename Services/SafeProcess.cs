using System;
using System.Diagnostics;

namespace TrayTrigger.Services;

/// <summary>
/// Questions about a process that may have exited. A <see cref="Process"/> throws when it is asked about
/// something after it has gone, which is an answer rather than a failure; these return a value that says so.
/// </summary>
internal static class SafeProcess
{
    /// <summary>The process id, or -1 when the process has already exited.</summary>
    public static int Id(Process process)
    {
        try { return process.Id; } catch { /* the process has already exited */ return -1; }
    }

    /// <summary>"name (PID n)" for a log line, or "the game process" when it has already exited.</summary>
    public static string Describe(Process process)
    {
        try { return $"{process.ProcessName} (PID {process.Id})"; } catch { /* the process has already exited */ return "the game process"; }
    }

    /// <summary>The main window, or <see cref="IntPtr.Zero"/> when there is none or the process has already exited.</summary>
    public static IntPtr MainWindow(Process? process)
    {
        try { return process?.MainWindowHandle ?? IntPtr.Zero; } catch { /* the process has already exited */ return IntPtr.Zero; }
    }

    /// <summary>When the process started, or <see cref="DateTime.MinValue"/> when that can't be read (it has already exited).</summary>
    public static DateTime StartTime(Process process)
    {
        try { return process.StartTime; } catch { /* the process has already exited */ return DateTime.MinValue; }
    }
}
