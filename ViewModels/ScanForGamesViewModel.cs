using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

/// <summary>
/// One newly-discovered game in the scan results. Wraps either a <see cref="DiscoveredSteamGame"/>
/// or a <see cref="GameCandidate"/> so both sources share a single selectable list - the same
/// select/filter shape the old separate SteamImportViewModel and FolderBatchImportViewModel each
/// used on their own before "Scan for Games" unified both entry points. Only ever constructed for
/// games not already in the library - see <see cref="ImportCoordinator.ScanForGamesAsync"/>.
/// </summary>
public class ScannedGameItemViewModel : ViewModelBase
{
    private bool _isSelected = true;
    private ImageSource? _iconImage;

    public DiscoveredSteamGame? SteamGame { get; }
    public GameCandidate? FolderCandidate { get; }

    public string Name => SteamGame?.Name ?? FolderCandidate!.Name;
    public string PathDisplay => SteamGame?.InstallDir ?? FolderCandidate!.DisplayPath;
    public string SourceLabel => SteamGame != null ? "Steam" : "Folder";

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
        LoadIconAsync(() => IconExtractorService.LoadBitmapSafely(steamGame.IconPath));
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

    public event Action<List<DiscoveredSteamGame>, List<GameCandidate>>? ImportConfirmed;
    public event Action? RequestClose;

    public ICommand SelectAllCommand { get; }
    public ICommand DeselectAllCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand CancelCommand { get; }

    private readonly Action<GameCandidate> _onIgnoreCandidate;
    private readonly Action<DiscoveredSteamGame> _onIgnoreSteamGame;

    public ScanForGamesViewModel(List<DiscoveredSteamGame> steamGames, List<GameCandidate> folderCandidates,
        Action<GameCandidate> onIgnoreCandidate, Action<DiscoveredSteamGame> onIgnoreSteamGame)
    {
        _onIgnoreCandidate = onIgnoreCandidate;
        _onIgnoreSteamGame = onIgnoreSteamGame;

        foreach (var g in steamGames)
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
        var selectedFolder = Results.Where(r => r.IsSelected && r.FolderCandidate != null).Select(r => r.FolderCandidate!).ToList();

        if (selectedSteam.Count > 0 || selectedFolder.Count > 0)
        {
            LoggingService.Info("ScanForGames", $"Confirmed import of {selectedSteam.Count} Steam game(s) and {selectedFolder.Count} folder game(s) out of {Results.Count} scan result(s).");
            ImportConfirmed?.Invoke(selectedSteam, selectedFolder);
        }
        else
        {
            LoggingService.Verbose("ScanForGames", $"Scan dialog closed with 0 of {Results.Count} result(s) selected - nothing imported.");
        }
        RequestClose?.Invoke();
    }
}
