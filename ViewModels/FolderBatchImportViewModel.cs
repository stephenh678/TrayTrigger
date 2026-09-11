using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

public class BatchGameItemViewModel : ViewModelBase
{
    private bool _isSelected;
    private string _name;

    private System.Windows.Media.ImageSource? _iconImage;

    public GameCandidate Candidate { get; }
    public System.Windows.Media.ImageSource? IconImage
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
    public bool IsAlreadyImported { get; }

    /// <summary>Pack URI to the launcher logo for a candidate that resolved to a Steam/GOG/EA/
    /// Epic/Ubisoft install (see GameCandidate.Platform), or the generic "Local Games" mark -
    /// the same treatment "Scan for Games" gives its rows, so the preview matches the import.</summary>
    public string SourceLogoUri => Candidate.PlatformName switch
    {
        "Steam" => "pack://application:,,,/Assets/LauncherLogos/steam.png",
        "GOG" => "pack://application:,,,/Assets/LauncherLogos/gog_galaxy.png",
        "EA" => "pack://application:,,,/Assets/LauncherLogos/ea_app.png",
        "Epic" => "pack://application:,,,/Assets/LauncherLogos/epic_games.png",
        "Ubisoft" => "pack://application:,,,/Assets/LauncherLogos/ubisoft_connect.png",
        "Xbox" => "pack://application:,,,/Assets/LauncherLogos/xbox.png",
        _ => "pack://application:,,,/Assets/LauncherLogos/local_games.png"
    };


    /// <summary>Raised when the user clicks "Ignore" - the parent VM removes this row and persists the ignore.</summary>
    public event Action<BatchGameItemViewModel>? IgnoreRequested;
    public ICommand IgnoreCommand { get; }

    public BatchGameItemViewModel(GameCandidate candidate, bool isAlreadyImported)
    {
        IgnoreCommand = new RelayCommand(() => IgnoreRequested?.Invoke(this));
        Candidate = candidate;
        _name = candidate.Name;
        IsAlreadyImported = isAlreadyImported;
        _isSelected = !isAlreadyImported;

        _ = Task.Run(() =>
        {
            try
            {
                var img = ExtractExeIcon(candidate.ExePath);
                if (img != null)
                {
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        IconImage = img;
                    });
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn("FolderBatchImportViewModel", $"Failed to extract icon for '{candidate.ExePath}': {ex.Message}");
            }
        });
    }

    public string Name
    {
        get => _name;
        set
        {
            if (_name != value)
            {
                _name = value;
                OnPropertyChanged();
            }
        }
    }

    public string ExePath => Candidate.ExePath;
    public string DisplayPath => Candidate.DisplayPath;

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

    private static System.Windows.Media.ImageSource? ExtractExeIcon(string path)
    {
        return IconExtractorService.ExtractAssociatedBitmapSource(path);
    }
}

public class FolderBatchImportViewModel : ViewModelBase
{
    private string _filterText = string.Empty;
    private readonly string _folderPath;
    private bool _rememberAsScanLocation = true;

    public string FolderName { get; }
    public ObservableCollection<BatchGameItemViewModel> Games { get; } = new();
    public ICollectionView FilteredGames { get; }

    /// <summary>
    /// True when this batch came from one real, still-existing folder on disk rather than an
    /// aggregated multi-folder drop (see ImportCoordinator.ProcessFolderAddBatchAsync, which
    /// passes a synthetic "N folders" label instead of a path) - only then does "remember as a
    /// scan location" make sense to offer.
    /// </summary>
    public bool CanRememberAsScanLocation { get; }

    /// <summary>Whether the "remember as scan location" row shows at all - see <see cref="CanRememberAsScanLocation"/>.</summary>
    public bool ShowScanLocationRow => CanRememberAsScanLocation;

    public bool IsAlreadyScanLocation { get; }

    /// <summary>
    /// False (and the checkbox disabled) when this folder is already tracked - shown checked and
    /// read-only rather than removed entirely, so it's clear *why* there's nothing to toggle
    /// instead of the option just silently disappearing.
    /// </summary>
    public bool CanToggleRememberAsScanLocation => !IsAlreadyScanLocation;

    public string RememberScanLocationLabel => IsAlreadyScanLocation
        ? $"\"{FolderName}\" is already a scan location"
        : $"Remember \"{FolderName}\" as a scan location (auto-detect new games here)";

    public bool RememberAsScanLocation
    {
        get => IsAlreadyScanLocation || _rememberAsScanLocation;
        set => SetProperty(ref _rememberAsScanLocation, value);
    }

    public event Action<List<GameCandidate>, bool>? ImportConfirmed;
    public event Action? RequestClose;

