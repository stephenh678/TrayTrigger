using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

public class SteamImportItemViewModel : ViewModelBase
{
    private bool _isSelected;
    public DiscoveredSteamGame Discovered { get; }
    public BitmapImage? IconImage { get; }

    public SteamImportItemViewModel(DiscoveredSteamGame discovered)
    {
        Discovered = discovered;
        _isSelected = !discovered.IsAlreadyImported;
        IconImage = IconExtractorService.LoadBitmapSafely(discovered.IconPath);
    }

    public string AppId => Discovered.AppId;
    public string Name => Discovered.Name;
    public string InstallDir => Discovered.InstallDir;
    public bool IsAlreadyImported => Discovered.IsAlreadyImported;

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
}

public class SteamImportViewModel : ViewModelBase
{
    private readonly SteamScannerService _scannerService;
    private readonly IEnumerable<string> _existingAppIds;
    private string _filterText = string.Empty;
    private bool _isLoading;
    private string _statusMessage = string.Empty;

    public ObservableCollection<SteamImportItemViewModel> Games { get; } = new();
    public ICollectionView FilteredGames { get; }

    public event Action<List<DiscoveredSteamGame>>? ImportConfirmed;
    public event Action? RequestClose;

    public SteamImportViewModel(SteamScannerService scannerService, IEnumerable<string> existingAppIds)
    {
        _scannerService = scannerService;
        _existingAppIds = existingAppIds;

        FilteredGames = CollectionViewSource.GetDefaultView(Games);
        FilteredGames.Filter = FilterGame;

        SelectAllCommand = new RelayCommand(SelectAll);
        DeselectAllCommand = new RelayCommand(DeselectAll);
        ImportCommand = new RelayCommand(ImportSelected);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke());
        RefreshCommand = new RelayCommand(() => _ = LoadGamesAsync());

        _ = LoadGamesAsync();
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            _filterText = value;
            OnPropertyChanged();
            FilteredGames.Refresh();
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; OnPropertyChanged(); }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public ICommand SelectAllCommand { get; }
    public ICommand DeselectAllCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RefreshCommand { get; }

    public async Task LoadGamesAsync()
    {
        IsLoading = true;
        StatusMessage = "Scanning Steam library folders...";
        Games.Clear();

        try
        {
            var discovered = await Task.Run(() => _scannerService.ScanInstalledGames(_existingAppIds)).ConfigureAwait(true);
            foreach (var g in discovered)
            {
                Games.Add(new SteamImportItemViewModel(g));
            }

            StatusMessage = discovered.Count > 0
                ? $"Found {discovered.Count} Steam game(s)."
                : "No installed Steam games found in library folders.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scan error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool FilterGame(object obj)
    {
        if (obj is not SteamImportItemViewModel item) return false;
        if (string.IsNullOrWhiteSpace(FilterText)) return true;

        return item.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ||
               item.AppId.Contains(FilterText, StringComparison.OrdinalIgnoreCase);
    }

    private void SelectAll()
    {
        foreach (var item in Games.Where(g => !g.IsAlreadyImported))
        {
            item.IsSelected = true;
        }
    }

    private void DeselectAll()
    {
        foreach (var item in Games)
        {
            item.IsSelected = false;
        }
    }

    private void ImportSelected()
    {
        var selected = Games
            .Where(g => g.IsSelected && !g.IsAlreadyImported)
            .Select(g => g.Discovered)
            .ToList();

        ImportConfirmed?.Invoke(selected);
        RequestClose?.Invoke();
    }
}
