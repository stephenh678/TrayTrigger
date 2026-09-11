using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>What happened to the pre-launch script, and whether the launch should go ahead.</summary>
public readonly record struct PreLaunchScriptResult(bool ProceedWithLaunch, string? AbortReason)
{
    public static readonly PreLaunchScriptResult Proceed = new(true, null);
    public static PreLaunchScriptResult Abort(string reason) => new(false, reason);
}

/// <summary>
/// Outcome of a "Test Run" from Edit Game (see <see cref="GameScriptService.TestRun"/>).
/// <see cref="Started"/> is false when the script could not be launched at all, with the reason
/// in <see cref="Error"/>. <see cref="ExitCode"/> is null when the run timed out and was killed.
/// <see cref="Output"/> is stdout and stderr interleaved in arrival order, stderr lines prefixed.
/// </summary>
public sealed record ScriptTestResult(bool Started, int? ExitCode, bool TimedOut, string Output, string? Error, TimeSpan Elapsed)
{
    public bool Succeeded => Started && !TimedOut && ExitCode == 0;
}

/// <summary>
/// Runs a game's optional user-supplied pre-launch and post-exit scripts. Sits alongside
/// <see cref="PerformanceProfileService"/> (the built-in, snapshot-and-restore tweaks) and
/// covers everything that service can't know about: killing a background app, switching an
/// audio device, toggling RGB, remapping a controller, and so on.
///
/// Supported script types: .bat/.cmd (via cmd.exe), .ps1 (via powershell.exe with
/// -ExecutionPolicy Bypass), .exe/.com (run directly). Anything else is refused - a shell
/// fallback would depend on the user's file associations and couldn't honour "run hidden".
///
/// Each script receives five positional arguments - phase ("prelaunch" or "postexit"), game
/// name, game executable path, game ID, playtime in minutes (empty on pre-launch, so the
/// position is stable) - and, when not elevated, the same data as TRAYTRIGGER_* environment
/// variables (see <see cref="BuildStartInfo"/>). Elevated launches go through ShellExecute, which
/// cannot carry a custom environment, so the arguments are the only way those scripts can read
/// the game ID and playtime. The game's free-text <see cref="GameEntry.ScriptArguments"/> follow
/// the five positional ones: raw for cmd.exe, tokenised for PowerShell and executables.
///
/// When a script runs hidden (and not elevated) its stdout/stderr are captured into the
/// TrayTrigger log under the GameScript category, so a misbehaving script can be diagnosed
/// without editing it to redirect its own output.
/// </summary>
public class GameScriptService
{
    public const string PhasePreLaunch = "prelaunch";
    public const string PhasePostExit = "postexit";

    /// <summary>Default upper bound on how long a "wait for it" pre-launch script can hold up the game launch.</summary>
    public static readonly TimeSpan DefaultPreLaunchWaitTimeout = TimeSpan.FromSeconds(30);
    public const int MinPreLaunchTimeoutSeconds = 1;
    public const int MaxPreLaunchTimeoutSeconds = 600;

    public static readonly IReadOnlyList<string> SupportedExtensions = new[] { ".bat", ".cmd", ".ps1", ".exe", ".com" };

    /// <summary>"*.bat;*.cmd;*.ps1;*.exe;*.com" - for file-dialog filters.</summary>
    public static string SupportedExtensionsFilterPattern => string.Join(";", SupportedExtensions.Select(e => "*" + e));

