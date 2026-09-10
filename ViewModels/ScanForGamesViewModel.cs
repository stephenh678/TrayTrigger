using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

/// <summary>
/// One newly-discovered game in the scan results. Wraps a <see cref="DiscoveredSteamGame"/>,
/// a <see cref="DiscoveredGogGame"/>, or a <see cref="GameCandidate"/> so every source shares a
/// single selectable list - the same select/filter shape the old separate SteamImportViewModel
/// and FolderBatchImportViewModel each used on their own before "Scan for Games" unified all entry
/// points. Only ever constructed for games not already in the library - see
/// <see cref="ImportCoordinator.ScanForGamesAsync"/>.
/// </summary>
public class ScannedGameItemViewModel : ViewModelBase
{
    private bool _isSelected = true;
    private ImageSource? _iconImage;

    public DiscoveredSteamGame? SteamGame { get; }
    public DiscoveredGogGame? GogGame { get; }
    public DiscoveredEaGame? EaGame { get; }
    public DiscoveredEpicGame? EpicGame { get; }
    public DiscoveredUbisoftGame? UbisoftGame { get; }
    public GameCandidate? FolderCandidate { get; }

    public string Name => SteamGame?.Name ?? GogGame?.Name ?? EaGame?.Name ?? EpicGame?.Name ?? UbisoftGame?.Name ?? FolderCandidate!.Name;
    public string PathDisplay => SteamGame?.InstallDir ?? GogGame?.InstallDir ?? EaGame?.InstallDir ?? EpicGame?.InstallDir ?? UbisoftGame?.InstallDir ?? FolderCandidate!.DisplayPath;

    /// <summary>Pack URI to the same launcher logo used on poster cards and the first-launch
    /// picker - a folder-scanned candidate has no launcher, so it gets the generic "Local Games"
    /// mark instead.</summary>
    public string SourceLogoUri => SteamGame != null ? "pack://application:,,,/Assets/LauncherLogos/steam.png"
        : GogGame != null ? "pack://application:,,,/Assets/LauncherLogos/gog_galaxy.png"
        : EaGame != null ? "pack://application:,,,/Assets/LauncherLogos/ea_app.png"
        : EpicGame != null ? "pack://application:,,,/Assets/LauncherLogos/epic_games.png"
        : UbisoftGame != null ? "pack://application:,,,/Assets/LauncherLogos/ubisoft_connect.png"
        : "pack://application:,,,/Assets/LauncherLogos/local_games.png";

    /// <summary>Raised when the user clicks "Ignore" - the parent VM removes this row and persists the ignore.</summary>
    public event Action<ScannedGameItemViewModel>? IgnoreRequested;
    public ICommand IgnoreCommand { get; }

    public ImageSource? IconImage
    {
        get => _iconImage;
        private set
        {
            if (_iconImage != value)
            {
                _iconImage = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public ScannedGameItemViewModel(DiscoveredSteamGame steamGame)
    {
        IgnoreCommand = new RelayCommand(() => IgnoreRequested?.Invoke(this));
        SteamGame = steamGame;
        LoadIconAsync(() => IconExtractorService.LoadIconOrExtractFromExe(steamGame.IconPath));
    }

    public ScannedGameItemViewModel(DiscoveredGogGame gogGame)
    {
        IgnoreCommand = new RelayCommand(() => IgnoreRequested?.Invoke(this));
        GogGame = gogGame;
        LoadIconAsync(() => IconExtractorService.LoadIconOrExtractFromExe(gogGame.IconPath));
    }

    public ScannedGameItemViewModel(DiscoveredEaGame eaGame)
    {
        IgnoreCommand = new RelayCommand(() => IgnoreRequested?.Invoke(this));
        EaGame = eaGame;
        LoadIconAsync(() => IconExtractorService.LoadIconOrExtractFromExe(eaGame.IconPath));
    }

    public ScannedGameItemViewModel(DiscoveredEpicGame epicGame)
    {
        IgnoreCommand = new RelayCommand(() => IgnoreRequested?.Invoke(this));
        EpicGame = epicGame;
        LoadIconAsync(() => IconExtractorService.LoadIconOrExtractFromExe(epicGame.IconPath));
    }

    public ScannedGameItemViewModel(DiscoveredUbisoftGame ubisoftGame)
    {
        IgnoreCommand = new RelayCommand(() => IgnoreRequested?.Invoke(this));
        UbisoftGame = ubisoftGame;
        LoadIconAsync(() => IconExtractorService.LoadIconOrExtractFromExe(ubisoftGame.IconPath));
    }

    public ScannedGameItemViewModel(GameCandidate candidate)
    {
        IgnoreCommand = new RelayCommand(() => IgnoreRequested?.Invoke(this));
        FolderCandidate = candidate;
        LoadIconAsync(() => IconExtractorService.ExtractAssociatedBitmapSource(candidate.ExePath));
    }

    // Decode off the UI thread - a large multi-location scan would otherwise hang the dialog
    // while every icon decodes synchronously. See M-24 (same reasoning in SteamImportItemViewModel).
    private void LoadIconAsync(Func<ImageSource?> loadIcon)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var img = loadIcon();
                if (img != null)
                {
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => IconImage = img);
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn("ScanForGames", $"Failed to load icon for '{Name}': {ex.Message}");
            }
        });
    }
}

