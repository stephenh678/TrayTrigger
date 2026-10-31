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

/// <summary>Edit Game's tabs: All shows every card as before; the others show one card each.</summary>
public enum GameEditSection
{
    All,
    Identity,
    Artwork,
    Launch,
    Performance,
    Display,
    Scripts
}

public partial class GameEditViewModel : ViewModelBase
{
    private readonly IconExtractorService _iconExtractorService;
    private readonly Func<PerformanceProfileMode, IReadOnlyList<ProfileTweakToggleViewModel>>? _profileTweaks;
    private readonly string? _steamGridDbApiKey;
    private readonly double _minConfidence;
    /// <summary>One for every edit dialog: the service keeps no per-instance state, and its
    /// constructor re-scans the legacy covers folder each time one is built.</summary>
    private static readonly Lazy<SteamMetadataService> SharedMetadataService = new(() => new SteamMetadataService());
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
    private string _preLaunchScriptTimeoutSeconds = ((int)GameScriptService.DefaultPreLaunchWaitTimeout.TotalSeconds).ToString();
    private bool _useSameScriptForBoth;
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
                LoggingService.Shown("Edit Game status", value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasStatusMessage));
            }
            // Any new message is news until ShowProblem says otherwise.
            if (_statusIsProblem)
            {
                _statusIsProblem = false;
                OnPropertyChanged(nameof(StatusIsProblem));
            }
        }
    }

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(_statusMessage);

    private bool _statusIsProblem;

    /// <summary>The footer's message is a failure (a refused save, a look-up that errored), shown in the problem colour, not the accent.</summary>
    public bool StatusIsProblem => _statusIsProblem;

    private void ShowProblem(string message)
    {
        StatusMessage = message;
        _statusIsProblem = true;
        OnPropertyChanged(nameof(StatusIsProblem));
    }

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
        ScriptLibraryService? scriptLibrary = null,
        Func<PerformanceProfileMode, IReadOnlyList<ProfileTweakToggleViewModel>>? profileTweaks = null,
        Action? persistLibrary = null,
        Action<bool>? setScriptsEnabled = null)
    {
        SourceGame = game;
        _scriptDefaults = scriptDefaults;
        _scriptLibrary = scriptLibrary;
        _setScriptsEnabled = setScriptsEnabled;
        _scriptsEnabled = scriptsEnabled;
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
        _profileTweaks = profileTweaks;
        IsNewGame = isNewGame;
        // The records list is the game's own, mutated in place: a DLSS apply changes the driver
        // immediately, so the record has to be saved immediately too. Deferring it to Save Changes
        // would let Cancel strand an override TrayTrigger could no longer undo.
        Dlss = new DlssCardViewModel(game.ExecutablePath, game.Name, game.DlssSettings, persistLibrary, game: game);

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
        _hasPlatform = game.IsGogGame || game.IsEaGame || game.IsEpicGame || game.IsUbisoftGame || game.IsXboxGame || game.IsBattleNetGame || (game.IsSteamGame && !string.IsNullOrEmpty(game.SteamAppId));
        _performanceProfile = game.PerformanceProfile;
        _cpuAffinity = game.CpuAffinity;
        _initialCpuAffinity = game.CpuAffinity;
        _hdr = game.Hdr;
        _initialHdr = game.Hdr;
        _autoHdr = game.AutoHdr;
        _initialAutoHdr = game.AutoHdr;
        LoadDisplay(game);
        _keepMouseOnGameScreen = game.KeepMouseOnGameScreen;
        _initialKeepMouse = game.KeepMouseOnGameScreen;
        _cpuCoresDelaySeconds = game.CpuCoresDelaySeconds.ToString();
        _preLaunchScriptPath = game.PreLaunchScriptPath;
        _postExitScriptPath = game.PostExitScriptPath;
        // Not stored on the game: a game "uses the same script" exactly when both paths match.
        _useSameScriptForBoth = !string.IsNullOrWhiteSpace(game.PreLaunchScriptPath)
            && string.Equals(game.PreLaunchScriptPath.Trim(), game.PostExitScriptPath?.Trim(), StringComparison.OrdinalIgnoreCase);
        _waitForPreLaunchScript = game.WaitForPreLaunchScript;
        _runScriptsHidden = game.RunScriptsHidden;
        _runScriptsAsAdmin = game.RunScriptsAsAdmin;
        _scriptArguments = game.ScriptArguments;
        _isHidden = game.IsHidden;
        _customIconPath = game.IconPath;
        _customCoverPath = game.CoverImagePath;

        // All, Favorites and Hidden are library views, not categories a game can be put in.
        foreach (var cat in categories.Where(c => c is not (LibraryConstants.AllCategory or LibraryConstants.FavoritesCategory or LibraryConstants.HiddenCategory)).Distinct())
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
        UndoLookUpCommand = new RelayCommand(UndoLookUp, () => CanUndoLookUp && !IsRefreshingMetadata);
        FetchBySteamIdCommand = new RelayCommand(FetchBySteamId, () => !IsRefreshingMetadata);
        RefreshPosterCommand = new RelayCommand(RefreshPoster, () => !IsRefreshingMetadata);
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(Cancel);
        SelectSectionCommand = new RelayCommand(p =>
        {
            if (p is GameEditSection section) SelectedSection = section;
            else if (p is string name && Enum.TryParse(name, out GameEditSection parsed)) SelectedSection = parsed;
        });

        UpdateIconPreview();
        UpdateCoverPreview();
        InitArtworkTab(game);
        _showScriptRunOptions = game.HasScripts;
        _initialEditState = EditState();
    }

    private readonly string _initialEditState;

    /// <summary>
    /// True once any field Save writes back differs from what the dialog opened with. Esc asks
    /// before throwing such edits away; with nothing changed it closes at once.
    /// </summary>
    public bool HasUnsavedChanges => !string.Equals(EditState(), _initialEditState, StringComparison.Ordinal);

    /// <summary>Every value Save copies onto the game, joined into one comparable string.</summary>
    private string EditState() => string.Join("", new object?[]
    {
        Name, ExecutablePath, Arguments, WorkingDirectory, RunAsAdmin, IsHidden, Category, Hotkey,
        IsSteamGame, ForceSteamOverlayTag, SteamAppId, LaunchDirectly, _convertToLocal,
        PerformanceProfile, CpuAffinity, CpuCoresDelaySeconds, Hdr, AutoHdr, SelectedDisplayMode?.ToString(), KeepMouseOnGameScreen,
        SelectedMonitor.Id, MakeMainDisplay, DimOtherScreens, SelectedAudio.Id, SelectedMicrophone.Id,
        PreLaunchScriptPath, PostExitScriptPath, UseSameScriptForBoth, WaitForPreLaunchScript,
        RunScriptsHidden, RunScriptsAsAdmin, ScriptArguments, SkipDefaultScripts,
        AbortLaunchOnScriptFailure, PreLaunchScriptTimeoutSeconds, CloseLauncherOnExit,
        CustomIconPath, CustomCoverPath, _fetchedCoverPath, _customHeroSource, _heroCustom, _posterCustom, _iconCustom,
    });

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); OnPropertyChanged(nameof(HeadingText)); }
    }

    public string ExecutablePath
    {
        get => _executablePath;
        set
        {
            _executablePath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsLinkTarget));
            OnPropertyChanged(nameof(ExecutableLabel));
            OnPropertyChanged(nameof(LinkTargetHint));
        }
    }

    /// <summary>The target is a launcher link (steam://rungameid/..., a protocol URL), not a program to run.</summary>
    public bool IsLinkTarget => _executablePath?.Contains("://", StringComparison.Ordinal) == true;

    public string ExecutableLabel => IsLinkTarget ? "Launch Link" : "Executable / Shortcut Target";

    /// <summary>Under a link target: what it is, and how to run a program instead where that is possible.</summary>
    public string LinkTargetHint => !IsLinkTarget ? string.Empty
        : !HasPlatform ? "Windows opens this link to start the game, with whatever program handles it."
        : !CanOfferLaunchDirectly ? $"{PlatformName} opens this link to start the game."
        : LaunchDirectly ? "Launching directly needs a program: Browse to the game's .exe."
        : $"{PlatformName} opens this link to start the game. To run a program instead, tick \"{LaunchDirectlyLabel}\" and Browse to its .exe.";

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
        set
        {
            _performanceProfile = value;
            OnPropertyChanged();
            NotifyProfileSummary();
        }
    }

    /// <summary>
    /// Under the profile box: what the chosen tier will do for this game, one line per tweak,
    /// read from the same toggles the System page's Performance Profiles tab edits. A tweak that
    /// asks for administrator permission says so. Empty (and hidden) when the dialog was opened
    /// without the System page's toggles.
    /// </summary>
    public IReadOnlyList<string> ProfileSummaryLines
    {
        get
        {
            if (_profileTweaks == null) return Array.Empty<string>();
            if (PerformanceProfile == PerformanceProfileMode.Off)
                return new[] { Hdr == HdrMode.On || AutoHdr != HdrMode.ProfileDefault
                    ? "Off: only HDR and Auto HDR are changed when this game runs, from the choices below."
                    : "Off: nothing is changed when this game runs." };
            var tweaks = _profileTweaks(PerformanceProfile);
            if (tweaks.Count == 0)
                return new[] { $"Every {PerformanceProfile} tweak is switched off on the System page, so nothing is changed." };
            return tweaks.Select(t => t.RequiresAdmin ? $"{t.Name} (asks for administrator permission)" : t.Name).ToList();
        }
    }

    public bool HasProfileSummary => _profileTweaks != null;

    /// <summary>How many summary lines show before "Show N more". A list one line longer is shown whole.</summary>
    internal const int ProfileSummaryCollapsedLines = 4;
    private bool _showAllProfileLines;

    /// <summary>The summary as shown: whole, or its first lines while a long list is collapsed.</summary>
    public IReadOnlyList<string> VisibleProfileSummaryLines =>
        CanExpandProfileSummary && !_showAllProfileLines ? ProfileSummaryLines.Take(ProfileSummaryCollapsedLines).ToList() : ProfileSummaryLines;

    public bool CanExpandProfileSummary => ProfileSummaryLines.Count > ProfileSummaryCollapsedLines + 1;

    public string ProfileSummaryToggleText => _showAllProfileLines
        ? "Show fewer"
        : $"Show {ProfileSummaryLines.Count - ProfileSummaryCollapsedLines} more";

    public ICommand ToggleProfileSummaryCommand => _toggleProfileSummaryCommand ??= new RelayCommand(() =>
    {
        _showAllProfileLines = !_showAllProfileLines;
        NotifyProfileSummary();
    });
    private ICommand? _toggleProfileSummaryCommand;

    private void NotifyProfileSummary()
    {
        OnPropertyChanged(nameof(ProfileSummaryLines));
        OnPropertyChanged(nameof(VisibleProfileSummaryLines));
        OnPropertyChanged(nameof(CanExpandProfileSummary));
        OnPropertyChanged(nameof(ProfileSummaryToggleText));
    }

    public IReadOnlyList<PerformanceProfileMode> PerformanceProfileOptions { get; } =
        new[] { PerformanceProfileMode.Off, PerformanceProfileMode.Optimized, PerformanceProfileMode.Aggressive };

    /// <summary>The dialog's heading: the game being edited, or "New Game" until it has a name.</summary>
    public string HeadingText => string.IsNullOrWhiteSpace(Name) ? "New Game" : Name.Trim();

    // --- DLSS (read-only; see docs/dlss-plan.md) ---

    /// <summary>
    /// The DLSS card. Built here but deliberately not loaded here: <see cref="DlssCardViewModel"/>
    /// only touches the driver when the dialog asks it to, so constructing this view model - which
    /// the tests do freely - never does.
    /// </summary>
    public DlssCardViewModel Dlss { get; }

    // --- Pre-launch / post-exit scripts ---

    public bool ShowScriptsCard { get; private set; }

    /// <summary>The one-line "scripts are turned off" note shown in place of the scripts card, so
    /// the feature stays discoverable from Edit Game while the Settings switch is off.</summary>
    public bool ShowScriptsStub => !ShowScriptsCard;

    private bool _scriptsEnabled;
    private readonly Action<bool>? _setScriptsEnabled;

    /// <summary>
    /// "Enable scripts" in the notice: the same switch as Settings > Launch &amp; Performance, which is one setting for
    /// every game. It takes effect at once, like the DLSS switch, so Cancel does not undo it. Turned on, the notice goes
    /// and the scripts card takes its place.
    /// </summary>
    public bool ScriptsEnabled
    {
        get => _scriptsEnabled;
        set
        {
            if (_scriptsEnabled == value) return;
            _scriptsEnabled = value;
            _setScriptsEnabled?.Invoke(value);
            // A game with scripts of its own keeps the card with the feature off; any other shows it only while it is on.
            ShowScriptsCard = value || SourceGame.HasScripts;
            ShowScriptsDisabledNotice = !value && SourceGame.HasScripts;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowScriptsCard));
            OnPropertyChanged(nameof(ShowScriptsStub));
            OnPropertyChanged(nameof(ShowScriptsCardOnTab));
            OnPropertyChanged(nameof(ShowScriptsStubOnTab));
            OnPropertyChanged(nameof(ShowScriptsDisabledNotice));
            OnPropertyChanged(nameof(CanEditScriptArguments));
        }
    }

    private void BrowseExe()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Game Executable or Shortcut",
            Filter = "Executables & Shortcuts (*.exe;*.lnk;*.url)|*.exe;*.lnk;*.url|All Files (*.*)|*.*",
            CheckFileExists = true
        };
        DialogStartLocation.ForFile(dialog, ExecutablePath, WorkingDirectory);

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
        DialogStartLocation.ForFolder(dialog, WorkingDirectory, ExecutablePath);

        if (FileDialogCloak.Show(dialog) == true)
        {
            WorkingDirectory = dialog.FolderName;
        }
    }

    private void Save()
    {
        SetFieldError(null);

        // Scripts are validated at save time rather than silently skipped at launch, so a typo
        // or an unsupported file type is caught while the user is still looking at the field.
        string? preLaunchProblem = ValidateScriptPath(PreLaunchScriptPath, "Pre-launch");
        if (preLaunchProblem != null)
        {
            RejectField(EditField.PreLaunchScript, preLaunchProblem);
            return;
        }
        string? postExitProblem = ValidateScriptPath(PostExitScriptPath, "Post-exit");
        if (postExitProblem != null)
        {
            RejectField(EditField.PostExitScript, postExitProblem);
            return;
        }

        string trimmedAppId = SteamAppId?.Trim() ?? string.Empty;
        if (trimmedAppId.Length > 0 && !UrlProtocolHelper.IsValidSteamAppId(trimmedAppId))
        {
            RejectField(EditField.SteamAppId, "Steam App ID must be numeric (e.g. 1245620), or leave it blank.");
            return;
        }

        int timeoutSeconds = (int)GameScriptService.DefaultPreLaunchWaitTimeout.TotalSeconds;
        if (!string.IsNullOrWhiteSpace(PreLaunchScriptTimeoutSeconds))
        {
            if (!int.TryParse(PreLaunchScriptTimeoutSeconds.Trim(), out timeoutSeconds)
                || timeoutSeconds < GameScriptService.MinPreLaunchTimeoutSeconds
                || timeoutSeconds > GameScriptService.MaxPreLaunchTimeoutSeconds)
            {
                RejectField(EditField.PreLaunchTimeout, $"Pre-launch script timeout must be a whole number of seconds between {GameScriptService.MinPreLaunchTimeoutSeconds} and {GameScriptService.MaxPreLaunchTimeoutSeconds}.");
                return;
            }
        }

        int cpuCoresDelay = 0;
        if (!string.IsNullOrWhiteSpace(CpuCoresDelaySeconds) && CpuAffinity != CpuAffinityMode.Default)
        {
            if (!int.TryParse(CpuCoresDelaySeconds.Trim(), out cpuCoresDelay) || cpuCoresDelay < 0 || cpuCoresDelay > CpuTopologyService.MaxDelaySeconds)
            {
                RejectField(EditField.CpuCoresDelay, $"The wait before setting CPU cores must be a whole number of seconds between 0 and {CpuTopologyService.MaxDelaySeconds}.");
                return;
            }
        }

        // Only now that nothing can refuse the save: a refused one leaves the box as it was typed.
        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = "Unnamed Game";
        }

        var settingsBefore = LibraryChanges.Capture(SourceGame);
        string platformBefore = _convertedFrom ?? PlatformName;

        SourceGame.Name = Name.Trim();
        SourceGame.ExecutablePath = ExecutablePath.Trim();
        SourceGame.Arguments = Arguments?.Trim() ?? string.Empty;
        SourceGame.WorkingDirectory = WorkingDirectory?.Trim() ?? string.Empty;
        SourceGame.RunAsAdmin = RunAsAdmin;
        SourceGame.Category = LibraryConstants.NormalizeCategory(Category);
        SourceGame.Hotkey = Hotkey?.Trim() ?? string.Empty;
        SourceGame.IsSteamGame = IsSteamGame;
        SourceGame.ForceSteamOverlayTag = ForceSteamOverlayTag;
        SourceGame.SteamAppId = trimmedAppId.Length == 0 ? null : trimmedAppId;
        SourceGame.LaunchDirectly = LaunchDirectly;
        if (_convertToLocal)
        {
            LoggingService.Info("GameEdit", $"'{SourceGame.Name}' converted from {platformBefore} to a Local game.");
            // IsSteamGame is left as the box has it: false after Convert, unless "Launch through the
            // Steam client" was ticked afterwards - a choice Save must not throw away.
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
            SourceGame.IsBattleNetGame = false;
            SourceGame.BattleNetUid = null;
            SourceGame.BattleNetProgramId = null;
            SourceGame.ImportedFrom = null;
            SourceGame.LaunchDirectly = false;
        }
        if (SourceGame.PerformanceProfile != PerformanceProfile)
        {
            LoggingService.Info("GameEdit", $"'{SourceGame.Name}' Performance Profile changed: {SourceGame.PerformanceProfile} -> {PerformanceProfile}.");
            PerformanceActivity.GameChanged(SourceGame, SourceGame.PerformanceProfile, PerformanceProfile);
        }
        SourceGame.PerformanceProfile = PerformanceProfile;
        if (SourceGame.CpuAffinity != CpuAffinity)
        {
            PerformanceActivity.GameChanged(SourceGame, SourceGame.CpuAffinity, CpuAffinity);
        }
        SourceGame.CpuAffinity = CpuAffinity;
        if (SourceGame.Hdr != Hdr)
        {
            PerformanceActivity.GameChanged(SourceGame, SourceGame.Hdr, Hdr);
        }
        SourceGame.Hdr = Hdr;
        if (SourceGame.AutoHdr != AutoHdr)
        {
            PerformanceActivity.AutoHdrGameChanged(SourceGame, SourceGame.AutoHdr, AutoHdr);
        }
        SourceGame.AutoHdr = AutoHdr;
        var displayMode = SelectedDisplayMode;
        SourceGame.DisplayWidth = displayMode?.Width ?? 0;
        SourceGame.DisplayHeight = displayMode?.Height ?? 0;
        SourceGame.DisplayRefreshHz = displayMode?.RefreshHz ?? 0;
        SourceGame.KeepMouseOnGameScreen = KeepMouseOnGameScreen;
        SourceGame.DisplayMonitorId = SelectedMonitor.Id;
        SourceGame.MakeMainDisplay = ShowMakeMainDisplay && MakeMainDisplay;
        SourceGame.DimOtherScreens = DimOtherScreens;
        SourceGame.AudioOutputDeviceId = SelectedAudio.Id;
        SourceGame.AudioInputDeviceId = SelectedMicrophone.Id;
        // Kept as it was while CPU Cores is on Default, where the box is hidden: switching back
        // to a choice brings the game's own delay back with it.
        if (CpuAffinity != CpuAffinityMode.Default) SourceGame.CpuCoresDelaySeconds = cpuCoresDelay;
        SourceGame.PreLaunchScriptPath = PreLaunchScriptPath?.Trim().Trim('"') ?? string.Empty;
        SourceGame.PostExitScriptPath = (UseSameScriptForBoth ? PreLaunchScriptPath : PostExitScriptPath)?.Trim().Trim('"') ?? string.Empty;
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
            catch (Exception ex)
            {
                LoggingService.Swallowed("GameEdit", ex, "copying the custom cover into the covers folder; the original path is kept");
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

        CommitHero();

        // Said last: the pictures are only in place once they have been committed.
        var edit = LibraryChanges.DescribeEdit(settingsBefore, LibraryChanges.Capture(SourceGame));
        if (edit != null)
        {
            ActivityService.Add(ActivityLevel.Change, edit.Value.Text, subject: SourceGame.Name, detail: edit.Value.Detail);
        }
        if (_convertToLocal)
        {
            ActivityService.Add(ActivityLevel.Change, $"{SourceGame.Name} is now a Local game", subject: SourceGame.Name,
                detail: $"It was linked to {platformBefore}; it no longer is, and launches the way a Local game does.");
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
