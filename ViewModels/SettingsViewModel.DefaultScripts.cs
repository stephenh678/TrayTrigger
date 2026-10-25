using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.Views;

namespace TrayTrigger.ViewModels;

/// <summary>Settings > Launch & Performance: the default pre-launch and post-exit scripts.</summary>
public partial class SettingsViewModel
{
    // --- Default scripts (Settings > Launch & Performance) ---
    // Always read through _settings.ScriptDefaults: "Reset to defaults" swaps that object.

    /// <summary>"Run the default scripts": pauses the defaults without clearing anything.</summary>
    public bool DefaultScriptsEnabled
    {
        get => _settings.ScriptDefaults.Enabled;
        set
        {
            if (_settings.ScriptDefaults.Enabled != value)
            {
                _settings.ScriptDefaults.Enabled = value;
                OnPropertyChanged();
                AutoSaveSettings();
            }
        }
    }

    public string DefaultPreLaunchScriptPath
    {
        get => _settings.ScriptDefaults.PreLaunchScriptPath;
        set
        {
            string v = value ?? string.Empty;
            if (_settings.ScriptDefaults.PreLaunchScriptPath != v)
            {
                _settings.ScriptDefaults.PreLaunchScriptPath = v;
                OnPropertyChanged();
                NotifyDefaultScriptsChanged();
                AutoSaveSettings();
                // Typing, Browse and "New script..." all land here, so the post-exit box follows along.
                if (DefaultUseSameScriptForBoth) DefaultPostExitScriptPath = v;
            }
        }
    }

    public string DefaultPostExitScriptPath
    {
        get => _settings.ScriptDefaults.PostExitScriptPath;
        set
        {
            string v = value ?? string.Empty;
            if (_settings.ScriptDefaults.PostExitScriptPath != v)
            {
                _settings.ScriptDefaults.PostExitScriptPath = v;
                OnPropertyChanged();
                NotifyDefaultScriptsChanged();
                AutoSaveSettings();
            }
        }
    }

    /// <summary>Script Arguments for the defaults, used for any game whose own are blank.</summary>
    public string DefaultScriptArguments
    {
        get => _settings.ScriptDefaults.ScriptArguments;
        set
        {
            string v = value ?? string.Empty;
            if (_settings.ScriptDefaults.ScriptArguments != v)
            {
                _settings.ScriptDefaults.ScriptArguments = v;
                OnPropertyChanged();
                AutoSaveSettings();
            }
        }
    }

    public bool HasDefaultPreLaunchScript => _settings.ScriptDefaults.HasPreLaunchScript;
    public bool HasDefaultPostExitScript => _settings.ScriptDefaults.HasPostExitScript;
    public bool HasAnyDefaultScript => _settings.ScriptDefaults.HasAny;

    private bool? _defaultUseSameScriptForBoth;

