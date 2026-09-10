using System;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

/// <summary>
/// One folder row in Settings - either a manual Scan Location (with a Remove button) or an
/// auto-detected Steam library under the Steam integration toggle (checkbox only). Both share
/// this view model; the two lists are split by <see cref="ScanLocation.Source"/> in
/// SettingsViewModel.RebuildScanLocationRows.
/// </summary>
public class ScanLocationRowViewModel : ViewModelBase
{
    private readonly Action _onChanged;

    public ScanLocation Model { get; }
    public string Path => Model.Path;
    public bool IsAutoManaged => Model.IsAutoManaged;

    public ScanLocationRowViewModel(ScanLocation model, Action onChanged)
    {
        Model = model;
        _onChanged = onChanged;
    }

    public bool IsEnabled
    {
        get => Model.IsEnabled;
        set
        {
            if (Model.IsEnabled != value)
            {
                Model.IsEnabled = value;
                OnPropertyChanged();
                LoggingService.Info("ScanLocationRow", $"Scan location '{Path}' {(value ? "enabled" : "disabled")}.");
                _onChanged();
            }
        }
    }
}
