using System;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

/// <summary>One row in Settings' editable list of folders "Scan for Games" looks in.</summary>
public class ScanLocationRowViewModel : ViewModelBase
{
    private readonly Action _onChanged;

    public ScanLocation Model { get; }
    public string Path => Model.Path;
    public bool IsAutoManaged => Model.IsAutoManaged;

    public string SourceLabel => Model.Source switch
    {
        ScanLocationSource.Steam => "STEAM",
        ScanLocationSource.Gog => "GOG",
        ScanLocationSource.Ea => "EA",
        _ => "MANUAL"
    };

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