/// <summary>
/// Backs the "Scan for Games" results dialog - a bulk install prompt shown after
/// <see cref="ImportCoordinator.ScanForGamesAsync"/> already found one or more new games across
/// the configured scan locations (managed in Settings &gt; Game Scanner). Purely a picker: it takes
/// the already-scanned results and lets the user choose which ones to add.
/// </summary>
public class ScanForGamesViewModel : ViewModelBase
{
    private string _filterText = string.Empty;

    public ObservableCollection<ScannedGameItemViewModel> Results { get; } = new();
    public ICollectionView FilteredResults { get; }

    public event Action<List<DiscoveredSteamGame>, List<DiscoveredGogGame>, List<DiscoveredEaGame>, List<DiscoveredEpicGame>, List<DiscoveredUbisoftGame>, List<GameCandidate>>? ImportConfirmed;
    public event Action? RequestClose;

    public ICommand SelectAllCommand { get; }
    public ICommand DeselectAllCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand CancelCommand { get; }

    private readonly Action<GameCandidate> _onIgnoreCandidate;
    private readonly Action<DiscoveredSteamGame> _onIgnoreSteamGame;
    private readonly Action<DiscoveredGogGame> _onIgnoreGogGame;
    private readonly Action<DiscoveredEaGame> _onIgnoreEaGame;
    private readonly Action<DiscoveredEpicGame> _onIgnoreEpicGame;
    private readonly Action<DiscoveredUbisoftGame> _onIgnoreUbisoftGame;

