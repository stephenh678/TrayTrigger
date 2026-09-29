using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// Tools that start with games (Edit Tool > "Start when I launch a game"). Before a game TrayTrigger
/// launches, each one is started unless a copy of its program is already running - one the user
/// opened, or one started for an earlier game - and the copy started here is remembered for "Close
/// it when the game exits". Nothing is remembered across a restart: a copy started before TrayTrigger
/// closed is left running.
/// </summary>
public sealed class CompanionToolService
{
    private readonly Func<IReadOnlyList<ToolEntry>> _tools;
    private readonly Func<bool> _isEnabled;

    /// <summary>Starting is serialised, so two launches together can't both find a tool not running and start it twice.</summary>
    private readonly Lock _gate = new();

    /// <summary>
    /// The copy started for each tool, by tool id. Holding the process keeps its id from being reused
    /// while it is remembered, so closing it later can never reach another program.
    /// </summary>
    private readonly Dictionary<string, Process> _started = new(StringComparer.Ordinal);

    /// <param name="tools">Every tool, readable from the launch thread.</param>
    /// <param name="isEnabled">The Tools page is on (Settings > General). Off, nothing on it starts.</param>
    public CompanionToolService(Func<IReadOnlyList<ToolEntry>> tools, Func<bool> isEnabled)
    {
        _tools = tools;
        _isEnabled = isEnabled;
    }

    /// <summary>
    /// A tool is about to start for this game, then null once they all have. Its administrator prompt
    /// holds up the game, so the launch popup says what is being waited on.
    /// </summary>
    public event Action<GameEntry, ToolEntry?>? Starting;

    /// <summary>A tool that should have started with a game didn't; the text says which and why. A declined administrator prompt isn't reported.</summary>
    public event Action<string>? StartFailed;

    /// <summary>Starts the tool's program the way the Tools page does. Replaced in tests, so nothing real is started.</summary>
    internal Func<ToolEntry, Process?> StartProcess { get; set; } = tool => Process.Start(ToolLauncherService.BuildStartInfo(tool));

    /// <summary>The id of a running copy of the program, or null. Replaced in tests.</summary>
    internal Func<string, int?> FindRunningCopy { get; set; } = FindRunningProgram;

    internal Func<string, bool> FileExists { get; set; } = File.Exists;

    /// <summary>The ids of the tools whose started copy is remembered. For tests.</summary>
    internal IReadOnlyList<string> Remembered
    {
        get { lock (_gate) return _started.Keys.ToList(); }
    }

    /// <summary>
    /// Starts each tool that starts with games and isn't already running, one at a time. Blocks while
    /// Windows asks for administrator permission, so it runs on the launch thread, before the game:
    /// the prompt is answered before the game can go full screen over it. <paramref name="remember"/>
    /// is false for a launch TrayTrigger can't follow to its exit (a bare link), whose tools stay open.
    /// </summary>
    public void StartForGame(GameEntry game, bool remember)
    {
        var flagged = _tools().Where(t => t.StartWithGames).ToList();
        if (flagged.Count == 0) return;
        if (!_isEnabled())
        {
            LoggingService.Verbose("Tools", $"Not starting {flagged.Count} tool(s) with '{game.Name}': the Tools page is turned off in Settings.");
            return;
        }

        lock (_gate)
        {
            bool announced = false;
            try
            {
                foreach (var tool in flagged)
                {
                    if (!ToolCatalog.CanStartWithGames(tool))
                    {
                        // tools.json is user-editable; Edit Tool never saves the flag on a script or Store app.
                        LoggingService.Verbose("Tools", $"Not starting '{tool.Name}' with '{game.Name}': only a program can start with games.");
                        continue;
                    }

                    string? problem = ToolCatalog.ValidateTarget(tool.TargetPath, FileExists);
                    if (problem != null)
                    {
                        LoggingService.Warn("Tools", $"Not starting '{tool.Name}' with '{game.Name}': {problem} ('{tool.TargetPath}').");
                        Report($"\"{tool.Name}\" wasn't started with the game because {problem}.");
                        continue;
                    }

                    if (FindRunningCopy(tool.TargetPath) is int pid)
                    {
                        LoggingService.Verbose("Tools", $"Not starting '{tool.Name}' with '{game.Name}': it's already running (PID {pid}).");
                        continue;
                    }

                    announced = true;
                    Announce(game, tool);
                    Start(game, tool, remember);
                }
            }
            finally
            {
                if (announced) Announce(game, null);
            }
        }
    }

    private void Start(GameEntry game, ToolEntry tool, bool remember)
    {
        try
        {
            var process = StartProcess(tool);
            int? pid = process == null ? null : SafeId(process);
            LoggingService.Info("Tools", $"Started '{tool.Name}' with '{game.Name}'{(pid != null ? $" (PID {pid})" : string.Empty)}.");
            if (process == null) return;

            if (remember)
            {
                if (_started.Remove(tool.Id, out var previous)) previous.Dispose();
                _started[tool.Id] = process;
            }
            else
            {
                LoggingService.Verbose("Tools", $"'{tool.Name}' is left running after '{game.Name}': TrayTrigger can't tell when a game started from a link exits.");
                process.Dispose();
            }
        }
        catch (Exception ex) when (ToolLauncherService.ClassifyStartFailure(ex) == ToolLaunchOutcome.Cancelled)
        {
            LoggingService.Info("Tools", $"'{tool.Name}' wasn't started with '{game.Name}': the administrator prompt was declined.");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Tools", $"Could not start '{tool.Name}' with '{game.Name}': {ex.Message}");
            Report($"\"{tool.Name}\" didn't start with the game: {ex.Message}");
        }
    }

    private void Announce(GameEntry game, ToolEntry? tool)
    {
        try { Starting?.Invoke(game, tool); }
        catch (Exception ex) { LoggingService.Swallowed("Tools", ex, "showing which tool is starting"); }
    }

    private void Report(string message)
    {
        try { StartFailed?.Invoke(message); }
        catch (Exception ex) { LoggingService.Swallowed("Tools", ex, "reporting a tool that didn't start"); }
    }

    private static int? SafeId(Process process)
    {
        try { return process.Id; }
        catch (InvalidOperationException) { return null; } // it has already exited
    }

    /// <summary>
    /// A running copy of the program at <paramref name="path"/>, matched by its real image path rather
    /// than its name. The path of a program running as administrator can be read too
    /// (<see cref="ProcessPathResolver"/>), so one of those counts as running.
    /// </summary>
    internal static int? FindRunningProgram(string path)
    {
        Process[] candidates;
        try
        {
            candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path));
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed("Tools", ex, $"looking for a running copy of '{path}'");
            return null;
        }

        try
        {
            return candidates.FirstOrDefault(p => ProcessPathResolver.IsSamePath(ProcessPathResolver.GetProcessPath(p.Id), path))?.Id;
        }
        finally
        {
            foreach (var p in candidates) p.Dispose();
        }
    }
}
