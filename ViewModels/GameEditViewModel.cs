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
        bool scriptsEnabled = false)
    {
        SourceGame = game;
        // The card is opt-in (Settings > General), but a game that already has a script must
        // stay editable even if the setting was later turned off or reset.
        ShowScriptsCard = scriptsEnabled || game.HasScripts;
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
        _performanceProfile = game.PerformanceProfile;
        _preLaunchScriptPath = game.PreLaunchScriptPath;
        _postExitScriptPath = game.PostExitScriptPath;
        _waitForPreLaunchScript = game.WaitForPreLaunchScript;
        _runScriptsHidden = game.RunScriptsHidden;
        _runScriptsAsAdmin = game.RunScriptsAsAdmin;
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
        BrowseIconCommand = new RelayCommand(BrowseIcon);
        ResetIconCommand = new RelayCommand(ResetIcon);
        BrowseCoverCommand = new RelayCommand(BrowseCover);
        ResetCoverCommand = new RelayCommand(ResetCover);
        FetchNameFromExeCommand = new RelayCommand(FetchNameFromExe);
        FetchOfficialNameOnlineCommand = new RelayCommand(FetchOfficialNameOnline);
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

    // --- Pre-launch / post-exit scripts ---

    public bool ShowScriptsCard { get; }

    public string PreLaunchScriptPath
    {
        get => _preLaunchScriptPath;
        set
        {
            _preLaunchScriptPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPreLaunchScript));
            OnPropertyChanged(nameof(HasAnyScript));
        }
    }

    public string PostExitScriptPath
    {
        get => _postExitScriptPath;
        set
        {
            _postExitScriptPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasAnyScript));
        }
    }

    /// <summary>Gates the "wait for pre-launch script" option, which only means something with a pre-launch script set.</summary>
    public bool HasPreLaunchScript => !string.IsNullOrWhiteSpace(_preLaunchScriptPath);

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

    public bool IsSteamGame
    {
        get => _isSteamGame;
        set { _isSteamGame = value; OnPropertyChanged(); }
    }

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
    public ICommand BrowseIconCommand { get; }
    public ICommand ResetIconCommand { get; }
    public ICommand BrowseCoverCommand { get; }
    public ICommand ResetCoverCommand { get; }
    public ICommand FetchNameFromExeCommand { get; }
    public ICommand FetchOfficialNameOnlineCommand { get; }
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

        if (dialog.ShowDialog() == true)
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

        if (dialog.ShowDialog() == true)
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

    public async Task FetchOfficialNameOnlineAsync()
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

            StatusMessage = $"Searching Steam for \"{term}\"...";
            var steamSearch = new SteamSearchService();
            var match = await steamSearch.FindBestMatchAsync(term, _minConfidence);
            if (match != null && !string.IsNullOrWhiteSpace(match.Name))
            {
                Name = match.Name;
                if (!string.IsNullOrWhiteSpace(match.AppId))
                {
                    SteamAppId = match.AppId;
                }
                StatusMessage = $"Found Steam match: \"{match.Name}\"";
            }
            else
            {
                StatusMessage = $"No matching game found on Steam for \"{term}\".";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Search failed: {ex.Message}";
            LoggingService.Warn("GameEditViewModel", $"Failed to fetch official name online: {ex.Message}");
        }
    }

    public void FetchOfficialNameOnline() => _ = FetchOfficialNameOnlineAsync();

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

            Name = details.Name;
            SteamAppId = id;
            // Preserves non-Steam state: local executables must NOT be changed to steam:// protocol games
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

        if (dialog.ShowDialog() == true)
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

        if (dialog.ShowDialog() == true)
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
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            if (isPreLaunch) PreLaunchScriptPath = dialog.FileName;
            else PostExitScriptPath = dialog.FileName;
        }
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

        SourceGame.Name = Name.Trim();
        SourceGame.ExecutablePath = ExecutablePath.Trim();
        SourceGame.Arguments = Arguments?.Trim() ?? string.Empty;
        SourceGame.WorkingDirectory = WorkingDirectory?.Trim() ?? string.Empty;
        SourceGame.RunAsAdmin = RunAsAdmin;
        SourceGame.Category = string.IsNullOrWhiteSpace(Category) ? LibraryConstants.Uncategorized : Category.Trim();
        SourceGame.Hotkey = Hotkey?.Trim() ?? string.Empty;
        SourceGame.IsSteamGame = IsSteamGame;
        SourceGame.ForceSteamOverlayTag = ForceSteamOverlayTag;
        SourceGame.SteamAppId = string.IsNullOrWhiteSpace(SteamAppId) ? null : SteamAppId.Trim();
        SourceGame.PerformanceProfile = PerformanceProfile;
        SourceGame.PreLaunchScriptPath = PreLaunchScriptPath?.Trim().Trim('"') ?? string.Empty;
        SourceGame.PostExitScriptPath = PostExitScriptPath?.Trim().Trim('"') ?? string.Empty;
        SourceGame.WaitForPreLaunchScript = WaitForPreLaunchScript;
        SourceGame.RunScriptsHidden = RunScriptsHidden;
        SourceGame.RunScriptsAsAdmin = RunScriptsAsAdmin;

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