    /// <summary>True if the path has one of the <see cref="SupportedExtensions"/>. Does not check existence.</summary>
    public static bool IsSupportedScript(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string ext = Path.GetExtension(path.Trim().Trim('"'));
        return SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The effective wait for a game's own pre-launch script, clamped to the supported range.</summary>
    public static TimeSpan EffectivePreLaunchTimeout(GameEntry game) => ClampPreLaunchTimeout(game.PreLaunchScriptTimeoutSeconds);

    /// <summary>Clamps a configured wait to the supported range, falling back to the default when out of range.</summary>
    public static TimeSpan ClampPreLaunchTimeout(int seconds)
    {
        if (seconds < MinPreLaunchTimeoutSeconds || seconds > MaxPreLaunchTimeoutSeconds)
        {
            seconds = (int)DefaultPreLaunchWaitTimeout.TotalSeconds;
        }
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// The script that will actually run for one phase of one game, after the Settings defaults
    /// have been applied. <see cref="IsDefault"/> says which side it came from, for logging and
    /// for the Edit Game summary.
    /// </summary>
    public readonly record struct EffectiveScript(string Path, bool Wait, int TimeoutSeconds, bool AbortOnFailure, bool Hidden, bool Elevated, bool IsDefault);

    /// <summary>
    /// Per-phase resolution: the game's own pre-launch script if it has one; otherwise the
    /// default pre-launch script unless the game opted out; otherwise nothing. A game's own
    /// options apply only to its own script, the defaults' options to the default script.
    /// </summary>
    public static EffectiveScript? ResolvePreLaunch(GameEntry game, ScriptDefaults? defaults)
    {
        if (!string.IsNullOrWhiteSpace(game.PreLaunchScriptPath))
        {
            return new EffectiveScript(game.PreLaunchScriptPath, game.WaitForPreLaunchScript, game.PreLaunchScriptTimeoutSeconds, game.AbortLaunchOnScriptFailure, game.RunScriptsHidden, game.RunScriptsAsAdmin, IsDefault: false);
        }
        if (defaults is { Enabled: true } && !game.SkipDefaultScripts && defaults.HasPreLaunchScript)
        {
            return new EffectiveScript(defaults.PreLaunchScriptPath, defaults.WaitForPreLaunchScript, defaults.PreLaunchScriptTimeoutSeconds, defaults.AbortLaunchOnScriptFailure, defaults.RunScriptsHidden, defaults.RunScriptsAsAdmin, IsDefault: true);
        }
        return null;
    }

    /// <summary>Per-phase resolution for post-exit; see <see cref="ResolvePreLaunch"/>.</summary>
    public static EffectiveScript? ResolvePostExit(GameEntry game, ScriptDefaults? defaults)
    {
        if (!string.IsNullOrWhiteSpace(game.PostExitScriptPath))
        {
            return new EffectiveScript(game.PostExitScriptPath, game.WaitForPreLaunchScript, game.PreLaunchScriptTimeoutSeconds, game.AbortLaunchOnScriptFailure, game.RunScriptsHidden, game.RunScriptsAsAdmin, IsDefault: false);
        }
        if (defaults is { Enabled: true } && !game.SkipDefaultScripts && defaults.HasPostExitScript)
        {
            return new EffectiveScript(defaults.PostExitScriptPath, defaults.WaitForPreLaunchScript, defaults.PreLaunchScriptTimeoutSeconds, defaults.AbortLaunchOnScriptFailure, defaults.RunScriptsHidden, defaults.RunScriptsAsAdmin, IsDefault: true);
        }
        return null;
    }

    private readonly Lock _lock = new();
    private readonly Dictionary<string, GameEntry> _pendingPostExit = new(StringComparer.Ordinal);
    private readonly Func<bool> _isFeatureEnabled;
    private readonly Func<ScriptDefaults?> _defaults;

    /// <param name="isFeatureEnabled">
    /// The Settings "Enable game scripts" switch. This is the real kill-switch: when it returns
    /// false no script runs, even for a game whose entry still carries script paths (from before
    /// the feature was turned off, or from a hand-edited games.json). Defaults to always-on for
    /// tests and callers that don't wire settings.
    /// </param>
    /// <param name="defaults">
    /// The Settings default scripts, read at each launch so edits apply without a restart. Null
    /// (the default) means no defaults - every game runs only its own scripts.
    /// </param>
    public GameScriptService(Func<bool>? isFeatureEnabled = null, Func<ScriptDefaults?>? defaults = null)
    {
        _isFeatureEnabled = isFeatureEnabled ?? (static () => true);
        _defaults = defaults ?? (static () => null);
    }

    /// <summary>
    /// Runs the game's pre-launch script (its own, or the Settings default), if any. Never throws.
    /// Unless the effective script opts into abort-on-failure, a broken script is logged and the
    /// launch proceeds; with it, a non-zero exit code, a timeout, or a script that fails to start
    /// returns an abort result the launcher honours. If the script asks to wait (or to abort on
    /// failure, which implies waiting), this returns once the script exits or its timeout elapses.
    /// </summary>
    public PreLaunchScriptResult RunPreLaunch(GameEntry game)
    {
        var script = ResolvePreLaunch(game, _defaults());
        if (script == null) return PreLaunchScriptResult.Proceed;

        string label = script.Value.IsDefault ? "Default pre-launch script" : "Pre-launch script";
        string path = script.Value.Path;

        if (!_isFeatureEnabled())
        {
            LoggingService.Info("GameScript", $"{label} for '{game.Name}' skipped: game scripts are disabled in Settings.");
            return PreLaunchScriptResult.Proceed;
        }

        bool abortOnFailure = script.Value.AbortOnFailure;
        bool wait = script.Value.Wait || abortOnFailure;
        TimeSpan timeout = ClampPreLaunchTimeout(script.Value.TimeoutSeconds);

        if (!IsSupportedScript(path))
        {
            LoggingService.Warn("GameScript", $"{label} for '{game.Name}' has an unsupported type and was skipped: {path} (supported: {string.Join(", ", SupportedExtensions)})");
            return abortOnFailure ? PreLaunchScriptResult.Abort("the pre-launch script has an unsupported file type") : PreLaunchScriptResult.Proceed;
        }

        var psi = BuildStartInfo(path, game, PhasePreLaunch, script.Value.Hidden, script.Value.Elevated, playedMinutes: null);
        if (psi == null)
        {
            LoggingService.Warn("GameScript", $"{label} for '{game.Name}' not found: {path}");
            return abortOnFailure ? PreLaunchScriptResult.Abort("the pre-launch script file was not found") : PreLaunchScriptResult.Proceed;
        }

        Process? process = null;
        try
        {
            LoggingService.Info("GameScript", $"Running {label.ToLowerInvariant()} for '{game.Name}': {path} (wait={wait}, timeout={timeout.TotalSeconds:0}s, hidden={script.Value.Hidden}, admin={script.Value.Elevated}, abortOnFailure={abortOnFailure})");
            process = Process.Start(psi);
            if (process == null)
            {
                LoggingService.Warn("GameScript", $"{label} for '{game.Name}' did not start.");
                return abortOnFailure ? PreLaunchScriptResult.Abort("the pre-launch script did not start") : PreLaunchScriptResult.Proceed;
            }

            AttachOutputLogging(process, psi, game, PhasePreLaunch);

            if (!wait)
            {
                DisposeOnExit(process);
                process = null;
                return PreLaunchScriptResult.Proceed;
            }

            if (process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                // With redirected output the timed overload can return before the async
                // readers have drained; the untimed one waits for them.
                if (psi.RedirectStandardOutput) process.WaitForExit();

                int exitCode = process.ExitCode;
                if (exitCode != 0)
                {
                    LoggingService.Warn("GameScript", $"Pre-launch script for '{game.Name}' exited with code {exitCode}{(abortOnFailure ? "; cancelling the launch." : "; launching anyway.")}");
                    return abortOnFailure ? PreLaunchScriptResult.Abort($"the pre-launch script exited with code {exitCode}") : PreLaunchScriptResult.Proceed;
                }

                LoggingService.Verbose("GameScript", $"Pre-launch script for '{game.Name}' completed.");
                return PreLaunchScriptResult.Proceed;
            }

            LoggingService.Warn("GameScript", $"Pre-launch script for '{game.Name}' is still running after {timeout.TotalSeconds:0}s; {(abortOnFailure ? "cancelling the launch." : "launching the game without waiting further.")}");
            DisposeOnExit(process);
            process = null;
            return abortOnFailure
                ? PreLaunchScriptResult.Abort($"the pre-launch script did not finish within {timeout.TotalSeconds:0}s")
                : PreLaunchScriptResult.Proceed;
        }
        catch (Exception ex)
        {
            // Includes the user cancelling a UAC prompt (Win32Exception 1223) for elevated scripts.
            LoggingService.Warn("GameScript", $"Pre-launch script for '{game.Name}' failed: {ex.Message}");
            return abortOnFailure ? PreLaunchScriptResult.Abort($"the pre-launch script failed to run ({ex.Message})") : PreLaunchScriptResult.Proceed;
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>
    /// Registers that this game's session is being tracked, so its post-exit script can still run
    /// if TrayTrigger shuts down before the game does. Call as soon as a session with a real exit
    /// signal begins (direct .exe launch, Steam session, or client launch tracked by install
    /// directory), never for fire-and-forget protocol launches.
    /// </summary>
    public void TrackPostExit(GameEntry game)
    {
        if (ResolvePostExit(game, _defaults()) == null) return;
        lock (_lock)
        {
            _pendingPostExit[game.Id] = game;
        }
        LoggingService.Verbose("GameScript", $"Tracking post-exit script for '{game.Name}' in case of early shutdown.");
    }

    /// <summary>Forgets a tracked post-exit script without running it (the game never actually started).</summary>
    public void UntrackPostExit(GameEntry game)
    {
        lock (_lock)
        {
            _pendingPostExit.Remove(game.Id);
        }
    }

    /// <summary>Runs the game's post-exit script, if configured. Fire-and-forget; never throws.</summary>
    public void RunPostExit(GameEntry game, long playedMinutes)
    {
        lock (_lock)
        {
            _pendingPostExit.Remove(game.Id);
        }

        var script = ResolvePostExit(game, _defaults());
        if (script == null) return;

        string label = script.Value.IsDefault ? "Default post-exit script" : "Post-exit script";
        string path = script.Value.Path;

        if (!_isFeatureEnabled())
        {
            LoggingService.Info("GameScript", $"{label} for '{game.Name}' skipped: game scripts are disabled in Settings.");
            return;
        }

        if (!IsSupportedScript(path))
        {
            LoggingService.Warn("GameScript", $"{label} for '{game.Name}' has an unsupported type and was skipped: {path} (supported: {string.Join(", ", SupportedExtensions)})");
            return;
        }

        var psi = BuildStartInfo(path, game, PhasePostExit, script.Value.Hidden, script.Value.Elevated, playedMinutes);
        if (psi == null)
        {
            LoggingService.Warn("GameScript", $"{label} for '{game.Name}' not found: {path}");
            return;
        }

        try
        {
            LoggingService.Info("GameScript", $"Running {label.ToLowerInvariant()} for '{game.Name}': {path} (playtime {playedMinutes}m, hidden={script.Value.Hidden}, admin={script.Value.Elevated})");
            var process = Process.Start(psi);
            if (process == null)
            {
                LoggingService.Warn("GameScript", $"{label} for '{game.Name}' did not start.");
                return;
            }
            AttachOutputLogging(process, psi, game, PhasePostExit);
            DisposeOnExit(process);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("GameScript", $"Post-exit script for '{game.Name}' failed: {ex.Message}");
        }
    }

    /// <summary>
    /// TrayTrigger is exiting while games with post-exit scripts may still be running. Run those
    /// scripts now rather than never, mirroring how Performance Profiles restore on shutdown.
    /// </summary>
    public void RunPendingPostExitScriptsOnShutdown()
    {
        List<GameEntry> pending;
        lock (_lock)
        {
            pending = new List<GameEntry>(_pendingPostExit.Values);
            _pendingPostExit.Clear();
        }

        foreach (var game in pending)
        {
            LoggingService.Info("GameScript", $"Running post-exit script for '{game.Name}' on application exit.");
            RunPostExit(game, playedMinutes: 0);
        }
    }

    /// <summary>Upper bound on a Test Run; the script is killed when it elapses.</summary>
    public static readonly TimeSpan TestRunTimeout = TimeSpan.FromSeconds(30);
    /// <summary>Lines kept from a Test Run's output before it is truncated.</summary>
    public const int TestRunMaxOutputLines = 500;

    /// <summary>
    /// Runs a script the way "Test Run" in Edit Game does: always hidden and never elevated (so
    /// stdout/stderr can be captured and shown), synchronously, killed on timeout. Deliberately
    /// ignores the Settings kill-switch - the user just clicked a button asking for exactly this
    /// run. Never throws; call from a background thread.
    /// </summary>
    public static ScriptTestResult TestRun(string scriptPath, GameEntry game, string phase, long? playedMinutes, TimeSpan? timeout = null)
    {
        TimeSpan limit = timeout ?? TestRunTimeout;
        var sw = Stopwatch.StartNew();

        string trimmed = scriptPath.Trim().Trim('"');
        if (!IsSupportedScript(trimmed))
        {
            return new ScriptTestResult(false, null, false, string.Empty, $"Unsupported file type. Supported: {string.Join(", ", SupportedExtensions)}.", sw.Elapsed);
        }
        if (!File.Exists(trimmed))
        {
            return new ScriptTestResult(false, null, false, string.Empty, "The script file was not found.", sw.Elapsed);
        }

        var psi = BuildStartInfo(trimmed, game, phase, hidden: true, elevated: false, playedMinutes);
        if (psi == null)
        {
            return new ScriptTestResult(false, null, false, string.Empty, "The script could not be prepared to run.", sw.Elapsed);
        }

        var lines = new List<string>();
        int dropped = 0;
        var outputLock = new object();
        void Capture(string? text, bool isError)
        {
            if (text == null) return;
            lock (outputLock)
            {
                if (lines.Count >= TestRunMaxOutputLines) { dropped++; return; }
                lines.Add(isError ? "[stderr] " + text : text);
            }
        }

        try
        {
            LoggingService.Info("GameScript", $"Test run of {phase} script for '{game.Name}': {trimmed}");
            using var process = Process.Start(psi);
            if (process == null)
            {
                return new ScriptTestResult(false, null, false, string.Empty, "The script did not start.", sw.Elapsed);
            }

            process.OutputDataReceived += (_, e) => Capture(e.Data, isError: false);
            process.ErrorDataReceived += (_, e) => Capture(e.Data, isError: true);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            bool exited = process.WaitForExit((int)limit.TotalMilliseconds);
            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { process.WaitForExit(5000); } catch { }
                return new ScriptTestResult(true, null, true, JoinOutput(), null, sw.Elapsed);
            }

            // Let the async readers drain (the timed overload can return before they have).
            process.WaitForExit();
            int exitCode = process.ExitCode;
            LoggingService.Info("GameScript", $"Test run of {phase} script for '{game.Name}' exited with code {exitCode} after {sw.Elapsed.TotalSeconds:0.0}s.");
            return new ScriptTestResult(true, exitCode, false, JoinOutput(), null, sw.Elapsed);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("GameScript", $"Test run of {phase} script for '{game.Name}' failed: {ex.Message}");
            return new ScriptTestResult(false, null, false, JoinOutput(), ex.Message, sw.Elapsed);
        }

        string JoinOutput()
        {
            lock (outputLock)
            {
                var all = new List<string>(lines);
                if (dropped > 0) all.Add($"... ({dropped} more line(s) not shown)");
                return string.Join(Environment.NewLine, all);
            }
        }
    }

    /// <summary>
    /// Streams a hidden script's stdout/stderr into the log. Only possible when the process was
    /// started with redirected streams (hidden and not elevated - see <see cref="BuildStartInfo"/>).
    /// </summary>
    private static void AttachOutputLogging(Process process, ProcessStartInfo psi, GameEntry game, string phase)
    {
        if (!psi.RedirectStandardOutput) return;
        string tag = $"[{game.Name} {phase}]";
        try
        {
            process.OutputDataReceived += (_, e) => { if (e.Data != null) LoggingService.Verbose("GameScript", $"{tag} {e.Data}"); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) LoggingService.Warn("GameScript", $"{tag} {e.Data}"); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("GameScript", $"{tag} could not capture script output: {ex.Message}");
        }
    }

    /// <summary>
    /// Keeps the Process object (and therefore its redirected pipes) alive until the script exits,
    /// then logs the exit code and disposes it. Disposing early would close the script's stdout
    /// and make a chatty script die with a broken pipe.
    /// </summary>
    private static void DisposeOnExit(Process process)
    {
        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += (s, _) =>
            {
                if (s is not Process p) return;
                try
                {
                    LoggingService.Verbose("GameScript", $"Script process {p.Id} exited with code {p.ExitCode}.");
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            };
            if (process.HasExited)
            {
                // Exited may already have fired (or never will if it raced EnableRaisingEvents);
                // disposing twice is harmless.
                process.Dispose();
            }
        }
        catch
        {
            process.Dispose();
        }
    }

    /// <summary>
    /// Builds the process start info for a script. Returns null if the script file doesn't exist
    /// or has an unsupported extension. Pure and side-effect free so it can be unit tested.
    /// </summary>
    internal static ProcessStartInfo? BuildStartInfo(
        string scriptPath,
        GameEntry game,
        string phase,
        bool hidden,
        bool elevated,
        long? playedMinutes)
    {
        string path = scriptPath.Trim().Trim('"');
        if (!IsSupportedScript(path) || !File.Exists(path)) return null;

        string ext = Path.GetExtension(path).ToLowerInvariant();
        string workDir = Path.GetDirectoryName(path) ?? string.Empty;
        // Argument 5 is always present so a script's positions never shift between phases:
        // empty on pre-launch (nothing has been played yet), the minute count on post-exit.
        string playtimeArg = playedMinutes?.ToString() ?? string.Empty;

        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workDir,
            // ShellExecute is required for the "runas" verb; otherwise CreateProcess gives us a
            // real environment block and a reliable CreateNoWindow.
            UseShellExecute = elevated,
            CreateNoWindow = hidden && !elevated,
            WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
        };

        if (elevated)
        {
            psi.Verb = "runas";
        }
        else if (hidden)
        {
            // A hidden script has nowhere to show its output; capture it into the log instead.
            // (A visible console keeps its output on screen where the user can read it.)
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
        }

        switch (ext)
        {
            case ".bat":
            case ".cmd":
                // cmd.exe re-parses its command line, so the runtime's ArgumentList quoting is
                // not enough: it only quotes an argument containing whitespace, and an unquoted
                // "&", "|" or ">" in the game name would become a command separator. Every
                // argument is force-quoted here and the whole thing wrapped for /s (strip the
                // outer quotes, keep the inner ones). Quotes can't appear in a Windows path, and
                // are replaced in the free-text arguments so they can't terminate a quoted span.
                // cmd also expands %NAME% before it parses quotes, so a game called "%TEMP%" would
                // reach the script as the temp folder - the display name has its percent signs
                // stripped (the exact value is still in TRAYTRIGGER_GAME_NAME).
                psi.FileName = "cmd.exe";
                psi.Arguments = "/d /s /c \"" + string.Join(' ',
                    new[] { path, phase, SanitizeForCmdLine(game.Name), game.ExecutablePath, game.Id, playtimeArg }.Select(a => "\"" + a.Replace('"', '\'') + "\""));
                // The game's own script arguments go in raw: a batch author writes cmd syntax and
                // expects cmd to parse it (%~6, quoted spans, even redirections). Their quoting is
                // their responsibility - the help page says an unbalanced quote will break parsing.
                if (!string.IsNullOrWhiteSpace(game.ScriptArguments))
                {
                    psi.Arguments += " " + game.ScriptArguments.Trim();
                }
                psi.Arguments += "\"";
                break;

            case ".ps1":
                psi.FileName = "powershell.exe";
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                if (hidden)
                {
                    psi.ArgumentList.Add("-WindowStyle");
                    psi.ArgumentList.Add("Hidden");
                }
                psi.ArgumentList.Add("-File");
                psi.ArgumentList.Add(path);
                break;

            case ".exe":
            case ".com":
                psi.FileName = path;
                break;

            default:
                // Unreachable: IsSupportedScript filtered this above. Keep the switch exhaustive.
                return null;
        }

        // Positional arguments work for every script type and every privilege level. (The
        // cmd.exe case already folded them into its pre-quoted Arguments string above -
        // ArgumentList and Arguments can't be mixed on one ProcessStartInfo.)
        if (ext is not (".bat" or ".cmd"))
        {
            psi.ArgumentList.Add(phase);
            psi.ArgumentList.Add(game.Name);
            psi.ArgumentList.Add(game.ExecutablePath);
            psi.ArgumentList.Add(game.Id);
            psi.ArgumentList.Add(playtimeArg);
            // PowerShell and executables receive discrete argv entries, so the free text is split
            // the way Windows itself would split a command line, then each token is re-quoted by
            // the runtime. "C:\My Saves" arrives as one argument.
            foreach (string token in SplitScriptArguments(game.ScriptArguments))
            {
                psi.ArgumentList.Add(token);
            }
        }

        if (!psi.UseShellExecute)
        {
            psi.Environment["TRAYTRIGGER_PHASE"] = phase;
            psi.Environment["TRAYTRIGGER_GAME_NAME"] = game.Name;
            psi.Environment["TRAYTRIGGER_GAME_ID"] = game.Id;
            psi.Environment["TRAYTRIGGER_GAME_EXE"] = game.ExecutablePath;
            if (playedMinutes.HasValue)
            {
                psi.Environment["TRAYTRIGGER_PLAYTIME_MINUTES"] = playedMinutes.Value.ToString();
            }
        }

        return psi;
    }

    /// <summary>Strips the one character cmd.exe expands before quote parsing ('%').</summary>
    internal static string SanitizeForCmdLine(string value) => value.Replace("%", string.Empty);

    /// <summary>
    /// Splits a game's free-text script arguments into argv tokens using the same rules Windows
    /// applies to a process command line (CommandLineToArgvW): whitespace separates, double quotes
    /// group, backslashes escape quotes. Empty input yields no tokens.
    /// </summary>
    internal static string[] SplitScriptArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return Array.Empty<string>();

        // The first token of a command line is parsed with program-name rules (no backslash
        // escaping), so a throwaway token is prepended and dropped from the result.
        IntPtr argv = CommandLineToArgvW("x " + arguments.Trim(), out int count);
        if (argv == IntPtr.Zero)
        {
            return arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        try
        {
            var tokens = new string[Math.Max(0, count - 1)];
            for (int i = 1; i < count; i++)
            {
                tokens[i - 1] = System.Runtime.InteropServices.Marshal.PtrToStringUni(
                    System.Runtime.InteropServices.Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? string.Empty;
            }
            return tokens;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
