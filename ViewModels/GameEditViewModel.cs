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

public class GameEditViewModel : ViewModelBase
{
    private readonly IconExtractorService _iconExtractorService;
    private readonly string? _steamGridDbApiKey;
    private readonly double _minConfidence;
    private bool _isRefreshingMetadata;
    private string _name;
    private string _executablePath;
    private string _arguments;
    private string _workingDirectory;
    private bool _runAsAdmin;
    private string _category;
    private string _hotkey;
    private bool _isSteamGame;
    private bool _forceSteamOverlayTag;
    private string? _steamAppId;
    private PerformanceProfileMode _performanceProfile;
    private string _preLaunchScriptPath;
    private string _postExitScriptPath;
    private bool _waitForPreLaunchScript;
    private bool _runScriptsHidden;
    private bool _runScriptsAsAdmin;
    private string _scriptArguments;
    private bool _skipDefaultScripts;
    private readonly ScriptDefaults? _scriptDefaults;
    private readonly ScriptLibraryService? _scriptLibrary;
    private bool _abortLaunchOnScriptFailure;
    private string _preLaunchScriptTimeoutSeconds = "30";
    private bool _closeLauncherOnExit;
    private bool _isHidden;
    private string? _customIconPath;
    private BitmapImage? _iconPreview;
    private string? _customCoverPath;
    private string? _fetchedCoverPath;
    private BitmapImage? _coverPreview;
    private string? _statusMessage;

