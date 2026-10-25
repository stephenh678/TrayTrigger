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

/// <summary>Edit Game: the launcher platform and its game id.</summary>
public partial class GameEditViewModel
{
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
        private set { _hasPlatform = value; OnPropertyChanged(); OnPropertyChanged(nameof(PlatformDescription)); OnPropertyChanged(nameof(LaunchDirectlyLabel)); OnPropertyChanged(nameof(LaunchDirectlyHint)); OnPropertyChanged(nameof(LinkTargetHint)); OnPropertyChanged(nameof(CanOfferSteamLaunch)); OnPropertyChanged(nameof(CanOfferLaunchDirectly)); }
    }

    /// <summary>"GOG", "EA", "Epic", "Ubisoft" or "Steam" - whichever tag the entry carries.
    /// Steam reads the edited value so ticking "launch through Steam" relabels the box at once.</summary>
    public string PlatformName =>
        SourceGame.IsGogGame ? "GOG" :
        SourceGame.IsEaGame ? "EA" :
        SourceGame.IsEpicGame ? "Epic" :
        SourceGame.IsUbisoftGame ? "Ubisoft" :
        SourceGame.IsXboxGame ? "Xbox" :
        SourceGame.IsBattleNetGame ? "Battle.net" :
        _isSteamGame ? "Steam" : "Local";

    /// <summary>"Launch this executable directly" has no meaning for a Game Pass title: a GDK exe
    /// can't run outside its package, so the launcher always activates the AUMID. Nor for a
    /// Battle.net game, which needs the sign-in token only the client passes it.</summary>
    public bool CanOfferLaunchDirectly => HasPlatform && !SourceGame.IsXboxGame && !SourceGame.IsBattleNetGame;

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
                : SourceGame.IsBattleNetGame ? (string.IsNullOrEmpty(SourceGame.BattleNetProgramId)
                    ? SourceGame.BattleNetUid
                    : $"{SourceGame.BattleNetUid}, launch code {SourceGame.BattleNetProgramId}")
                // Steam's App ID has its own box in the Identity card, so it isn't repeated here.
                : null;
            string via = SourceGame.ImportedFrom != null ? "Imported from" : "Linked to";
            return string.IsNullOrEmpty(id) ? $"{via} {PlatformName}" : $"{via} {PlatformName} (ID {id})";
        }
    }

    public string LaunchDirectlyLabel => $"Launch this executable directly instead of through {PlatformName}";

    public string LaunchDirectlyHint => $"Off: starts through {PlatformName} (cloud saves, achievements, overlay). On: runs the executable below, for an alternate exe such as a DX11 build or a mod launcher.";

    /// <summary>See <see cref="GameEntry.LaunchDirectly"/>: without this, editing the executable
    /// path of a platform game has no effect because the client-launch path ignores it.</summary>
    public bool LaunchDirectly
    {
        get => _launchDirectly;
        set { _launchDirectly = value; OnPropertyChanged(); OnPropertyChanged(nameof(LinkTargetHint)); }
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
            ClearFieldError(EditField.SteamAppId);
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
                OnPropertyChanged(nameof(IsBusy));
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
    public ICommand UndoLookUpCommand { get; }
    public ICommand FetchBySteamIdCommand { get; }
    public ICommand RefreshPosterCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
}