    public ICommand SelectAllCommand { get; }
    public ICommand DeselectAllCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand CancelCommand { get; }

    private readonly Action<GameCandidate> _onIgnoreCandidate;

    /// <param name="isAlreadyImported">Whether a candidate is already in the library - by exe
    /// path or, for one that resolved to a launcher game, by platform ID (see MainWindow).</param>
    public FolderBatchImportViewModel(string folderPath, List<GameCandidate> candidates, Func<GameCandidate, bool> isAlreadyImported, bool canRememberAsScanLocation, bool isAlreadyScanLocation, Action<GameCandidate> onIgnoreCandidate)
    {
        _folderPath = folderPath;
        FolderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        CanRememberAsScanLocation = canRememberAsScanLocation;
        IsAlreadyScanLocation = isAlreadyScanLocation;
        _onIgnoreCandidate = onIgnoreCandidate;

        foreach (var c in candidates)
        {
            bool alreadyIn = isAlreadyImported(c);
            var item = new BatchGameItemViewModel(c, alreadyIn);
            item.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(BatchGameItemViewModel.IsSelected))
                {
                    OnPropertyChanged(nameof(SelectedCountDisplay));
                    OnPropertyChanged(nameof(ImportButtonLabel));
                    OnPropertyChanged(nameof(CanImport));
                }
            };
            item.IgnoreRequested += OnItemIgnoreRequested;
            Games.Add(item);
        }

        FilteredGames = CollectionViewSource.GetDefaultView(Games);
        FilteredGames.Filter = FilterGame;

        SelectAllCommand = new RelayCommand(SelectAll);
        DeselectAllCommand = new RelayCommand(DeselectAll);
        ImportCommand = new RelayCommand(ConfirmImport);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke());
    }

    private void OnItemIgnoreRequested(BatchGameItemViewModel item)
    {
        _onIgnoreCandidate(item.Candidate);
        item.IgnoreRequested -= OnItemIgnoreRequested;
        Games.Remove(item);
        OnPropertyChanged(nameof(SubtitleText));
        OnPropertyChanged(nameof(SelectedCountDisplay));
        OnPropertyChanged(nameof(ImportButtonLabel));
        OnPropertyChanged(nameof(CanImport));
    }

    public string SubtitleText => $"Found {Games.Count} games in \"{FolderName}\". Select the games to add to TrayTrigger:";

    public string SelectedCountDisplay
    {
        get
        {
            int selected = Games.Count(g => g.IsSelected);
            return $"{selected} of {Games.Count} games selected";
        }
    }

    public string ImportButtonLabel
    {
        get
        {
            int selected = Games.Count(g => g.IsSelected);
            return selected > 0 ? $"Add Selected ({selected})" : "Add Selected";
        }
    }

    public bool CanImport => Games.Any(g => g.IsSelected);

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (_filterText != value)
            {
                _filterText = value;
                OnPropertyChanged();
                FilteredGames.Refresh();
            }
        }
    }

    private bool FilterGame(object obj)
    {
        if (obj is not BatchGameItemViewModel item) return false;
        if (string.IsNullOrWhiteSpace(FilterText)) return true;

        return item.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ||
               item.DisplayPath.Contains(FilterText, StringComparison.OrdinalIgnoreCase);
    }

    private void SelectAll()
    {
        foreach (var g in Games.Where(g => !g.IsAlreadyImported))
        {
            g.IsSelected = true;
        }
        OnPropertyChanged(nameof(SelectedCountDisplay));
        OnPropertyChanged(nameof(ImportButtonLabel));
        OnPropertyChanged(nameof(CanImport));
    }

    private void DeselectAll()
    {
        foreach (var g in Games)
        {
            g.IsSelected = false;
        }
        OnPropertyChanged(nameof(SelectedCountDisplay));
        OnPropertyChanged(nameof(ImportButtonLabel));
        OnPropertyChanged(nameof(CanImport));
    }

    private void ConfirmImport()
    {
        var selectedCandidates = Games
            .Where(g => g.IsSelected)
            .Select(g => g.Candidate with { Name = g.Name })
            .ToList();

        if (selectedCandidates.Count > 0)
        {
            bool remember = CanRememberAsScanLocation && CanToggleRememberAsScanLocation && RememberAsScanLocation;
            LoggingService.Info("FolderBatchImportViewModel", $"Confirmed import of {selectedCandidates.Count}/{Games.Count} game(s) from '{FolderName}' (remember as scan location: {remember}).");
            ImportConfirmed?.Invoke(selectedCandidates, remember);
        }
        else
        {
            LoggingService.Verbose("FolderBatchImportViewModel", $"Import dialog for '{FolderName}' closed with 0 games selected - nothing imported.");
        }
        RequestClose?.Invoke();
    }
}