    public string? StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (_statusMessage != value)
            {
                _statusMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasStatusMessage));
            }
        }
    }

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(_statusMessage);

    public GameEntry SourceGame { get; }
    public bool IsNewGame { get; }

    public ObservableCollection<string> ExistingCategories { get; } = new();

    public event Action<bool>? RequestClose;

    public GameEditViewModel(
        GameEntry game, 
        IEnumerable<string> categories, 
        IconExtractorService iconExtractorService, 
        bool isNewGame = false,
        string? steamGridDbApiKey = null,
        double minConfidence = SteamSearchService.DefaultMinConfidence,
        bool scriptsEnabled = false,
        ScriptDefaults? scriptDefaults = null,
        ScriptLibraryService? scriptLibrary = null)
    {
        SourceGame = game;
        _scriptDefaults = scriptDefaults;
        _scriptLibrary = scriptLibrary;
        _skipDefaultScripts = game.SkipDefaultScripts;
        // The card is opt-in (Settings > General), but a game that already has a script must
        // stay editable even if the setting was later turned off or reset.
        ShowScriptsCard = scriptsEnabled || game.HasScripts;
        // ...and if it has scripts while the feature is off, those scripts are NOT running
        // (GameScriptService treats the setting as a kill-switch) - say so instead of leaving a
        // configured-looking card that silently does nothing.
        ShowScriptsDisabledNotice = !scriptsEnabled && game.HasScripts;
        _abortLaunchOnScriptFailure = game.AbortLaunchOnScriptFailure;
        _preLaunchScriptTimeoutSeconds = game.PreLaunchScriptTimeoutSeconds.ToString();
        _closeLauncherOnExit = game.CloseLauncherOnExit;
        _iconExtractorService = iconExtractorService;
        _steamGridDbApiKey = steamGridDbApiKey;
        _minConfidence = minConfidence;
        IsNewGame = isNewGame;

        _name = game.Name;
        _executablePath = game.ExecutablePath;
        _arguments = game.Arguments;
        _workingDirectory = game.WorkingDirectory;
        _runAsAdmin = game.RunAsAdmin;
        _category = string.IsNullOrWhiteSpace(game.Category) ? LibraryConstants.Uncategorized : game.Category;
        _hotkey = game.Hotkey;
        _isSteamGame = game.IsSteamGame;
        _forceSteamOverlayTag = game.ForceSteamOverlayTag;
        _steamAppId = game.SteamAppId;
        _launchDirectly = game.LaunchDirectly;
        _hasPlatform = game.IsGogGame || game.IsEaGame || game.IsEpicGame || game.IsUbisoftGame || game.IsXboxGame || (game.IsSteamGame && !string.IsNullOrEmpty(game.SteamAppId));
        _performanceProfile = game.PerformanceProfile;
        _cpuAffinity = game.CpuAffinity;
        _preLaunchScriptPath = game.PreLaunchScriptPath;
        _postExitScriptPath = game.PostExitScriptPath;
        _waitForPreLaunchScript = game.WaitForPreLaunchScript;
        _runScriptsHidden = game.RunScriptsHidden;
        _runScriptsAsAdmin = game.RunScriptsAsAdmin;
        _scriptArguments = game.ScriptArguments;
        _isHidden = game.IsHidden;
        _customIconPath = game.IconPath;
        _customCoverPath = game.CoverImagePath;

        foreach (var cat in categories.Where(c => c != LibraryConstants.AllCategory).Distinct())
        {
            ExistingCategories.Add(cat);
        }
        if (!ExistingCategories.Contains(LibraryConstants.Uncategorized))
        {
            ExistingCategories.Insert(0, LibraryConstants.Uncategorized);
        }

        BrowseExeCommand = new RelayCommand(BrowseExe);
        BrowseWorkDirCommand = new RelayCommand(BrowseWorkDir);
        BrowsePreLaunchScriptCommand = new RelayCommand(() => BrowseScript(isPreLaunch: true));
        BrowsePostExitScriptCommand = new RelayCommand(() => BrowseScript(isPreLaunch: false));
        TestPreLaunchScriptCommand = new AsyncRelayCommand(() => TestScriptAsync(isPreLaunch: true), () => !IsTestingScript && HasPreLaunchScript);
        TestPostExitScriptCommand = new AsyncRelayCommand(() => TestScriptAsync(isPreLaunch: false), () => !IsTestingScript && HasPostExitScript);
        NewPreLaunchScriptCommand = new RelayCommand(() => NewScript(isPreLaunch: true));
        NewPostExitScriptCommand = new RelayCommand(() => NewScript(isPreLaunch: false));
        EditPreLaunchScriptCommand = new RelayCommand(() => ScriptLibraryService.OpenInEditor(PreLaunchScriptPath), () => HasPreLaunchScript);
        EditPostExitScriptCommand = new RelayCommand(() => ScriptLibraryService.OpenInEditor(PostExitScriptPath), () => HasPostExitScript);
        OpenScriptsFolderCommand = new RelayCommand(() => _scriptLibrary?.OpenFolder(), () => _scriptLibrary != null);
        BrowseIconCommand = new RelayCommand(BrowseIcon);
        ResetIconCommand = new RelayCommand(ResetIcon);
        BrowseCoverCommand = new RelayCommand(BrowseCover);
        ResetCoverCommand = new RelayCommand(ResetCover);
        LookUpCommand = new RelayCommand(LookUp, () => !IsRefreshingMetadata);
        FetchBySteamIdCommand = new RelayCommand(FetchBySteamId, () => !IsRefreshingMetadata);
        RefreshPosterCommand = new RelayCommand(RefreshPoster, () => !IsRefreshingMetadata);
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(Cancel);

        UpdateIconPreview();
        UpdateCoverPreview();
    }

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    public string ExecutablePath
    {
        get => _executablePath;
        set { _executablePath = value; OnPropertyChanged(); }
    }

    public string Arguments
    {
        get => _arguments;
        set { _arguments = value; OnPropertyChanged(); }
    }

    public string WorkingDirectory
    {
        get => _workingDirectory;
        set { _workingDirectory = value; OnPropertyChanged(); }
    }

    public bool RunAsAdmin
    {
        get => _runAsAdmin;
        set { _runAsAdmin = value; OnPropertyChanged(); }
    }

    public bool IsHidden
    {
        get => _isHidden;
        set { _isHidden = value; OnPropertyChanged(); }
    }

    public string Category
    {
        get => _category;
        set { _category = value; OnPropertyChanged(); }
    }

    public string Hotkey
    {
        get => _hotkey;
        set { _hotkey = value; OnPropertyChanged(); }
    }

    public PerformanceProfileMode PerformanceProfile
    {
        get => _performanceProfile;
        set { _performanceProfile = value; OnPropertyChanged(); }
    }

    public IReadOnlyList<PerformanceProfileMode> PerformanceProfileOptions { get; } =
        new[] { PerformanceProfileMode.Off, PerformanceProfileMode.Optimized, PerformanceProfileMode.Aggressive };

    // --- CPU affinity (hybrid CPUs) ---

    private CpuAffinityMode _cpuAffinity;
    public CpuAffinityMode CpuAffinity
    {
        get => _cpuAffinity;
        set { _cpuAffinity = value; OnPropertyChanged(); }
    }

    public sealed record CpuAffinityOption(CpuAffinityMode Value, string Label);

    public IReadOnlyList<CpuAffinityOption> CpuAffinityOptions { get; } = new[]
    {
        new CpuAffinityOption(CpuAffinityMode.Default, "Default (all cores)"),
        new CpuAffinityOption(CpuAffinityMode.PerformanceCoresOnly, "Performance cores only (hybrid CPUs)")
    };

    /// <summary>Tells the user whether the option can do anything on this machine.</summary>
    public string CpuAffinityHint
    {
        get
        {
            var topology = CpuTopologyService.GetTopology();
            return topology.IsHybrid
                ? $"This CPU is hybrid: {topology.PerformanceCoreCount} performance cores of {topology.LogicalProcessorCount} logical processors. Pinning helps older engines and some anti-cheat titles that stutter when threads land on efficiency cores."
                : "This CPU is not hybrid (no separate efficiency cores), so this option has no effect here. Kept per game so a library moved to a hybrid machine picks it up.";
        }
    }

    // --- Pre-launch / post-exit scripts ---

    public bool ShowScriptsCard { get; }

    /// <summary>The one-line "scripts are hidden" stub shown in place of the scripts card, so
    /// the feature stays discoverable from Edit Game while the Settings switch is off.</summary>
    public bool ShowScriptsStub => !ShowScriptsCard;

    public string PreLaunchScriptPath
    {
        get => _preLaunchScriptPath;
        set
        {
            _preLaunchScriptPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPreLaunchScript));
            OnPropertyChanged(nameof(HasAnyScript));
            OnPropertyChanged(nameof(DefaultScriptsSummary));
        }
    }

    public string PostExitScriptPath
    {
        get => _postExitScriptPath;
        set
        {
            _postExitScriptPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPostExitScript));
            OnPropertyChanged(nameof(HasAnyScript));
            OnPropertyChanged(nameof(DefaultScriptsSummary));
        }
    }

    /// <summary>Gates the "wait for pre-launch script" option, which only means something with a pre-launch script set.</summary>
    public bool HasPreLaunchScript => !string.IsNullOrWhiteSpace(_preLaunchScriptPath);

    public bool HasPostExitScript => !string.IsNullOrWhiteSpace(_postExitScriptPath);

    // --- Test Run ---

    private bool _isTestingScript;

    /// <summary>A Test Run is in progress; both Test buttons stay disabled until it finishes.</summary>
    public bool IsTestingScript
    {
        get => _isTestingScript;
        private set { _isTestingScript = value; OnPropertyChanged(); }
    }

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
        set { _scriptArguments = value; OnPropertyChanged(); }
    }

    // --- Settings default scripts, as they apply to this game ---

    /// <summary>Settings has at least one default script, so the summary and opt-out are shown.</summary>
    public bool HasDefaultScripts => _scriptDefaults?.HasAny == true;

    /// <summary>"Don't run the default scripts for this game". See <see cref="GameEntry.SkipDefaultScripts"/>.</summary>
    public bool SkipDefaultScripts
    {
        get => _skipDefaultScripts;
        set { _skipDefaultScripts = value; OnPropertyChanged(); OnPropertyChanged(nameof(DefaultScriptsSummary)); }
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
        set { _preLaunchScriptTimeoutSeconds = value; OnPropertyChanged(); }
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
            OnPropertyChanged(nameof(CanOfferSteamLaunch));
            OnPropertyChanged(nameof(CanOfferSteamBadge));
        }
    }

    /// <summary>Shows the "Launch through the Steam client" option: an entry with no launcher tag
    /// (or one converted to Local in this session) that has a Steam App ID to launch by.</summary>
    public bool CanOfferSteamLaunch => !HasPlatform && !string.IsNullOrWhiteSpace(SteamAppId);

    /// <summary>"Use the Steam badge instead of the Local badge" only matters for a game that does not launch through Steam.</summary>
    public bool CanOfferSteamBadge => !_isSteamGame;

    // --- Launcher platform (GOG / EA / Epic / Ubisoft / Steam-by-AppId) ---
    // The card shows a platform badge the dialog previously could neither explain nor change.

    private bool _launchDirectly;
    private bool _hasPlatform;
    private bool _convertToLocal;
    private ICommand? _convertToLocalCommand;

    /// <summary>True while the entry is tagged to a launcher platform (and hasn't been converted
    /// to Local in this edit session) - shows the platform card.</summary>
    public bool HasPlatform
    {
        get => (_hasPlatform || _isSteamGame) && !_convertToLocal;
        private set { _hasPlatform = value; OnPropertyChanged(); OnPropertyChanged(nameof(PlatformDescription)); OnPropertyChanged(nameof(LaunchDirectlyLabel)); OnPropertyChanged(nameof(CanOfferSteamLaunch)); OnPropertyChanged(nameof(CanOfferLaunchDirectly)); }
    }

    /// <summary>"GOG", "EA", "Epic", "Ubisoft" or "Steam" - whichever tag the entry carries.
    /// Steam reads the edited value so ticking "launch through Steam" relabels the box at once.</summary>
    public string PlatformName =>
        SourceGame.IsGogGame ? "GOG" :
        SourceGame.IsEaGame ? "EA" :
        SourceGame.IsEpicGame ? "Epic" :
        SourceGame.IsUbisoftGame ? "Ubisoft" :
        SourceGame.IsXboxGame ? "Xbox" :
        _isSteamGame ? "Steam" : "Local";

    /// <summary>"Launch this executable directly" has no meaning for a Game Pass title: a GDK exe
    /// can't run outside its package, so the launcher always activates the AUMID.</summary>
    public bool CanOfferLaunchDirectly => HasPlatform && !SourceGame.IsXboxGame;

    /// <summary>e.g. "Imported from GOG (game ID 1207658924)". Where it came from and the ID
    /// the launcher knows it by, so a wrong match is at least diagnosable.</summary>
    public string PlatformDescription
    {
        get
        {
            string? id = SourceGame.IsGogGame ? SourceGame.GogGameId
                : SourceGame.IsEaGame ? SourceGame.EaContentId
                : SourceGame.IsEpicGame ? SourceGame.EpicAppName
                : SourceGame.IsUbisoftGame ? SourceGame.UbisoftGameId
                : SourceGame.IsXboxGame ? SourceGame.XboxAumid
                : SteamAppId;
            string via = SourceGame.ImportedFrom != null ? "Imported from" : "Linked to";
            return string.IsNullOrEmpty(id) ? $"{via} {PlatformName}" : $"{via} {PlatformName} (ID {id})";
        }
    }

    public string LaunchDirectlyLabel => $"Launch this executable directly instead of through {PlatformName}";

    /// <summary>See <see cref="GameEntry.LaunchDirectly"/>: without this, editing the executable
    /// path of a platform game has no effect because the client-launch path ignores it.</summary>
    public bool LaunchDirectly
    {
        get => _launchDirectly;
        set { _launchDirectly = value; OnPropertyChanged(); }
    }

    /// <summary>Drops the platform tag and ID on save, making this a plain Local game: launched
    /// from its executable, badged as Local, no longer swept by "turn off integration + remove
    /// its games". The next Scan for Games may offer the platform's own record again.</summary>
    public ICommand ConvertToLocalCommand => _convertToLocalCommand ??= new RelayCommand(() =>
    {
        _convertToLocal = true;
        IsSteamGame = false;
        OnPropertyChanged(nameof(HasPlatform));
        OnPropertyChanged(nameof(CanOfferSteamLaunch));
        StatusMessage = $"Will be saved as a Local game (no longer linked to {PlatformName}). Click Save to apply.";
    });

    public bool ForceSteamOverlayTag
    {
        get => _forceSteamOverlayTag;
        set { _forceSteamOverlayTag = value; OnPropertyChanged(); }
    }

    public string? SteamAppId
    {
        get => _steamAppId;
        set 
        { 
            _steamAppId = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SteamAppIdDisplay));
            OnPropertyChanged(nameof(HasSteamAppId));
            OnPropertyChanged(nameof(CanOfferSteamLaunch));
            OnPropertyChanged(nameof(PlatformDescription));
        }
    }

    public string SteamAppIdDisplay => !string.IsNullOrEmpty(SteamAppId) ? $"Steam AppID: {SteamAppId}" : string.Empty;
    public bool HasSteamAppId => !string.IsNullOrWhiteSpace(SteamAppId);

    public bool IsRefreshingMetadata
    {
        get => _isRefreshingMetadata;
        private set
        {
            if (_isRefreshingMetadata != value)
            {
                _isRefreshingMetadata = value;
                OnPropertyChanged();
                Application.Current?.Dispatcher?.InvokeAsync(CommandManager.InvalidateRequerySuggested);
            }
        }
    }

    public string? CustomIconPath
    {
        get => _customIconPath;
        set
        {
            if (_customIconPath != value)
            {
                _customIconPath = value;
                OnPropertyChanged();
                UpdateIconPreview();
            }
        }
    }

    public BitmapImage? IconPreview
    {
        get => _iconPreview;
        private set
        {
            _iconPreview = value;
            OnPropertyChanged();
        }
    }

    public string? CustomCoverPath
    {
        get => _customCoverPath;
        set
        {
            if (_customCoverPath != value)
            {
                _customCoverPath = value;
                // A manual pick or removal supersedes an earlier fetched cover from this session.
                _fetchedCoverPath = null;
                OnPropertyChanged();
                UpdateCoverPreview();
            }
        }
    }

    public BitmapImage? CoverPreview
    {
        get => _coverPreview;
        private set
        {
            _coverPreview = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCoverPreview));
        }
    }

    public bool HasCoverPreview => CoverPreview != null;

    public ICommand BrowseExeCommand { get; }
    public ICommand BrowseWorkDirCommand { get; }
    public ICommand BrowsePreLaunchScriptCommand { get; }
    public ICommand BrowsePostExitScriptCommand { get; }
    public ICommand TestPreLaunchScriptCommand { get; }
    public ICommand TestPostExitScriptCommand { get; }
    public ICommand NewPreLaunchScriptCommand { get; }
    public ICommand NewPostExitScriptCommand { get; }
    public ICommand EditPreLaunchScriptCommand { get; }
    public ICommand EditPostExitScriptCommand { get; }
    public ICommand OpenScriptsFolderCommand { get; }
    public ICommand BrowseIconCommand { get; }
    public ICommand ResetIconCommand { get; }
    public ICommand BrowseCoverCommand { get; }
    public ICommand ResetCoverCommand { get; }
    public ICommand LookUpCommand { get; }
    public ICommand FetchBySteamIdCommand { get; }
    public ICommand RefreshPosterCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    private void BrowseIcon()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Custom Game Icon",
            Filter = "Image & Icon Files (*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.exe)|*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.exe|All Files (*.*)|*.*",
            CheckFileExists = true
        };

        if (FileDialogCloak.Show(dialog) == true)
        {
            CustomIconPath = dialog.FileName;
        }
    }

    private void ResetIcon()
    {
        _customIconPath = string.Empty;
        OnPropertyChanged(nameof(CustomIconPath));

        if (!string.IsNullOrEmpty(ExecutablePath) && File.Exists(ExecutablePath))
        {
            try
            {
                using var ico = System.Drawing.Icon.ExtractAssociatedIcon(ExecutablePath);
                if (ico != null)
                {
                    using var bmp = ico.ToBitmap();
                    using var ms = new MemoryStream();
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    ms.Position = 0;
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.StreamSource = ms;
                    bi.EndInit();
                    bi.Freeze();
                    IconPreview = bi;
                    return;
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn("GameEditViewModel", $"Failed to extract icon preview from '{ExecutablePath}': {ex.Message}");
            }
        }

        UpdateIconPreview();
    }

    private void UpdateIconPreview()
    {
        if (!string.IsNullOrEmpty(_customIconPath) && File.Exists(_customIconPath))
        {
            string ext = Path.GetExtension(_customIconPath).ToLowerInvariant();
            if (ext == ".exe" || ext == ".dll" || ext == ".lnk")
            {
                try
                {
                    using var ico = System.Drawing.Icon.ExtractAssociatedIcon(_customIconPath);
                    if (ico != null)
                    {
                        using var bmp = ico.ToBitmap();
                        using var ms = new MemoryStream();
                        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        ms.Position = 0;
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.StreamSource = ms;
                        bi.EndInit();
                        bi.Freeze();
                        IconPreview = bi;
                        return;
                    }
                }
                catch (Exception ex)
                {
                    LoggingService.Warn("GameEditViewModel", $"Failed to extract icon preview from '{_customIconPath}': {ex.Message}");
                }
            }

            IconPreview = IconExtractorService.LoadBitmapSafely(_customIconPath);
            return;
        }

        if (!string.IsNullOrEmpty(SourceGame.IconPath) && File.Exists(SourceGame.IconPath))
        {
            IconPreview = IconExtractorService.LoadBitmapSafely(SourceGame.IconPath);
            return;
        }

        if (!string.IsNullOrEmpty(ExecutablePath) && File.Exists(ExecutablePath))
        {
            try
            {
                using var ico = System.Drawing.Icon.ExtractAssociatedIcon(ExecutablePath);
                if (ico != null)
                {
                    using var bmp = ico.ToBitmap();
                    using var ms = new MemoryStream();
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    ms.Position = 0;
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.StreamSource = ms;
                    bi.EndInit();
                    bi.Freeze();
                    IconPreview = bi;
                    return;
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn("GameEditViewModel", $"Failed to extract icon preview from '{ExecutablePath}': {ex.Message}");
            }
        }

        IconPreview = null;
    }

    private void BrowseCover()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Custom Poster Artwork (600×900 recommended)",
            Filter = "Image Files (*.jpg;*.jpeg;*.png;*.webp;*.bmp)|*.jpg;*.jpeg;*.png;*.webp;*.bmp|All Files (*.*)|*.*",
            CheckFileExists = true
        };

        if (FileDialogCloak.Show(dialog) == true)
        {
            CustomCoverPath = dialog.FileName;
        }
    }

    private void ResetCover()
    {
        CustomCoverPath = string.Empty;
    }

    private void UpdateCoverPreview()
    {
        if (!string.IsNullOrEmpty(_fetchedCoverPath) && File.Exists(_fetchedCoverPath))
        {
            CoverPreview = IconExtractorService.LoadBitmapSafely(_fetchedCoverPath, decodePixelWidth: 340);
        }
        else if (!string.IsNullOrEmpty(_customCoverPath) && File.Exists(_customCoverPath))
        {
            CoverPreview = IconExtractorService.LoadBitmapSafely(_customCoverPath, decodePixelWidth: 340);
        }
        else if (!string.IsNullOrEmpty(SourceGame.CoverImagePath) && File.Exists(SourceGame.CoverImagePath))
        {
            CoverPreview = IconExtractorService.LoadBitmapSafely(SourceGame.CoverImagePath, decodePixelWidth: 340);
        }
        else
        {
            CoverPreview = null;
        }
    }

    public void FetchNameFromExe()
    {
        if (string.IsNullOrWhiteSpace(ExecutablePath)) return;

        string folderFallback = string.Empty;
        if (!string.IsNullOrWhiteSpace(WorkingDirectory) && Directory.Exists(WorkingDirectory))
        {
            folderFallback = Path.GetFileName(WorkingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        if (string.IsNullOrWhiteSpace(folderFallback) && !string.IsNullOrWhiteSpace(ExecutablePath))
        {
            folderFallback = Path.GetFileName(Path.GetDirectoryName(ExecutablePath) ?? "");
        }

        string resolved = GameNameExtractor.ExtractGameName(ExecutablePath, folderFallback, preferExe: true);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            Name = resolved;
        }
    }

    /// <summary>
    /// "Look Up": find this game online and fully populate it. First tries Steam by title - on a
    /// hit it links the App ID and pulls the poster and genre in one step (the same as "Fetch by
    /// ID", not just the title as the old "Search Online" did). When Steam has nothing (Roblox,
    /// Fortnite, a Game Pass exclusive, an obscure indie) it falls back to SteamGridDB for a poster
    /// by name - art only, no genre - and reports which game the art came from so a loose match is
    /// visible. The typed title is left as-is on the fallback path since there's no authoritative
    /// title source there.
    /// </summary>
    public async Task LookUpAsync()
    {
        try
        {
            string term = !string.IsNullOrWhiteSpace(Name) ? Name : Path.GetFileNameWithoutExtension(ExecutablePath);
            if (string.IsNullOrWhiteSpace(term) && !string.IsNullOrWhiteSpace(ExecutablePath))
            {
                term = Path.GetFileName(Path.GetDirectoryName(ExecutablePath) ?? "");
            }

            if (string.IsNullOrWhiteSpace(term))
            {
                StatusMessage = "Enter a title or select an executable first.";
                return;
            }

            IsRefreshingMetadata = true;
            StatusMessage = $"Looking up \"{term}\"...";

            var steamSearch = new SteamSearchService();
            var match = await steamSearch.FindBestMatchAsync(term, _minConfidence);
            if (match != null && !string.IsNullOrWhiteSpace(match.AppId))
            {
                // Steam hit: pull full details (title, poster, genre) exactly like Fetch by ID.
                var metadataService = new SteamMetadataService();
                var details = await metadataService.GetAppDetailsAsync(match.AppId, _steamGridDbApiKey, forceRefresh: true);
                if (details != null && !string.IsNullOrWhiteSpace(details.Name))
                {
                    ApplySteamDetails(details, match.AppId);
                    StatusMessage = $"Found on Steam: \"{details.Name}\"";
                    return;
                }

                // Details fetch failed but the search matched a title - keep at least that.
                Name = match.Name;
                SteamAppId = match.AppId;
                StatusMessage = $"Found Steam match: \"{match.Name}\" (details unavailable).";
                return;
            }

            // No Steam match: fall back to SteamGridDB poster art by name.
            await LookUpGridArtByNameAsync(term);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Look up failed: {ex.Message}";
            LoggingService.Warn("GameEditViewModel", $"Look up failed: {ex.Message}");
        }
        finally
        {
            IsRefreshingMetadata = false;
        }
    }

    /// <summary>The SteamGridDB-by-name fallback for Look Up. Applies a poster only when the matched
    /// community title resembles what was searched, so a stray autocomplete hit isn't pinned.</summary>
    private async Task LookUpGridArtByNameAsync(string term)
    {
        if (string.IsNullOrWhiteSpace(_steamGridDbApiKey))
        {
            StatusMessage = $"No Steam match for \"{term}\". Add a SteamGridDB key in Settings to find poster art for non-Steam games.";
            return;
        }

        StatusMessage = $"No Steam match; searching SteamGridDB art for \"{term}\"...";
        var metadataService = new SteamMetadataService();
        var art = await metadataService.DownloadAndCacheGridArtByNameAsync(SourceGame.Id, term, _steamGridDbApiKey);
        if (art == null)
        {
            StatusMessage = $"No match found for \"{term}\" on Steam or SteamGridDB.";
            return;
        }

        double similarity = SteamSearchService.CalculateSimilarity(term, art.Value.MatchedName);
        if (similarity >= _minConfidence)
        {
            ApplyFetchedCover(art.Value.Path);
            StatusMessage = $"No Steam listing; found poster art for \"{art.Value.MatchedName}\" via SteamGridDB.";
        }
        else
        {
            StatusMessage = $"Closest SteamGridDB art (\"{art.Value.MatchedName}\") didn't look like \"{term}\" - left unchanged. Use Change... to set art manually.";
        }
    }

    /// <summary>Applies a full Steam details fetch to the edit fields - shared by "Look Up" and
    /// "Fetch by ID" so both routes to an App ID populate title, poster and genre identically.</summary>
    private void ApplySteamDetails(SteamAppDetails details, string appId)
    {
        Name = details.Name;
        SteamAppId = appId;
        // Preserves non-Steam launch: a local executable must NOT be flipped to a steam:// game.
        if (!string.IsNullOrWhiteSpace(ExecutablePath) && !ExecutablePath.StartsWith("steam://", StringComparison.OrdinalIgnoreCase))
        {
            IsSteamGame = false;
        }
        ApplyFetchedCover(details.CoverImagePath);
        if ((string.IsNullOrWhiteSpace(Category) || Category.Equals(LibraryConstants.Uncategorized, StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(details.PrimaryGenre))
        {
            Category = details.PrimaryGenre;
        }
    }

    public void LookUp() => _ = LookUpAsync();

    /// <summary>
    /// Corrects a misidentified game by fetching name + poster art directly from Steam for an
    /// exact AppID, bypassing whatever the fuzzy name search or folder-scan heuristics guessed.
    /// Invalidates any cached details/poster for this AppID first, so a previously wrong result
    /// (e.g. from a bad automatic match) doesn't get served back out of the in-memory cache.
    /// Note: Does NOT modify IsSteamGame; local executables remain local executable games.
    /// </summary>
    public async Task FetchBySteamIdAsync()
    {
        string id = SteamAppId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(id) || !id.All(char.IsDigit))
        {
            StatusMessage = "Please enter a valid numeric Steam App ID.";
            LoggingService.Warn("GameEditViewModel", $"Invalid Steam AppID '{id}' supplied for manual match.");
            return;
        }

        IsRefreshingMetadata = true;
        StatusMessage = $"Fetching Steam details for App ID {id}...";
        try
        {
            SteamMetadataService.InvalidateCache(id);
            var metadataService = new SteamMetadataService();
            var details = await metadataService.GetAppDetailsAsync(id, _steamGridDbApiKey, forceRefresh: true);
            if (details == null || string.IsNullOrWhiteSpace(details.Name))
            {
                StatusMessage = $"No Steam store details found for App ID {id}.";
                LoggingService.Warn("GameEditViewModel", $"No Steam app details found for AppID '{id}'.");
                return;
            }

            ApplySteamDetails(details, id);
            StatusMessage = $"Successfully matched with \"{details.Name}\"!";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error fetching from Steam: {ex.Message}";
            LoggingService.Warn("GameEditViewModel", $"Failed to fetch Steam details for AppID '{id}': {ex.Message}");
        }
        finally
        {
            IsRefreshingMetadata = false;
        }
    }

    public void FetchBySteamId() => _ = FetchBySteamIdAsync();

    /// <summary>
    /// Force re-downloads poster art for the currently set Steam AppID, bypassing the on-disk
    /// "poster already exists" check - useful when the name/AppID are already correct but the
    /// cached art is a low-quality fallback (or was cached before SteamGridDB was configured).
    /// </summary>
    public async Task RefreshPosterAsync()
    {
        string id = SteamAppId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(id) || !id.All(char.IsDigit))
        {
            StatusMessage = "Cannot refresh poster: no valid numeric Steam App ID set.";
            LoggingService.Warn("GameEditViewModel", "Cannot refresh poster: no valid numeric Steam AppID set for this game.");
            return;
        }

        IsRefreshingMetadata = true;
        StatusMessage = $"Downloading latest poster for App ID {id}...";
        try
        {
            SteamMetadataService.InvalidateCache(id);
            var metadataService = new SteamMetadataService();
            string? cover = await metadataService.DownloadAndCachePosterAsync(id, null, _steamGridDbApiKey, forceRefresh: true);
            if (!string.IsNullOrWhiteSpace(cover))
            {
                ApplyFetchedCover(cover);
                StatusMessage = "Poster artwork refreshed successfully!";
            }
            else
            {
                StatusMessage = "No updated poster art found on Steam.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to refresh poster: {ex.Message}";
            LoggingService.Warn("GameEditViewModel", $"Failed to refresh poster for AppID '{id}': {ex.Message}");
        }
        finally
        {
            IsRefreshingMetadata = false;
        }
    }

    public void RefreshPoster() => _ = RefreshPosterAsync();

    private void ApplyFetchedCover(string? coverPath)
    {
        if (string.IsNullOrWhiteSpace(coverPath) || !File.Exists(coverPath)) return;

        // Staged like CustomCoverPath: only committed to SourceGame in Save(), so Cancel leaves
        // the game's on-disk CoverImagePath reference untouched. The downloaded file itself is
        // already written to the covers cache dir by the metadata fetch, which is fine to keep.
        _fetchedCoverPath = coverPath;
        // Clear any earlier manual "Change..." pick so the freshly fetched official art wins,
        // both in the preview and in Save()'s custom-cover-copy check.
        _customCoverPath = null;
        OnPropertyChanged(nameof(CustomCoverPath));
        UpdateCoverPreview();
    }

    private void BrowseExe()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Game Executable or Shortcut",
            Filter = "Executables & Shortcuts (*.exe;*.lnk;*.url)|*.exe;*.lnk;*.url|All Files (*.*)|*.*",
            CheckFileExists = true
        };

        if (FileDialogCloak.Show(dialog) == true)
        {
            ExecutablePath = dialog.FileName;
            if (string.IsNullOrWhiteSpace(WorkingDirectory))
            {
                WorkingDirectory = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(Name) || IsNewGame)
            {
                FetchNameFromExe();
            }
        }
    }

    private void BrowseWorkDir()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Working Directory",
            Multiselect = false
        };

        if (FileDialogCloak.Show(dialog) == true)
        {
            WorkingDirectory = dialog.FolderName;
        }
    }

    private static string? ValidateScriptPath(string? rawPath, string label)
    {
        string path = rawPath?.Trim().Trim('"') ?? string.Empty;
        if (path.Length == 0) return null;

        if (!GameScriptService.IsSupportedScript(path))
        {
            return $"{label} script must be one of: {string.Join(", ", GameScriptService.SupportedExtensions)}. Wrap other file types in a .bat.";
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
            Filter = "Batch script (*.bat)|*.bat|PowerShell script (*.ps1)|*.ps1",
            DefaultExt = ".bat",
            AddExtension = true,
            OverwritePrompt = false,
            FileName = $"{SafeFileStem(Name)}-{phase}.bat",
            InitialDirectory = InitialScriptDirectory(string.Empty)
        };

        if (FileDialogCloak.Show(dialog) != true || string.IsNullOrWhiteSpace(dialog.FileName)) return;

        string path = dialog.FileName;
        try
        {
            bool created = ScriptLibraryService.CreateFromBlankTemplate(path);
            StatusMessage = created
                ? $"Created {Path.GetFileName(path)} from the blank template. Edit it, then Save."
                : $"{Path.GetFileName(path)} already exists and was left untouched.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not create the script: {ex.Message}";
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

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = "Unnamed Game";
        }

        // Scripts are validated at save time rather than silently skipped at launch, so a typo
        // or an unsupported file type is caught while the user is still looking at the field.
        string? scriptProblem = ValidateScriptPath(PreLaunchScriptPath, "Pre-launch")
                             ?? ValidateScriptPath(PostExitScriptPath, "Post-exit");
        if (scriptProblem != null)
        {
            StatusMessage = scriptProblem;
            return;
        }

        string trimmedAppId = SteamAppId?.Trim() ?? string.Empty;
        if (trimmedAppId.Length > 0 && !UrlProtocolHelper.IsValidSteamAppId(trimmedAppId))
        {
            StatusMessage = "Steam App ID must be numeric (e.g. 1245620), or leave it blank.";
            return;
        }

        int timeoutSeconds = (int)GameScriptService.DefaultPreLaunchWaitTimeout.TotalSeconds;
        if (!string.IsNullOrWhiteSpace(PreLaunchScriptTimeoutSeconds))
        {
            if (!int.TryParse(PreLaunchScriptTimeoutSeconds.Trim(), out timeoutSeconds)
                || timeoutSeconds < GameScriptService.MinPreLaunchTimeoutSeconds
                || timeoutSeconds > GameScriptService.MaxPreLaunchTimeoutSeconds)
            {
                StatusMessage = $"Pre-launch script timeout must be a whole number of seconds between {GameScriptService.MinPreLaunchTimeoutSeconds} and {GameScriptService.MaxPreLaunchTimeoutSeconds}.";
                return;
            }
        }

        SourceGame.Name = Name.Trim();
        SourceGame.ExecutablePath = ExecutablePath.Trim();
        SourceGame.Arguments = Arguments?.Trim() ?? string.Empty;
        SourceGame.WorkingDirectory = WorkingDirectory?.Trim() ?? string.Empty;
        SourceGame.RunAsAdmin = RunAsAdmin;
        SourceGame.Category = string.IsNullOrWhiteSpace(Category) ? LibraryConstants.Uncategorized : Category.Trim();
        SourceGame.Hotkey = Hotkey?.Trim() ?? string.Empty;
        SourceGame.IsSteamGame = IsSteamGame;
        SourceGame.ForceSteamOverlayTag = ForceSteamOverlayTag;
        SourceGame.SteamAppId = trimmedAppId.Length == 0 ? null : trimmedAppId;
        SourceGame.LaunchDirectly = LaunchDirectly;
        if (_convertToLocal)
        {
            LoggingService.Info("GameEdit", $"'{SourceGame.Name}' converted from {PlatformName} to a Local game.");
            SourceGame.IsSteamGame = false;
            SourceGame.IsGogGame = false;
            SourceGame.GogGameId = null;
            SourceGame.IsEaGame = false;
            SourceGame.EaContentId = null;
            SourceGame.IsEpicGame = false;
            SourceGame.EpicAppName = null;
            SourceGame.IsUbisoftGame = false;
            SourceGame.UbisoftGameId = null;
            SourceGame.IsXboxGame = false;
            SourceGame.XboxAumid = null;
            SourceGame.ImportedFrom = null;
            SourceGame.LaunchDirectly = false;
        }
        if (SourceGame.PerformanceProfile != PerformanceProfile)
        {
            LoggingService.Info("GameEdit", $"'{SourceGame.Name}' Performance Profile changed: {SourceGame.PerformanceProfile} -> {PerformanceProfile}.");
        }
        SourceGame.PerformanceProfile = PerformanceProfile;
        SourceGame.CpuAffinity = CpuAffinity;
        SourceGame.PreLaunchScriptPath = PreLaunchScriptPath?.Trim().Trim('"') ?? string.Empty;
        SourceGame.PostExitScriptPath = PostExitScriptPath?.Trim().Trim('"') ?? string.Empty;
        SourceGame.WaitForPreLaunchScript = WaitForPreLaunchScript || AbortLaunchOnScriptFailure;
        SourceGame.RunScriptsHidden = RunScriptsHidden;
        SourceGame.RunScriptsAsAdmin = RunScriptsAsAdmin;
        SourceGame.ScriptArguments = ScriptArguments?.Trim() ?? string.Empty;
        SourceGame.SkipDefaultScripts = SkipDefaultScripts;
        SourceGame.AbortLaunchOnScriptFailure = AbortLaunchOnScriptFailure;
        SourceGame.PreLaunchScriptTimeoutSeconds = timeoutSeconds;
        SourceGame.CloseLauncherOnExit = CloseLauncherOnExit && !_convertToLocal;
        SourceGame.IsHidden = IsHidden;

        // Handle custom icon caching
        if (!string.IsNullOrEmpty(CustomIconPath) && CustomIconPath != SourceGame.IconPath && File.Exists(CustomIconPath))
        {
            string cached = _iconExtractorService.ExtractAndCacheIcon(SourceGame.Id, CustomIconPath, Name);
            if (!string.IsNullOrEmpty(cached))
            {
                SourceGame.IconPath = cached;
            }
        }
        else if (CustomIconPath == string.Empty || (string.IsNullOrEmpty(SourceGame.IconPath) && !string.IsNullOrEmpty(ExecutablePath)))
        {
            string cached = _iconExtractorService.ExtractAndCacheIcon(SourceGame.Id, ExecutablePath, Name);
            if (!string.IsNullOrEmpty(cached))
            {
                SourceGame.IconPath = cached;
            }
        }

        // Handle custom poster artwork caching
        if (!string.IsNullOrEmpty(CustomCoverPath) && CustomCoverPath != SourceGame.CoverImagePath && File.Exists(CustomCoverPath))
        {
            try
            {
                string coversDir = SteamMetadataService.CoversDirectory;
                if (!Directory.Exists(coversDir)) Directory.CreateDirectory(coversDir);

                string ext = Path.GetExtension(CustomCoverPath);
                if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";
                string destFile = Path.Combine(coversDir, $"{SourceGame.Id}{ext}");

                if (!string.Equals(Path.GetFullPath(CustomCoverPath), Path.GetFullPath(destFile), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(CustomCoverPath, destFile, overwrite: true);
                }
                SourceGame.CoverImagePath = destFile;
            }
            catch
            {
                SourceGame.CoverImagePath = CustomCoverPath;
            }
        }
        else if (CustomCoverPath == string.Empty)
        {
            SourceGame.CoverImagePath = null;
        }
        else if (!string.IsNullOrEmpty(_fetchedCoverPath) && File.Exists(_fetchedCoverPath))
        {
            // Already written into the covers cache dir by the fetch itself - just point at it.
            SourceGame.CoverImagePath = _fetchedCoverPath;
        }

        RequestClose?.Invoke(true);
    }

    private void Cancel()
    {
        RequestClose?.Invoke(false);
    }
}

/// <summary>
/// What a Test Run produced, plus the two per-game options the test deliberately ignored so the
/// result dialog can warn that the real run will behave differently.
/// </summary>
/// <param name="ProbeDescription">What game values the test used, for the summary line; null means "the name and exe currently in Edit Game".</param>
public sealed record ScriptTestReport(bool IsPreLaunch, string ScriptPath, ScriptTestResult Result, bool WillRunElevated, bool WillRunHidden, string? ProbeDescription = null);
