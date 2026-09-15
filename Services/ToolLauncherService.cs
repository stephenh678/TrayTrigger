using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

public enum ToolLaunchOutcome
{
    /// <summary>A new process was started.</summary>
    Started,
    /// <summary>The tool was already running and its window was brought to the front.</summary>
    ActivatedRunning,
    /// <summary>The tool was already running with no window to show (a tray-only tool).</summary>
    AlreadyRunningNoWindow,
    /// <summary>The target file isn't there.</summary>
    Missing,
    /// <summary>The Windows admin prompt was declined. Logged only; nothing is shown.</summary>
    Cancelled,
    /// <summary>Anything else; <see cref="ToolLaunchResult.Error"/> says why.</summary>
    Failed
}

public readonly record struct ToolLaunchResult(ToolLaunchOutcome Outcome, string? Error = null);

/// <summary>
/// Starts a tool. Unlike <see cref="ProcessLauncherService"/> there is no session: no performance
/// profile, no scripts, no playtime. Elevation is left to Windows - with Run as administrator ticked
/// the "runas" verb asks for it, and a program whose manifest requires admin gets the prompt either way.
/// </summary>
public class ToolLauncherService
{
    /// <summary>ERROR_CANCELLED: what ShellExecute reports when the UAC prompt is declined.</summary>
    internal const int ErrorCancelled = 1223;

    /// <summary>
    /// The copy started for each tool that has arguments, by tool id. Two tools can share a program
    /// (cmd.exe with different scripts), so the program's name can't say which tool a running copy
    /// belongs to. Holding the process keeps its id from being reused while it is tracked.
    /// </summary>
    private readonly Dictionary<string, StartedCopy> _startedCopies = new(StringComparer.Ordinal);
    private readonly Lock _startedCopiesLock = new();

    /// <param name="Launch">The program and arguments the copy was started with.</param>
    private sealed record StartedCopy(Process Process, ToolEntry Launch);

    public ToolLaunchResult Launch(ToolEntry tool)
    {
        if (ToolCatalog.IsStoreApp(tool)) return LaunchStoreApp(tool);

        if (string.IsNullOrWhiteSpace(tool.TargetPath) || !File.Exists(tool.TargetPath))
        {
            LoggingService.Warn("ToolLauncher", $"Cannot launch tool '{tool.Name}': program missing at '{tool.TargetPath}'.");
            return new ToolLaunchResult(ToolLaunchOutcome.Missing);
        }

        // tools.json is user-editable: never hand anything but a local exe to the shell.
        string? problem = ToolCatalog.ValidateTarget(tool.TargetPath);
        if (problem != null)
        {
            LoggingService.Warn("ToolLauncher", $"Refused to launch tool '{tool.Name}': {problem} ('{tool.TargetPath}').");
            return new ToolLaunchResult(ToolLaunchOutcome.Failed, $"\"{tool.Name}\" can't be launched because {problem}.");
        }

        bool anyRunningCopy = ChecksAnyRunningCopy(tool);
        var running = anyRunningCopy ? TryActivateRunning(tool) : TryActivateStartedCopy(tool);
        if (running != null) return running.Value;

        try
        {
            var startInfo = BuildStartInfo(tool);
            LoggingService.Verbose("ToolLauncher", $"Starting tool '{tool.Name}': '{startInfo.FileName}', Args='{startInfo.Arguments}', WorkDir='{startInfo.WorkingDirectory}', RunAsAdmin={tool.RunAsAdmin}.");
            var process = Process.Start(startInfo);
            LoggingService.Info("ToolLauncher", $"Started tool '{tool.Name}'{(process != null ? $" (PID {process.Id})" : string.Empty)}.");
            if (process != null && !anyRunningCopy)
            {
                Track(tool, process);
            }
            else
            {
                process?.Dispose();
            }
            return new ToolLaunchResult(ToolLaunchOutcome.Started);
        }
        catch (Exception ex)
        {
            var outcome = ClassifyStartFailure(ex);
            if (outcome == ToolLaunchOutcome.Cancelled)
            {
                LoggingService.Info("ToolLauncher", $"Tool '{tool.Name}' was not started: the administrator prompt was declined.");
                return new ToolLaunchResult(ToolLaunchOutcome.Cancelled);
            }
            LoggingService.Error("ToolLauncher", $"Failed to start tool '{tool.Name}': {ex.Message}", ex);
            return new ToolLaunchResult(ToolLaunchOutcome.Failed, ex.Message);
        }
    }

