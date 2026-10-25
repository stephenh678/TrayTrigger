using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

/// <summary>Edit Game: the pre-launch and post-exit scripts, Test Run, and the Settings defaults as they apply to this game.</summary>
public partial class GameEditViewModel
{
    // --- Test Run ---

    private bool _isTestingScript;

    /// <summary>A Test Run is in progress; both Test buttons stay disabled until it finishes.</summary>
    public bool IsTestingScript
    {
        get => _isTestingScript;
        private set { _isTestingScript = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsBusy)); }
    }

    /// <summary>Something is running that the footer's busy bar stands for: a look-up or a script test.</summary>
    public bool IsBusy => IsRefreshingMetadata || IsTestingScript;

    /// <summary>Raised on the UI thread when a Test Run finishes; the view shows the report.</summary>
    public event Action<ScriptTestReport>? ScriptTestCompleted;

    private async Task TestScriptAsync(bool isPreLaunch)
    {
        string path = (isPreLaunch ? PreLaunchScriptPath : PostExitScriptPath).Trim();
        if (string.IsNullOrWhiteSpace(path) || IsTestingScript) return;

        // The probe is built from what is typed right now, not from the saved record: the user
        // is testing the path (and name/exe) they can see. Only the ID comes from the saved game.
        var probe = new GameEntry
        {
            Id = SourceGame.Id,
            Name = string.IsNullOrWhiteSpace(Name) ? "Unnamed Game" : Name.Trim(),
            ExecutablePath = ExecutablePath.Trim(),
            ScriptArguments = ScriptArguments?.Trim() ?? string.Empty
        };
        string phase = isPreLaunch ? GameScriptService.PhasePreLaunch : GameScriptService.PhasePostExit;
        long? playtime = isPreLaunch ? null : 0;

        IsTestingScript = true;
        StatusMessage = $"Testing the {(isPreLaunch ? "pre-launch" : "post-exit")} script (up to {GameScriptService.TestRunTimeout.TotalSeconds:0} s)...";
        try
        {
            var result = await Task.Run(() => GameScriptService.TestRun(path, probe, phase, playtime));
            StatusMessage = null;
            ScriptTestCompleted?.Invoke(new ScriptTestReport(isPreLaunch, path, result, RunScriptsAsAdmin, RunScriptsHidden));
        }
        finally
        {
            IsTestingScript = false;
        }
    }

    /// <summary>Gates the hidden/admin options, which apply to whichever scripts are set.</summary>
    public bool HasAnyScript => HasPreLaunchScript || !string.IsNullOrWhiteSpace(_postExitScriptPath);

    public bool WaitForPreLaunchScript
    {
        get => _waitForPreLaunchScript;
        set { _waitForPreLaunchScript = value; OnPropertyChanged(); }
    }

    public bool RunScriptsHidden
    {
        get => _runScriptsHidden;
        set { _runScriptsHidden = value; OnPropertyChanged(); }
    }

    public bool RunScriptsAsAdmin
    {
        get => _runScriptsAsAdmin;
        set { _runScriptsAsAdmin = value; OnPropertyChanged(); }
    }

    /// <summary>Free-text arguments appended after the positional ones for both scripts. See <see cref="GameEntry.ScriptArguments"/>.</summary>
    public string ScriptArguments
    {
        get => _scriptArguments;
        set { _scriptArguments = value; OnPropertyChanged(); OnPropertyChanged(nameof(DefaultScriptsSummary)); }
    }

    /// <summary>
    /// Script Arguments mean something whenever a script runs for this game: its own, or a
    /// Settings default it hasn't opted out of. Gating on its own scripts only left a game that
    /// relies on the defaults with no way to pass them anything.
    /// </summary>
    public bool CanEditScriptArguments => HasAnyScript || DefaultsRunForThisGame;

    private bool DefaultsRunForThisGame => _scriptDefaults is { Enabled: true, HasAny: true } && !SkipDefaultScripts;

    // --- Settings default scripts, as they apply to this game ---

    /// <summary>Settings has at least one default script, so the summary and opt-out are shown.</summary>
    public bool HasDefaultScripts => _scriptDefaults?.HasAny == true;

    /// <summary>"Don't run the default scripts for this game". See <see cref="GameEntry.SkipDefaultScripts"/>.</summary>
    public bool SkipDefaultScripts
    {
        get => _skipDefaultScripts;
        set { _skipDefaultScripts = value; OnPropertyChanged(); OnPropertyChanged(nameof(DefaultScriptsSummary)); OnPropertyChanged(nameof(CanEditScriptArguments)); }
    }

    /// <summary>
    /// One line per configured default, saying whether it applies to this game given what is
    /// typed in the path boxes right now. Mirrors <see cref="GameScriptService.ResolvePreLaunch"/>.
    /// </summary>
    public string DefaultScriptsSummary
    {
        get
        {
            if (_scriptDefaults == null) return string.Empty;
            if (!_scriptDefaults.Enabled)
            {
                return "Default scripts are configured but not turned on in Settings (\"Run the default scripts\" is unticked), so none apply to this game.";
            }
            var lines = new List<string>();
            if (_scriptDefaults.HasPreLaunchScript)
            {
                lines.Add(DescribeDefault("pre-launch", _scriptDefaults.PreLaunchScriptPath, HasPreLaunchScript));
            }
            if (_scriptDefaults.HasPostExitScript)
            {
                lines.Add(DescribeDefault("post-exit", _scriptDefaults.PostExitScriptPath, HasPostExitScript));
            }
            bool aDefaultRuns = !SkipDefaultScripts
                && ((_scriptDefaults.HasPreLaunchScript && !HasPreLaunchScript) || (_scriptDefaults.HasPostExitScript && !HasPostExitScript));
            if (aDefaultRuns)
            {
                // Mirrors GameScriptService.DefaultScriptArguments, for what is typed right now.
                lines.Add(!string.IsNullOrWhiteSpace(ScriptArguments)
                    ? "The default scripts get this game's Script Arguments."
                    : string.IsNullOrWhiteSpace(_scriptDefaults.ScriptArguments)
                        ? "The default scripts get no Script Arguments."
                        : $"The default scripts get the Default Script Arguments from Settings: {_scriptDefaults.ScriptArguments.Trim()}");
            }
            return string.Join("\n", lines);

            string DescribeDefault(string phase, string path, bool gameHasOwn)
            {
                string name = Path.GetFileName(path.Trim().Trim('"'));
                string state = SkipDefaultScripts ? "skipped for this game."
                    : gameHasOwn ? $"not used, this game has its own {phase} script."
                    : $"runs for this game because it has no {phase} script of its own.";
                return $"Default {phase} script {name}: {state}";
            }
        }
    }

    /// <summary>Scripts are configured on this game but the Settings switch is off, so they won't run.</summary>
    public bool ShowScriptsDisabledNotice { get; }

    /// <summary>Cancel the launch when the pre-launch script fails/times out. Implies waiting for it.</summary>
    public bool AbortLaunchOnScriptFailure
    {
        get => _abortLaunchOnScriptFailure;
        set
        {
            _abortLaunchOnScriptFailure = value;
            if (value) WaitForPreLaunchScript = true;
            OnPropertyChanged();
        }
    }

    /// <summary>Bound as text so a half-typed number doesn't fight the binding; validated on save.</summary>
    public string PreLaunchScriptTimeoutSeconds
    {
        get => _preLaunchScriptTimeoutSeconds;
        set { _preLaunchScriptTimeoutSeconds = value; OnPropertyChanged(); ClearFieldError(EditField.PreLaunchTimeout); }
    }

    public string PreLaunchTimeoutHint => $"Seconds to wait ({GameScriptService.MinPreLaunchTimeoutSeconds}-{GameScriptService.MaxPreLaunchTimeoutSeconds}); default {GameScriptService.DefaultPreLaunchWaitTimeout.TotalSeconds:0}.";

    /// <summary>Close the platform client (Steam, Galaxy, EA App, Epic, Ubisoft Connect) once this game's session ends.</summary>
    public bool CloseLauncherOnExit
    {
        get => _closeLauncherOnExit;
        set { _closeLauncherOnExit = value; OnPropertyChanged(); }
    }

    /// <summary>Explains where the Arguments box goes for a Steam-by-AppId game, since Steam
    /// launches can't take a command line the way an exe does.</summary>
    public string ArgumentsHint => IsSteamGame && !string.IsNullOrWhiteSpace(SteamAppId)
        ? "For a Steam game these are sent as Steam launch options (steam://run/<id>//<args>/), the same as Properties > Launch Options in Steam."
        : "Passed to the executable as-is.";

    /// <summary>Launch through the Steam client (steam://rungameid/) rather than the executable.
    /// Offered inside the Steam App ID box only while the entry has no launcher platform; once
    /// on, the entry is a Steam-linked game and the launcher box (with "launch this executable
    /// directly") takes over - so exactly one launch-route control is ever on screen.</summary>
    public bool IsSteamGame
    {
        get => _isSteamGame;
        set
        {
            _isSteamGame = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ArgumentsHint));
            OnPropertyChanged(nameof(HasPlatform));
            OnPropertyChanged(nameof(PlatformDescription));
            OnPropertyChanged(nameof(LaunchDirectlyLabel));
            OnPropertyChanged(nameof(LaunchDirectlyHint));
            OnPropertyChanged(nameof(LinkTargetHint));
            OnPropertyChanged(nameof(CanOfferSteamLaunch));
            OnPropertyChanged(nameof(CanOfferSteamBadge));
        }
    }

    /// <summary>Shows the "Launch through the Steam client" option: an entry with no launcher tag
    /// (or one converted to Local in this session) that has a Steam App ID to launch by.</summary>
    public bool CanOfferSteamLaunch => !HasPlatform && !string.IsNullOrWhiteSpace(SteamAppId);

    /// <summary>"Use the Steam badge instead of the Local badge" only matters for a game that does not launch through Steam.</summary>
    public bool CanOfferSteamBadge => !_isSteamGame;

    private static string? ValidateScriptPath(string? rawPath, string label)
    {
        string path = rawPath?.Trim().Trim('"') ?? string.Empty;
        if (path.Length == 0) return null;

        if (!GameScriptService.IsSupportedScript(path))
        {
            return $"{label} script must be one of: {string.Join(", ", GameScriptService.SupportedExtensions)}. Start other file types from a one-line .ps1.";
        }

        if (!File.Exists(path))
        {
            return $"{label} script not found: {path}";
        }

        return null;
    }

    private void BrowseScript(bool isPreLaunch)
    {
        var dialog = new OpenFileDialog
        {
            Title = isPreLaunch ? "Select Pre-Launch Script" : "Select Post-Exit Script",
            Filter = $"Scripts & Programs ({GameScriptService.SupportedExtensionsFilterPattern})|{GameScriptService.SupportedExtensionsFilterPattern}",
            CheckFileExists = true,
            InitialDirectory = InitialScriptDirectory(isPreLaunch ? PreLaunchScriptPath : PostExitScriptPath)
        };

        if (FileDialogCloak.Show(dialog) == true)
        {
            if (isPreLaunch) PreLaunchScriptPath = dialog.FileName;
            else PostExitScriptPath = dialog.FileName;
        }
    }

    /// <summary>The current script's folder if it has one, otherwise the scripts folder (populated on demand).</summary>
    private string InitialScriptDirectory(string currentPath)
    {
        string p = currentPath.Trim().Trim('"');
        if (p.Length > 0)
        {
            string? dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
        }
        if (_scriptLibrary == null) return string.Empty;
        _scriptLibrary.EnsureInstalled();
        return _scriptLibrary.ScriptsDirectory;
    }

    /// <summary>
    /// "New script...": a save dialog in the scripts folder, then the blank template matching the
    /// chosen extension is written there, the path box filled, and the file opened for editing.
    /// An existing file is never overwritten - it is simply used as-is.
    /// </summary>
    private void NewScript(bool isPreLaunch)
    {
        string phase = isPreLaunch ? "PreLaunch" : "PostExit";
        var dialog = new SaveFileDialog
        {
            Title = isPreLaunch ? "New Pre-Launch Script" : "New Post-Exit Script",
            Filter = "PowerShell script (*.ps1)|*.ps1",
            DefaultExt = ".ps1",
            AddExtension = true,
            OverwritePrompt = false,
            FileName = $"{SafeFileStem(Name)}-{phase}.ps1",
            InitialDirectory = InitialScriptDirectory(string.Empty)
        };

        if (FileDialogCloak.Show(dialog) != true || string.IsNullOrWhiteSpace(dialog.FileName)) return;

        string path = ScriptLibraryService.AsPowerShellScriptPath(dialog.FileName);
        try
        {
            bool created = ScriptLibraryService.CreateFromBlankTemplate(path);
            StatusMessage = created
                ? $"Created {Path.GetFileName(path)} from the blank template. Edit it, then Save."
                : $"{Path.GetFileName(path)} already exists and was left untouched.";
        }
        catch (Exception ex)
        {
            ShowProblem($"Could not create the script: {ex.Message}");
            return;
        }

        if (isPreLaunch) PreLaunchScriptPath = path;
        else PostExitScriptPath = path;
        ScriptLibraryService.OpenInEditor(path);
    }

    private static string SafeFileStem(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string stem = new string((name ?? string.Empty).Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return stem.Length == 0 ? "Game" : stem;
    }
}
