using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// Tools that start with games (Edit Tool > "Start when I launch a game"). Before a game TrayTrigger
/// launches, each one is started unless a copy of its program is already running - one the user
/// opened, or one started for an earlier game. The copy started here is remembered, and once the last
/// game has exited it is closed if the tool says "Close it when the game exits"; a copy TrayTrigger
/// didn't start is never touched. Nothing is remembered across a restart: a copy started before
/// TrayTrigger closed is left running.
/// </summary>
public sealed partial class CompanionToolService
{
    private readonly Func<IReadOnlyList<ToolEntry>> _tools;
    private readonly Func<bool> _isEnabled;

    /// <summary>
    /// Starting and closing never overlap. Two launches together can't both find a tool not running and
    /// start it twice, and a launch that arrives while tools are being closed waits, then finds the tool
    /// closed and starts it again.
    /// </summary>
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

    /// <summary>The id of a running copy that counts as the tool already running, or null. Replaced in tests.</summary>
    internal Func<ToolEntry, int?> FindRunningCopy { get; set; } = FindRunningCopyOf;

    internal Func<string, bool> FileExists { get; set; } = File.Exists;

    /// <summary>The clock and the wait for "Wait before starting the game". Replaced in tests.</summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;
    internal Action<TimeSpan> Wait { get; set; } = Thread.Sleep;

    /// <summary>How long a tool is given to close when asked, before it is ended.</summary>
    internal TimeSpan CloseGrace { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long after a tool starts a program it starts still counts as part of it, and is closed with
    /// it: Afterburner starts RTSS within seconds. One opened from it later, like a browser for a link,
    /// is left alone.
    /// </summary>
    internal static readonly TimeSpan StartupWindow = TimeSpan.FromSeconds(30);

    /// <summary>The programs a copy started as it started up. Replaced in tests.</summary>
    internal Func<HeldProcess, IReadOnlyList<HeldProcess>> FindStartedBy { get; set; } = StartedAsItStartedUp;

    /// <summary>
    /// What a copy started as it started up and runs in the background, like RTSS in the tray. One with
    /// a window of its own - a browser the tool opened, say - is the user's, and is left open with
    /// everything under it.
    /// </summary>
    internal static IReadOnlyList<HeldProcess> StartedAsItStartedUp(HeldProcess root) =>
        ProcessTree.StartupDescendants(root, StartupWindow, child =>
        {
            if (!ProcessPathResolver.HasVisibleWindow(child.Id)) return true;
            LoggingService.Verbose("Tools", $"Leaving {child.Name} (PID {child.Id}) open: '{root.Name}' started it, but it has a window of its own.");
            return false;
        });

    /// <summary>Asks the copy and what it started to close, then ends them. Replaced in tests.</summary>
    internal Func<IReadOnlyList<HeldProcess>, TimeSpan, EndResult> EndCopy { get; set; } = EndNormally;

    /// <summary>The same with administrator rights, behind one UAC prompt; true when they have all closed. Replaced in tests.</summary>
    internal Func<IReadOnlyList<HeldProcess>, TimeSpan, bool> EndCopyAsAdministrator { get; set; } = EndElevated;

    /// <summary>
    /// A tool running as administrator is about to be closed (true), which takes Windows' permission,
    /// then the prompt has been answered (false). The prompt names Windows PowerShell rather than the
    /// tool, so the launch popup says what it is for.
    /// </summary>
    public event Action<ToolEntry, bool>? ClosingAsAdministrator;

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
            (ToolEntry Tool, DateTime ReadyAt)? waitFor = null;
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
                        ActivityService.Add(ActivityLevel.Problem, $"{tool.Name} wasn't started with {game.Name}", subject: tool.Name,
                            detail: $"Because {problem}. Check it in Edit Tool.", groupKey: $"tool.start|{tool.Id}");
                        continue;
                    }

                    if (FindRunningCopy(tool) is int pid)
                    {
                        LoggingService.Verbose("Tools", $"Not starting '{tool.Name}' with '{game.Name}': it's already running (PID {pid}).");
                        continue;
                    }