    /// <summary>
    /// A Store app: activated by its app ID, the way its Start menu tile starts it. Windows brings a
    /// running copy forward itself, so there's no running-copy check, and no elevation or working
    /// folder to apply.
    /// </summary>
    private static ToolLaunchResult LaunchStoreApp(ToolEntry tool)
    {
        if (CheckStoreApp(tool, PackagedApps.IsInstalled) is { } refused) return refused;

        try
        {
            string appId = tool.AppId.Trim();
            LoggingService.Verbose("ToolLauncher", $"Activating Store app tool '{tool.Name}': '{appId}', Args='{tool.Arguments}'.");
            uint pid = PackagedAppActivator.Activate(appId, tool.Arguments);
            LoggingService.Info("ToolLauncher", $"Started Store app tool '{tool.Name}'{(pid != 0 ? $" (PID {pid})" : string.Empty)}.");
            return new ToolLaunchResult(ToolLaunchOutcome.Started);
        }
        catch (Exception ex)
        {
            LoggingService.Error("ToolLauncher", $"Failed to start Store app tool '{tool.Name}': {ex.Message}", ex);
            return new ToolLaunchResult(ToolLaunchOutcome.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Why a Store app tool can't be started, or null when it can. tools.json is user-editable, and the
    /// app ID can reach explorer.exe's command line, so a malformed one is refused before anything runs.
    /// </summary>
    internal static ToolLaunchResult? CheckStoreApp(ToolEntry tool, Func<string, bool> isInstalled)
    {
        string? problem = ToolCatalog.ValidateAppId(tool.AppId);
        if (problem != null)
        {
            LoggingService.Warn("ToolLauncher", $"Refused to launch tool '{tool.Name}': {problem} ('{tool.AppId}').");
            return new ToolLaunchResult(ToolLaunchOutcome.Failed, $"\"{tool.Name}\" can't be launched because {problem}.");
        }
        if (!isInstalled(tool.AppId.Trim()))
        {
            LoggingService.Warn("ToolLauncher", $"Cannot launch tool '{tool.Name}': Store app '{tool.AppId}' isn't installed for this user.");
            return new ToolLaunchResult(ToolLaunchOutcome.Missing);
        }
        return null;
    }

    /// <summary>A declined UAC prompt is a cancel, not a failure.</summary>
    internal static ToolLaunchOutcome ClassifyStartFailure(Exception ex) =>
        ex is Win32Exception { NativeErrorCode: ErrorCancelled } ? ToolLaunchOutcome.Cancelled : ToolLaunchOutcome.Failed;

    /// <summary>The tool's working folder when it exists, otherwise the program's own folder.</summary>
    internal static string ResolveWorkingDirectory(ToolEntry tool, Func<string, bool>? directoryExists = null)
    {
        directoryExists ??= Directory.Exists;
        return !string.IsNullOrWhiteSpace(tool.WorkingDirectory) && directoryExists(tool.WorkingDirectory)
            ? tool.WorkingDirectory
            : (Path.GetDirectoryName(tool.TargetPath) ?? string.Empty);
    }

    internal static ProcessStartInfo BuildStartInfo(ToolEntry tool, Func<string, bool>? directoryExists = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = tool.TargetPath,
            Arguments = tool.Arguments ?? string.Empty,
            WorkingDirectory = ResolveWorkingDirectory(tool, directoryExists),
            UseShellExecute = true
        };
        if (tool.RunAsAdmin)
        {
            startInfo.Verb = "runas";
        }
        return startInfo;
    }

    /// <summary>
    /// Which running copy counts as this tool already running. With no arguments the tool is just its
    /// program, so any running copy of the exe does. With arguments only the copy TrayTrigger started
    /// for this tool does, so two tools sharing a program each start their own.
    /// </summary>
    internal static bool ChecksAnyRunningCopy(ToolEntry tool) => string.IsNullOrWhiteSpace(tool.Arguments);

    /// <summary>
    /// For a tool with arguments: when the copy started for it is still running with the same program
    /// and arguments, brings its window forward instead of starting another. Null when there is none.
    /// </summary>
    private ToolLaunchResult? TryActivateStartedCopy(ToolEntry tool)
    {
        StartedCopy? copy;
        lock (_startedCopiesLock)
        {
            PruneExitedCopies();
            if (!_startedCopies.TryGetValue(tool.Id, out copy)) return null;
            if (!ToolCatalog.IsSameLaunch(copy.Launch, tool))
            {
                // Edited since it was started: that copy runs the old program or arguments.
                _startedCopies.Remove(tool.Id);
                copy.Process.Dispose();
                return null;
            }
        }

        try
        {
            copy.Process.Refresh();
            IntPtr window = copy.Process.MainWindowHandle;
            if (window == IntPtr.Zero)
            {
                LoggingService.Info("ToolLauncher", $"Tool '{tool.Name}' is already running with no window (PID {copy.Process.Id}).");
                return new ToolLaunchResult(ToolLaunchOutcome.AlreadyRunningNoWindow);
            }

            ProcessLauncherService.ActivateWindow(window);
            LoggingService.Info("ToolLauncher", $"Tool '{tool.Name}' already running (PID {copy.Process.Id}); activated its window.");
            return new ToolLaunchResult(ToolLaunchOutcome.ActivatedRunning);
        }
        catch (Exception ex)
        {
            // It closed in the meantime, or can't be read: start a fresh copy.
            LoggingService.Verbose("ToolLauncher", $"Could not check the copy started for '{tool.Name}': {ex.Message}");
            return null;
        }
    }

    private void Track(ToolEntry tool, Process process)
    {
        var launch = new ToolEntry { Id = tool.Id, TargetPath = tool.TargetPath, Arguments = tool.Arguments };
        lock (_startedCopiesLock)
        {
            PruneExitedCopies();
            if (_startedCopies.Remove(tool.Id, out var previous)) previous.Process.Dispose();
            _startedCopies[tool.Id] = new StartedCopy(process, launch);
        }
    }

    /// <summary>Forgets copies that have closed or can no longer be checked. Called under the lock.</summary>
    private void PruneExitedCopies()
    {
        foreach (var (id, copy) in _startedCopies.ToList())
        {
            bool exited;
            try
            {
                exited = copy.Process.HasExited;
            }
            catch (Exception)
            {
                exited = true;
            }
            if (!exited) continue;
            _startedCopies.Remove(id);
            copy.Process.Dispose();
        }
    }

    /// <summary>
    /// For a tool with no arguments: when a copy of its exe is already running (matched by its real
    /// image path, not just the process name), brings its window forward instead of starting another.
    /// Null when none is.
    /// </summary>
    private static ToolLaunchResult? TryActivateRunning(ToolEntry tool)
    {
        Process[]? candidates = null;
        try
        {
            candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(tool.TargetPath));
            var matching = candidates
                .Where(p => ProcessPathResolver.IsSamePath(ProcessPathResolver.GetProcessPath(p.Id), tool.TargetPath))
                .ToList();
            if (matching.Count == 0) return null;

            var withWindow = matching.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
            if (withWindow == null)
            {
                LoggingService.Info("ToolLauncher", $"Tool '{tool.Name}' is already running with no window (PID {matching[0].Id}).");
                return new ToolLaunchResult(ToolLaunchOutcome.AlreadyRunningNoWindow);
            }

            ProcessLauncherService.ActivateWindow(withWindow.MainWindowHandle);
            LoggingService.Info("ToolLauncher", $"Tool '{tool.Name}' already running (PID {withWindow.Id}); activated its window.");
            return new ToolLaunchResult(ToolLaunchOutcome.ActivatedRunning);
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("ToolLauncher", $"Could not check for a running copy of '{tool.Name}': {ex.Message}");
            return null;
        }
        finally
        {
            if (candidates != null)
            {
                foreach (var p in candidates) p.Dispose();
            }
        }
    }
}