    /// <summary>
    /// Edit Game's "use the same script for pre-launch and post-exit", for the defaults: while on,
    /// the post-exit path mirrors the pre-launch one and its box is read-only. Nothing is stored -
    /// it is simply on when both defaults already point at the same file.
    /// </summary>
    public bool DefaultUseSameScriptForBoth
    {
        get => _defaultUseSameScriptForBoth ??=
            !string.IsNullOrWhiteSpace(DefaultPreLaunchScriptPath)
            && string.Equals(DefaultPreLaunchScriptPath.Trim(), DefaultPostExitScriptPath?.Trim(), StringComparison.OrdinalIgnoreCase);
        set
        {
            if (DefaultUseSameScriptForBoth == value) return;
            _defaultUseSameScriptForBoth = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanEditDefaultPostExitScript));
            if (value) DefaultPostExitScriptPath = DefaultPreLaunchScriptPath;
        }
    }

    /// <summary>The default post-exit box is read-only while it mirrors the pre-launch one.</summary>
    public bool CanEditDefaultPostExitScript => !DefaultUseSameScriptForBoth;

    /// <summary>A typed default path that won't run as-is (wrong type or missing file), or null.</summary>
    public string? DefaultScriptsProblem
    {
        get
        {
            return Describe("pre-launch", _settings.ScriptDefaults.PreLaunchScriptPath)
                ?? Describe("post-exit", _settings.ScriptDefaults.PostExitScriptPath);

            static string? Describe(string which, string path)
            {
                string p = path.Trim().Trim('"');
                if (p.Length == 0) return null;
                if (!GameScriptService.IsSupportedScript(p))
                    return $"The default {which} script has an unsupported file type and will be skipped. Supported: {string.Join(", ", GameScriptService.SupportedExtensions)}.";
                if (!File.Exists(p))
                    return $"The default {which} script file was not found and will be skipped: {p}";
                return null;
            }
        }
    }
    public bool HasDefaultScriptsProblem => DefaultScriptsProblem != null;

    public bool DefaultWaitForPreLaunchScript
    {
        get => _settings.ScriptDefaults.WaitForPreLaunchScript;
        set
        {
            if (_settings.ScriptDefaults.WaitForPreLaunchScript != value)
            {
                _settings.ScriptDefaults.WaitForPreLaunchScript = value;
                OnPropertyChanged();
                AutoSaveSettings();
            }
        }
    }

    /// <summary>Text-bound; only a value inside the supported range is stored.</summary>
    public string DefaultPreLaunchScriptTimeoutSeconds
    {
        get => _settings.ScriptDefaults.PreLaunchScriptTimeoutSeconds.ToString();
        set
        {
            if (int.TryParse(value?.Trim(), out int seconds)
                && seconds >= GameScriptService.MinPreLaunchTimeoutSeconds
                && seconds <= GameScriptService.MaxPreLaunchTimeoutSeconds
                && _settings.ScriptDefaults.PreLaunchScriptTimeoutSeconds != seconds)
            {
                _settings.ScriptDefaults.PreLaunchScriptTimeoutSeconds = seconds;
                AutoSaveSettings();
            }
            OnPropertyChanged();
        }
    }

    public string DefaultPreLaunchTimeoutHint => $"Seconds to wait ({GameScriptService.MinPreLaunchTimeoutSeconds}-{GameScriptService.MaxPreLaunchTimeoutSeconds}); default {GameScriptService.DefaultPreLaunchWaitTimeout.TotalSeconds:0}.";

    public bool DefaultAbortLaunchOnScriptFailure
    {
        get => _settings.ScriptDefaults.AbortLaunchOnScriptFailure;
        set
        {
            if (_settings.ScriptDefaults.AbortLaunchOnScriptFailure != value)
            {
                _settings.ScriptDefaults.AbortLaunchOnScriptFailure = value;
                OnPropertyChanged();
                if (value) DefaultWaitForPreLaunchScript = true;
                AutoSaveSettings();
            }
        }
    }

    public bool DefaultRunScriptsHidden
    {
        get => _settings.ScriptDefaults.RunScriptsHidden;
        set
        {
            if (_settings.ScriptDefaults.RunScriptsHidden != value)
            {
                _settings.ScriptDefaults.RunScriptsHidden = value;
                OnPropertyChanged();
                AutoSaveSettings();
            }
        }
    }

    public bool DefaultRunScriptsAsAdmin
    {
        get => _settings.ScriptDefaults.RunScriptsAsAdmin;
        set
        {
            if (_settings.ScriptDefaults.RunScriptsAsAdmin != value)
            {
                _settings.ScriptDefaults.RunScriptsAsAdmin = value;
                OnPropertyChanged();
                AutoSaveSettings();
            }
        }
    }

    private bool _isTestingDefaultScript;
    public bool IsTestingDefaultScript
    {
        get => _isTestingDefaultScript;
        private set { _isTestingDefaultScript = value; OnPropertyChanged(); }
    }

    private void NotifyDefaultScriptsChanged()
    {
        OnPropertyChanged(nameof(DefaultScriptsEnabled));
        OnPropertyChanged(nameof(DefaultPreLaunchScriptPath));
        OnPropertyChanged(nameof(DefaultPostExitScriptPath));
        OnPropertyChanged(nameof(DefaultScriptArguments));
        OnPropertyChanged(nameof(HasDefaultPreLaunchScript));
        OnPropertyChanged(nameof(HasDefaultPostExitScript));
        OnPropertyChanged(nameof(HasAnyDefaultScript));
        OnPropertyChanged(nameof(DefaultScriptsProblem));
        OnPropertyChanged(nameof(HasDefaultScriptsProblem));
        OnPropertyChanged(nameof(DefaultWaitForPreLaunchScript));
        OnPropertyChanged(nameof(DefaultPreLaunchScriptTimeoutSeconds));
        OnPropertyChanged(nameof(DefaultAbortLaunchOnScriptFailure));
        OnPropertyChanged(nameof(DefaultRunScriptsHidden));
        OnPropertyChanged(nameof(DefaultRunScriptsAsAdmin));
        OnPropertyChanged(nameof(DefaultUseSameScriptForBoth));
        OnPropertyChanged(nameof(CanEditDefaultPostExitScript));
    }

    /// <summary>
    /// "New script...": a save dialog in the scripts folder, then the blank template matching the
    /// chosen extension is written there, the path box filled, and the file opened for editing.
    /// An existing file is never overwritten - it is simply used as-is. Mirrors Edit Game.
    /// </summary>
    private void NewDefaultScript(bool isPreLaunch)
    {
        string phase = isPreLaunch ? "PreLaunch" : "PostExit";
        var dialog = new SaveFileDialog
        {
            Title = isPreLaunch ? "New Default Pre-Launch Script" : "New Default Post-Exit Script",
            Filter = "PowerShell script (*.ps1)|*.ps1",
            DefaultExt = ".ps1",
            AddExtension = true,
            OverwritePrompt = false,
            FileName = $"Default-{phase}.ps1",
            InitialDirectory = InitialDefaultScriptDirectory(string.Empty)
        };

        if (FileDialogCloak.Show(dialog) != true || string.IsNullOrWhiteSpace(dialog.FileName)) return;

        string path = ScriptLibraryService.AsPowerShellScriptPath(dialog.FileName);
        try
        {
            bool created = ScriptLibraryService.CreateFromBlankTemplate(path);
            StatusMessage = created
                ? $"Created {Path.GetFileName(path)} from the blank template."
                : $"{Path.GetFileName(path)} already exists and was left untouched.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not create the script: {ex.Message}";
            return;
        }

        if (isPreLaunch) DefaultPreLaunchScriptPath = path;
        else DefaultPostExitScriptPath = path;
        ScriptLibraryService.OpenInEditor(path);
    }

    private void BrowseDefaultScript(bool isPreLaunch)
    {
        var dialog = new OpenFileDialog
        {
            Title = isPreLaunch ? "Select Default Pre-Launch Script" : "Select Default Post-Exit Script",
            Filter = $"Scripts & Programs ({GameScriptService.SupportedExtensionsFilterPattern})|{GameScriptService.SupportedExtensionsFilterPattern}",
            CheckFileExists = true,
            InitialDirectory = InitialDefaultScriptDirectory(isPreLaunch ? DefaultPreLaunchScriptPath : DefaultPostExitScriptPath)
        };
        if (FileDialogCloak.Show(dialog) == true)
        {
            if (isPreLaunch) DefaultPreLaunchScriptPath = dialog.FileName;
            else DefaultPostExitScriptPath = dialog.FileName;
        }
    }

    /// <summary>The current default's folder if it has one, otherwise the scripts folder (populated on demand).</summary>
    private string InitialDefaultScriptDirectory(string currentPath)
    {
        string p = currentPath.Trim().Trim('"');
        if (p.Length > 0)
        {
            string? dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
        }
        ScriptLibrary.EnsureInstalled();
        return ScriptLibrary.ScriptsDirectory;
    }

    /// <summary>
    /// Test Run for a default script. There is no real game here, so the script receives
    /// placeholder values; the result dialog says so.
    /// </summary>
    private async Task TestDefaultScriptAsync(bool isPreLaunch)
    {
        string path = (isPreLaunch ? DefaultPreLaunchScriptPath : DefaultPostExitScriptPath).Trim();
        if (string.IsNullOrWhiteSpace(path) || IsTestingDefaultScript) return;

        const string probeName = "Default Script Test";
        // The default Script Arguments, as a game with none of its own would pass them.
        string arguments = DefaultScriptArguments.Trim();
        var probe = new GameEntry { Id = "default", Name = probeName, ExecutablePath = string.Empty, ScriptArguments = arguments };
        string phase = isPreLaunch ? GameScriptService.PhasePreLaunch : GameScriptService.PhasePostExit;
        long? playtime = isPreLaunch ? null : 0;

        IsTestingDefaultScript = true;
        StatusMessage = $"Testing the default {(isPreLaunch ? "pre-launch" : "post-exit")} script (up to {GameScriptService.TestRunTimeout.TotalSeconds:0} s)...";
        try
        {
            var result = await Task.Run(() => GameScriptService.TestRun(path, probe, phase, playtime));
            StatusMessage = string.Empty;
            var report = new ScriptTestReport(isPreLaunch, path, result, DefaultRunScriptsAsAdmin, DefaultRunScriptsHidden,
                ProbeDescription: $"placeholder values (name \"{probeName}\", game ID \"default\", empty exe) and " +
                    (arguments.Length == 0 ? "no script arguments" : $"the default script arguments \"{arguments}\""));
            new ScriptTestResultDialog(report).ShowDialog();
        }
        finally
        {
            IsTestingDefaultScript = false;
        }
    }

    /// <summary>Sessions the status-bar hotkey hint is shown for, counting this one.</summary>
    public const int HotkeyHintSessions = 5;

    /// <summary>The Library status bar's hotkey reminder: only for the first few sessions, and only
    /// when a window hotkey is set. After that the tray tooltip carries it.</summary>
    public bool ShowHotkeyHint => _settings.SessionsStarted <= HotkeyHintSessions && !string.IsNullOrWhiteSpace(GlobalManageHotkey);

    public string GlobalManageHotkey
    {
        get => _settings.GlobalManageHotkey;
        set
        {
            if (_settings.GlobalManageHotkey != value)
            {
                _settings.GlobalManageHotkey = value;
                OnPropertyChanged(nameof(ShowHotkeyHint));
                OnPropertyChanged();
                AutoSaveSettings();
                _onHotkeySettingChanged?.Invoke();
            }
        }
    }

    public string TrayMenuHotkey
    {
        get => _settings.TrayMenuHotkey;
        set
        {
            if (_settings.TrayMenuHotkey != value)
            {
                _settings.TrayMenuHotkey = value;
                OnPropertyChanged();
                AutoSaveSettings();
                _onHotkeySettingChanged?.Invoke();
            }
        }
    }

    public string SuspendGameHotkey
    {
        get => _settings.SuspendGameHotkey;
        set
        {
            if (_settings.SuspendGameHotkey != value)
            {
                _settings.SuspendGameHotkey = value;
                OnPropertyChanged();
                AutoSaveSettings();
                _onHotkeySettingChanged?.Invoke();
            }
        }
    }
}