    public ScanForGamesViewModel(List<DiscoveredSteamGame> steamGames, List<DiscoveredGogGame> gogGames, List<DiscoveredEaGame> eaGames, List<DiscoveredEpicGame> epicGames, List<DiscoveredUbisoftGame> ubisoftGames, List<GameCandidate> folderCandidates,
        Action<GameCandidate> onIgnoreCandidate, Action<DiscoveredSteamGame> onIgnoreSteamGame, Action<DiscoveredGogGame> onIgnoreGogGame, Action<DiscoveredEaGame> onIgnoreEaGame, Action<DiscoveredEpicGame> onIgnoreEpicGame, Action<DiscoveredUbisoftGame> onIgnoreUbisoftGame)
    {
        _onIgnoreCandidate = onIgnoreCandidate;
        _onIgnoreSteamGame = onIgnoreSteamGame;
        _onIgnoreGogGame = onIgnoreGogGame;
        _onIgnoreEaGame = onIgnoreEaGame;
        _onIgnoreEpicGame = onIgnoreEpicGame;
        _onIgnoreUbisoftGame = onIgnoreUbisoftGame;

        foreach (var g in steamGames)
        {
            var item = new ScannedGameItemViewModel(g);
            item.IgnoreRequested += OnItemIgnoreRequested;
            Results.Add(item);
        }
        foreach (var g in gogGames)
        {
            var item = new ScannedGameItemViewModel(g);
            item.IgnoreRequested += OnItemIgnoreRequested;
            Results.Add(item);
        }
        foreach (var g in eaGames)
        {
            var item = new ScannedGameItemViewModel(g);
            item.IgnoreRequested += OnItemIgnoreRequested;
            Results.Add(item);
        }
        foreach (var g in epicGames)
        {
            var item = new ScannedGameItemViewModel(g);
            item.IgnoreRequested += OnItemIgnoreRequested;
            Results.Add(item);
        }
        foreach (var g in ubisoftGames)
        {
            var item = new ScannedGameItemViewModel(g);
            item.IgnoreRequested += OnItemIgnoreRequested;
            Results.Add(item);
        }
        foreach (var c in folderCandidates)
        {
            var item = new ScannedGameItemViewModel(c);
            item.IgnoreRequested += OnItemIgnoreRequested;
            Results.Add(item);
        }

        FilteredResults = CollectionViewSource.GetDefaultView(Results);
        FilteredResults.Filter = FilterResult;

        SelectAllCommand = new RelayCommand(() => SetAllSelected(true));
        DeselectAllCommand = new RelayCommand(() => SetAllSelected(false));
        ImportCommand = new RelayCommand(ImportSelected);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke());
    }

    public string SubtitleText => Results.Count == 1
        ? "Found 1 new game. Select it to add it to your library:"
        : $"Found {Results.Count} new games. Select the ones to add to your library:";

    private void OnItemIgnoreRequested(ScannedGameItemViewModel item)
    {
        if (item.FolderCandidate != null)
        {
            _onIgnoreCandidate(item.FolderCandidate);
        }
        else if (item.SteamGame != null)
        {
            _onIgnoreSteamGame(item.SteamGame);
        }
        else if (item.GogGame != null)
        {
            _onIgnoreGogGame(item.GogGame);
        }
        else if (item.EaGame != null)
        {
            _onIgnoreEaGame(item.EaGame);
        }
        else if (item.EpicGame != null)
        {
            _onIgnoreEpicGame(item.EpicGame);
        }
        else if (item.UbisoftGame != null)
        {
            _onIgnoreUbisoftGame(item.UbisoftGame);
        }

        item.IgnoreRequested -= OnItemIgnoreRequested;
        Results.Remove(item);
        OnPropertyChanged(nameof(SubtitleText));
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (_filterText != value)
            {
                _filterText = value;
                OnPropertyChanged();
                FilteredResults.Refresh();
            }
        }
    }

    private bool FilterResult(object obj)
    {
        if (obj is not ScannedGameItemViewModel item) return false;
        if (string.IsNullOrWhiteSpace(FilterText)) return true;

        return item.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase);
    }

    private void SetAllSelected(bool selected)
    {
        foreach (var item in Results)
        {
            item.IsSelected = selected;
        }
    }

    private void ImportSelected()
    {
        var selectedSteam = Results.Where(r => r.IsSelected && r.SteamGame != null).Select(r => r.SteamGame!).ToList();
        var selectedGog = Results.Where(r => r.IsSelected && r.GogGame != null).Select(r => r.GogGame!).ToList();
        var selectedEa = Results.Where(r => r.IsSelected && r.EaGame != null).Select(r => r.EaGame!).ToList();
        var selectedEpic = Results.Where(r => r.IsSelected && r.EpicGame != null).Select(r => r.EpicGame!).ToList();
        var selectedUbisoft = Results.Where(r => r.IsSelected && r.UbisoftGame != null).Select(r => r.UbisoftGame!).ToList();
        var selectedFolder = Results.Where(r => r.IsSelected && r.FolderCandidate != null).Select(r => r.FolderCandidate!).ToList();

        if (selectedSteam.Count > 0 || selectedGog.Count > 0 || selectedEa.Count > 0 || selectedEpic.Count > 0 || selectedUbisoft.Count > 0 || selectedFolder.Count > 0)
        {
            LoggingService.Info("ScanForGames", $"Confirmed import of {selectedSteam.Count} Steam game(s), {selectedGog.Count} GOG game(s), {selectedEa.Count} EA game(s), {selectedEpic.Count} Epic game(s), {selectedUbisoft.Count} Ubisoft game(s), and {selectedFolder.Count} folder game(s) out of {Results.Count} scan result(s).");
            ImportConfirmed?.Invoke(selectedSteam, selectedGog, selectedEa, selectedEpic, selectedUbisoft, selectedFolder);
        }
        else
        {
            LoggingService.Verbose("ScanForGames", $"Scan dialog closed with 0 of {Results.Count} result(s) selected - nothing imported.");
        }
        RequestClose?.Invoke();
    }
}