                    announced = true;
                    Announce(game, tool);
                    if (Start(game, tool, remember) && tool.WaitBeforeGame)
                    {
                        int seconds = Math.Clamp(tool.WaitBeforeGameSeconds, ToolCatalog.MinWaitSeconds, ToolCatalog.MaxWaitSeconds);
                        DateTime readyAt = UtcNow() + TimeSpan.FromSeconds(seconds);
                        if (waitFor == null || readyAt > waitFor.Value.ReadyAt) waitFor = (tool, readyAt);
                    }
                }
                WaitUntilReady(game, waitFor);
            }
            finally
            {
                if (announced) Announce(game, null);
            }
        }
    }

    /// <summary>
    /// Holds the game back until every tool that asked for a wait has had its seconds. They start one
    /// after another and each counts from its own start, so it's the latest of those, not their sum.
    /// The popup names the tool being waited on.
    /// </summary>
    private void WaitUntilReady(GameEntry game, (ToolEntry Tool, DateTime ReadyAt)? waitFor)
    {
        if (waitFor is not { } last) return;
        TimeSpan remaining = last.ReadyAt - UtcNow();
        if (remaining <= TimeSpan.Zero) return;
        // The clock can be moved back while tools start (a time sync just after boot): never past the maximum.
        TimeSpan longest = TimeSpan.FromSeconds(ToolCatalog.MaxWaitSeconds);
        if (remaining > longest) remaining = longest;

        Announce(game, last.Tool);
        LoggingService.Verbose("Tools", $"Waiting {remaining.TotalSeconds:0.#}s for '{last.Tool.Name}' to get ready before starting '{game.Name}'.");
        Wait(remaining);
    }

    /// <summary>True when the program was started; false when the prompt was declined or it failed.</summary>
    private bool Start(GameEntry game, ToolEntry tool, bool remember)
    {
        try
        {
            var process = StartProcess(tool);
            int? pid = process == null ? null : SafeId(process);
            LoggingService.Info("Tools", $"Started '{tool.Name}' with '{game.Name}'{(pid != null ? $" (PID {pid})" : string.Empty)}.");
            if (process == null) return true;

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
            return true;
        }
        catch (Exception ex) when (ToolLauncherService.ClassifyStartFailure(ex) == ToolLaunchOutcome.Cancelled)
        {
            LoggingService.Info("Tools", $"'{tool.Name}' wasn't started with '{game.Name}': the administrator prompt was declined.");
            return false;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Tools", $"Could not start '{tool.Name}' with '{game.Name}': {ex.Message}");
            Report($"\"{tool.Name}\" didn't start with the game: {ex.Message}");
            ActivityService.Add(ActivityLevel.Problem, $"{tool.Name} didn't start with {game.Name}", subject: tool.Name,
                detail: ex.Message, groupKey: $"tool.start|{tool.Id}");
            return false;
        }
    }

    /// <summary>
    /// Closes, in the background, the copies started for games whose tool says "Close it when the game
    /// exits" - once no game is running or being launched. <paramref name="isIdle"/> is asked again when
    /// closing begins, so a game launched in the meantime keeps its tools until it has exited too.
    /// </summary>
    public Task CloseWhenIdleAsync(Func<bool> isIdle) => Task.Run(() => CloseIfIdle(isIdle));

    internal void CloseIfIdle(Func<bool> isIdle)
    {
        lock (_gate)
        {
            if (_started.Count == 0) return;
            if (!isIdle())
            {
                LoggingService.Verbose("Tools", "A game is running or being launched, so the tools started with games stay open.");
                return;
            }
            var tools = _tools();
            foreach (var (id, process) in _started.ToList())
            {
                // Forgotten whatever happens next: if it isn't closed now, it's the user's from here on.
                _started.Remove(id);
                var tool = tools.FirstOrDefault(t => t.Id == id);
                using (process)
                {
                    // One tool failing must not leave the rest open. This runs on a task nobody awaits,
                    // so an exception that escaped would never be seen.
                    try
                    {
                        Close(tool, process);
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Warn("Tools", $"Could not close '{tool?.Name ?? id}': {ex.Message}");
                    }
                }
            }
        }
    }


    private void Close(ToolEntry? tool, Process process)
    {
        int? pid = SafeId(process);
        if (HasExited(process))
        {
            LoggingService.Verbose("Tools", $"'{tool?.Name ?? "A removed tool"}' (PID {pid}) had already closed.");
            return;
        }
        if (tool == null || !tool.StartWithGames || !tool.CloseAfterGames || !ToolCatalog.CanStartWithGames(tool))
        {
            LoggingService.Verbose("Tools", $"Leaving '{tool?.Name ?? "a removed tool"}' running (PID {pid}): it isn't set to close when the game exits.");
            return;
        }

        // The copy, and the programs it started as it started up, each held open until they're done.
        using var root = pid is int id ? HeldProcess.Open(id, tool.Name) : null;
        if (root == null)
        {
            LoggingService.Verbose("Tools", $"'{tool.Name}' (PID {pid}) closed while TrayTrigger was getting to it.");
            return;
        }
        var startedBy = FindStartedBy(root);
        try
        {
            if (startedBy.Count > 0 && LoggingService.IsVerboseEnabled)
            {
                LoggingService.Verbose("Tools", $"'{tool.Name}' started {string.Join(", ", startedBy.Select(p => $"{p.Name} (PID {p.Id})"))} as it started up; closing them with it.");
            }
            var targets = new List<HeldProcess>(startedBy.Count + 1) { root };
            targets.AddRange(startedBy);

            var result = EndCopy(targets, CloseGrace);
            if (result == EndResult.NeedsAdministrator)
            {
                LoggingService.Info("Tools", $"'{tool.Name}' (PID {pid}) runs as administrator; asking Windows for permission to close it.");
                AnnounceClosing(tool, true);
                try
                {
                    result = EndCopyAsAdministrator(targets, CloseGrace) ? EndResult.Ended : EndResult.StillRunning;
                }
                finally
                {
                    AnnounceClosing(tool, false);
                }
            }

            string also = startedBy.Count > 0 ? $" and {startedBy.Count} program(s) it started" : string.Empty;
            if (result == EndResult.Ended)
            {
                // Not in Activity & History: the tool doing what it was set to do; only failures are.
                LoggingService.Info("Tools", $"Closed '{tool.Name}' (PID {pid}){also}, started with games.");
            }
            else
            {
                LoggingService.Warn("Tools", $"'{tool.Name}' (PID {pid}){also} still running after TrayTrigger tried to close it.");
                ActivityService.Add(ActivityLevel.Problem, $"{tool.Name} couldn't be closed after your game", subject: tool.Name,
                    detail: "It's still running. Close it yourself; if it runs as administrator, allow the prompt next time.",
                    groupKey: $"tool.close|{tool.Id}");
            }
        }
        finally
        {
            foreach (var p in startedBy) p.Dispose();
        }
    }

    private void Announce(GameEntry game, ToolEntry? tool)
    {
        try { Starting?.Invoke(game, tool); }
        catch (Exception ex) { LoggingService.Swallowed("Tools", ex, "showing which tool is starting"); }
    }

    private void AnnounceClosing(ToolEntry tool, bool asking)
    {
        try { ClosingAsAdministrator?.Invoke(tool, asking); }
        catch (Exception ex) { LoggingService.Swallowed("Tools", ex, "showing which tool is closing"); }
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

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Nothing left to ask about: treat it as gone rather than end something unknown.
            return true;
        }
    }

    internal enum EndResult { Ended, NeedsAdministrator, StillRunning }

    private const uint WmClose = 0x0010;
    private const int ErrorAccessDenied = 5;

    /// <summary>
    /// Asks each top-level window of every target to close, as its own close button would, hidden ones
    /// included: a tool in the tray has no visible window but usually still answers. What's still running
    /// after <paramref name="grace"/> is ended. Windows doesn't let a program close or end one running as
    /// administrator; when a target is one of those it's <see cref="EndResult.NeedsAdministrator"/>, and
    /// the rest have still had their chance.
    /// </summary>
    internal static EndResult EndNormally(IReadOnlyList<HeldProcess> targets, TimeSpan grace)
    {
        var denied = new List<HeldProcess>();
        foreach (var target in targets)
        {
            foreach (IntPtr window in ProcessPathResolver.TopLevelWindows(target.Id))
            {
                if (!PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero) && Marshal.GetLastPInvokeError() == ErrorAccessDenied)
                {
                    denied.Add(target);
                    break;
                }
            }
        }

        var askable = targets.Except(denied).ToList();
        WaitForAll(askable, grace);
        foreach (var target in askable.Where(t => !t.HasExited))
        {
            try
            {
                // By id: it's held open, so the id still names the same process.
                using var process = Process.GetProcessById(target.Id);
                process.Kill();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorAccessDenied)
            {
                LoggingService.Verbose("Tools", $"{target.Name} (PID {target.Id}) can't be ended without administrator rights: {ex.Message}");
                denied.Add(target);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // It closed between the wait and the kill.
            }
        }
        WaitForAll(askable, TimeSpan.FromSeconds(5));

        if (targets.All(t => t.HasExited)) return EndResult.Ended;
        return denied.Any(t => !t.HasExited) ? EndResult.NeedsAdministrator : EndResult.StillRunning;
    }

    /// <summary>
    /// <see cref="EndNormally"/> with administrator rights, behind one UAC prompt, for every target
    /// still running: taskkill without /F posts the same close request to each one's windows, and
    /// Stop-Process ends what's still running after <paramref name="grace"/>. TrayTrigger holds them all
    /// open meanwhile, so their ids can't be given to other programs. True when they've all closed.
    /// </summary>
    internal static bool EndElevated(IReadOnlyList<HeldProcess> targets, TimeSpan grace)
    {
        var running = targets.Where(t => !t.HasExited).ToList();
        if (running.Count == 0) return true;

        string taskkill = ElevatedPowerShell.QuoteLiteral(Path.Combine(Environment.SystemDirectory, "taskkill.exe"));
        string script =
            $"$ids = @({string.Join(",", running.Select(t => t.Id))}); " +
            $"foreach ($id in $ids) {{ & {taskkill} /PID $id | Out-Null }}; " +
            $"$deadline = [DateTime]::UtcNow.AddMilliseconds({(int)grace.TotalMilliseconds}); " +
            "foreach ($id in $ids) { $p = Get-Process -Id $id -ErrorAction SilentlyContinue; if (-not $p) { continue }; " +
            "$left = [int][Math]::Max(0, ($deadline - [DateTime]::UtcNow).TotalMilliseconds); " +
            "if (-not $p.WaitForExit($left)) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue } }; " +
            "exit 0";
        ElevatedPowerShell.Run(script, grace + TimeSpan.FromSeconds(15), "Tools");
        // A process ended by Stop-Process can take a moment to finish exiting.
        WaitForAll(running, TimeSpan.FromSeconds(2));
        return running.All(t => t.HasExited);
    }

    /// <summary>Waits until they've all exited or <paramref name="timeout"/> has passed, whichever is first.</summary>
    private static void WaitForAll(IEnumerable<HeldProcess> targets, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        foreach (var target in targets)
        {
            TimeSpan left = timeout - watch.Elapsed;
            if (left <= TimeSpan.Zero) return;
            target.WaitForExit(left);
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// A running copy of the tool's program that counts as the tool already running, or null. With no
    /// launch arguments any copy does, one running as administrator included. With arguments only a
    /// copy started with the same ones does, so a tool that runs a script or .jar through a program many
    /// others share (javaw.exe, python.exe, AutoHotkey) isn't taken for running because some other one
    /// is. A copy whose command line can't be read counts, rather than risk starting a second one.
    /// </summary>
    internal static int? FindRunningCopyOf(ToolEntry tool)
    {
        var copies = ProcessPathResolver.FindRunningCopies(tool.TargetPath);
        try
        {
            string wanted = tool.Arguments?.Trim() ?? string.Empty;
            foreach (var copy in copies)
            {
                if (wanted.Length == 0) return copy.Id;
                string? commandLine = ProcessPathResolver.GetCommandLine(copy.Id);
                if (commandLine == null || ProcessPathResolver.SameArguments(ProcessPathResolver.ArgumentsOf(commandLine), wanted))
                {
                    return copy.Id;
                }
            }
            if (copies.Count > 0 && LoggingService.IsVerboseEnabled)
            {
                LoggingService.Verbose("Tools", $"{copies.Count} copy(ies) of '{tool.TargetPath}' running, none with the tool's arguments ('{wanted}').");
            }
            return null;
        }
        finally
        {
            foreach (var p in copies) p.Dispose();
        }
    }
}
