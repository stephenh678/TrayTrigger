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

    public BatchGameItemViewModel(GameCandidate candidate, bool isAlreadyImported)
    {
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
    public string DisplaySize => Candidate.DisplaySize;

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
    private readonly HashSet<string> _existingExePaths;

    public string FolderName { get; }
    public ObservableCollection<BatchGameItemViewModel> Games { get; } = new();
    public ICollectionView FilteredGames { get; }

    public event Action<List<GameCandidate>>? ImportConfirmed;
    public event Action? RequestClose;

    public ICommand SelectAllCommand { get; }
    public ICommand DeselectAllCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand CancelCommand { get; }

    public FolderBatchImportViewModel(string folderPath, List<GameCandidate> candidates, IEnumerable<string> existingExePaths)
    {
        _folderPath = folderPath;
        FolderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        _existingExePaths = new HashSet<string>(existingExePaths, StringComparer.OrdinalIgnoreCase);

        foreach (var c in candidates)
        {
            bool alreadyIn = _existingExePaths.Contains(c.ExePath);
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
            Games.Add(item);
        }

        FilteredGames = CollectionViewSource.GetDefaultView(Games);
        FilteredGames.Filter = FilterGame;

        SelectAllCommand = new RelayCommand(SelectAll);
        DeselectAllCommand = new RelayCommand(DeselectAll);
        ImportCommand = new RelayCommand(ConfirmImport);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke());
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
            ImportConfirmed?.Invoke(selectedCandidates);
        }
        RequestClose?.Invoke();
    }
}
