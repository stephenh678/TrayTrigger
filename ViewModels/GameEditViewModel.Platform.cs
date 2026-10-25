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
    private readonly bool _hasPlatform;
    private bool _convertToLocal;
    /// <summary>The platform's name when Convert to Local was clicked, for the messages and the Activity entry;
    /// by Save time the edited Steam flag has already been cleared and the name would read "Local".</summary>
    private string? _convertedFrom;
    private ICommand? _convertToLocalCommand;
    private ICommand? _undoConvertToLocalCommand;

    /// <summary>True while the entry is tagged to a launcher platform (and hasn't been converted
    /// to Local in this edit session) - shows the platform card.</summary>
    public bool HasPlatform => (_hasPlatform || _isSteamGame) && !_convertToLocal;

    /// <summary>Convert to Local game is offered unless the result couldn't run: a Game Pass (GDK) exe needs its package.</summary>
    public bool CanConvertToLocal => !SourceGame.IsXboxGame;

    /// <summary>Convert to Local was clicked and not yet saved or undone: shows the note with Undo in the platform card's place.</summary>
    public bool IsConvertedToLocal => _convertToLocal;

    /// <summary>e.g. "Will be saved as a Local game, no longer linked to Battle.net."</summary>
    public string ConvertedNotice => $"Will be saved as a Local game, no longer linked to {_convertedFrom}.";

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
    /// its games". Until Save, Undo puts the link back; after Save a scan won't offer the
    /// platform's record again, since the entry already holds its executable.</summary>
    public ICommand ConvertToLocalCommand => _convertToLocalCommand ??= new RelayCommand(() =>
    {
        // A scan can leave a platform game with no executable on record (the client starts it, so
        // nothing needed one); as a Local game it would have nothing to run.
        if (string.IsNullOrWhiteSpace(ExecutablePath))
        {
            ShowProblem("This game has no executable on record, so it couldn't start as a Local game. Browse to its .exe under Executable / Shortcut Target first, then convert.");
            return;
        }

        _convertedFrom = PlatformName;
        _convertToLocal = true;
        IsSteamGame = false;
        NotifyPlatformChanged();
        StatusMessage = $"Will be saved as a Local game (no longer linked to {_convertedFrom}). Click Save to apply, or Undo to keep the link.";
    });

    /// <summary>Takes back Convert to Local before it is saved: the link, and the Steam flag as the game has it, come back.</summary>
    public ICommand UndoConvertToLocalCommand => _undoConvertToLocalCommand ??= new RelayCommand(() =>
    {
        string platform = _convertedFrom ?? PlatformName;
        _convertToLocal = false;
        _convertedFrom = null;
        IsSteamGame = SourceGame.IsSteamGame;
        NotifyPlatformChanged();
        StatusMessage = $"Still linked to {platform}.";
    });

    private void NotifyPlatformChanged()
    {
        OnPropertyChanged(nameof(HasPlatform));
        OnPropertyChanged(nameof(IsConvertedToLocal));
        OnPropertyChanged(nameof(ConvertedNotice));
        OnPropertyChanged(nameof(PlatformDescription));
        OnPropertyChanged(nameof(CanOfferLaunchDirectly));
        OnPropertyChanged(nameof(CanOfferSteamLaunch));
        OnPropertyChanged(nameof(CanOfferSteamBadge));
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
