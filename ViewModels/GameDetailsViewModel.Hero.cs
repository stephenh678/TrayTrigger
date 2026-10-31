using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

/// <summary>The two pages of Game Details: what is known about the game, and its settings.</summary>
public enum GameDetailsTab
{
    Overview,
    Settings
}

/// <summary>
/// What Game Details needs from the library to edit a game on its Settings tab, so the page never
/// reaches into the rest of the app.
/// </summary>
/// <param name="CreateEditor">A fresh, clean editor for the game as it is saved now.</param>
/// <param name="Saved">Called after Save has written onto the game: the library persists it and refreshes its card.</param>
/// <param name="ScriptsEnabled">Settings' "Enable scripts" switch, read each time the Overview's cards are built.</param>
/// <param name="DefaultScriptsConfigured">Settings has default scripts turned on.</param>
public sealed record GameEditHost(
    Func<GameEntry, GameEditViewModel> CreateEditor,
    Action<GameEntry> Saved,
    Func<bool> ScriptsEnabled,
    Func<bool> DefaultScriptsConfigured);

/// <summary>Game Details' header (the figures and Play), its tabs, and the Settings tab's editor.</summary>
public partial class GameDetailsViewModel
{
    private readonly GameEditHost? _editHost;
    private GameDetailsTab _selectedTab;
    private GameEditViewModel? _editor;
    private string? _nameWhenEditorOpened;
    private bool _showLibraryTitle;

    /// <summary>The page can edit the game: it has a Settings tab. Tests and audits that open it bare have none.</summary>
    public bool CanEdit => _editHost != null;

    public ICommand SelectTabCommand { get; }

    public GameDetailsTab SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (value == GameDetailsTab.Settings && !CanEdit) return;
            if (!SetProperty(ref _selectedTab, value)) return;
            if (value == GameDetailsTab.Settings) EnsureEditor();
            OnPropertyChanged(nameof(IsOverviewTab));
            OnPropertyChanged(nameof(IsSettingsTab));
            // Read each time the Overview comes on show, so it shows the game as it was last saved.
            OnPropertyChanged(nameof(SetupCards));
        }
    }

    public bool IsOverviewTab => _selectedTab == GameDetailsTab.Overview;
    public bool IsSettingsTab => _selectedTab == GameDetailsTab.Settings;

    /// <summary>The Settings tab's editor, made when the tab is first opened and replaced by a clean one after Save or Cancel.</summary>
    public GameEditViewModel? EditViewModel => _editor;

    /// <summary>Edits are still open: leaving the page would lose them.</summary>
    public bool HasUnsavedEdits => _editor?.HasUnsavedChanges == true;

    /// <summary>
    /// Set by the window: true when nothing is open to lose, or when the player says to discard it.
    /// Play, Remove and Close Game ask before they close the page.
    /// </summary>
    public Func<bool>? ConfirmLeave { get; set; }

    private bool CanLeave() => ConfirmLeave?.Invoke() ?? true;

    /// <summary>Opens the Settings tab, on one of its own tabs when given.</summary>
    public void ShowSettings(GameEditSection? section = null)
    {
        if (!CanEdit) return;
        SelectedTab = GameDetailsTab.Settings;
        if (section is { } s && _editor != null) _editor.SelectedSection = s;
    }

    private void EnsureEditor()
    {
        if (_editHost == null || _editor != null) return;
        _editor = _editHost.CreateEditor(Game);
        _nameWhenEditorOpened = Game.Name;
        _editor.RequestClose += OnEditorClosed;
        OnPropertyChanged(nameof(EditViewModel));
    }

    /// <summary>Save (true) has written onto the game; Cancel (false) leaves it as it was. Either way the form starts again from the game as it is now.</summary>
    private void OnEditorClosed(bool saved)
    {
        if (saved && !string.Equals(Game.Name, _nameWhenEditorOpened, StringComparison.Ordinal))
        {
            _showLibraryTitle = true;
            _lastSessionRead = false;
        }
        if (_editor != null) _editor.RequestClose -= OnEditorClosed;
        _editor = null;
        // Shown as nothing for a moment first, so the Settings tab is built again for the new editor. A
        // panel handed a second editor would keep its controls, and a combo box whose list is replaced
        // writes its emptied selection back, leaving the clean editor looking edited.
        OnPropertyChanged(nameof(EditViewModel));
        EnsureEditor();
        if (_editor != null) _editor.StatusMessage = saved ? "Changes saved." : null;
        if (!saved) return;

        InvalidateCoverImageCache();
        _editHost?.Saved(Game);
        // The banner may have been chosen, or put back to automatic, in Settings.
        _ = LoadHeroAsync(replace: true);
        // Everything the page shows can have changed: the title, the category, the poster, the figures.
        OnPropertyChanged(string.Empty);
    }

    // --- The banner behind the header ---

    private readonly HeroArtService? _heroArt;
    private ImageSource? _heroImage;
    private string? _heroPath;
    private DateTime _heroStamp;
    private int _heroLookups;
    private string? _heroLookupAppId;

    /// <summary>The wide banner behind the game's name, once found; the page shows the blurred poster until then (and if there is none).</summary>
    public ImageSource? HeroImage => _heroImage;
    public bool HasHeroImage => _heroImage != null;

    /// <summary>
    /// Looks for the game's banner: the one chosen for it, then on disk, then Steam's, then SteamGridDB's. Called as the page
    /// opens, and again once the page has linked the game to a Steam listing, which gives it an App ID to look under. A
    /// listing the page only showed, as too weak a match to link, is not used: its banner would be another game's.
    /// </summary>
    internal async Task LoadHeroAsync(bool replace = false)
    {
        if (_heroArt == null || (_heroImage != null && !replace)) return;
        string? appId = Game.SteamAppId;
        // The page asks as it opens and again when its details arrive: a first look already on its way for the same listing is not started twice.
        if (!replace && _heroLookups > 0 && string.Equals(_heroLookupAppId, appId, StringComparison.Ordinal)) return;
        _heroLookups++;
        _heroLookupAppId = appId;
        try
        {
            string? path = await _heroArt.GetHeroPathAsync(Game, appId, _steamGridDbApiKey, _minConfidenceForRawg).ConfigureAwait(true);
            // Find again keeps the file's name and replaces what is in it, so the time it was written is part of what is shown.
            DateTime stamp = path == null ? default : WrittenAt(path);
            if (path == _heroPath && stamp == _heroStamp && (path != null || _heroImage == null)) return;
            if (path == null)
            {
                // The banner was taken away (Use automatic found none): back to the blurred poster.
                _heroPath = null;
                _heroStamp = default;
                _heroImage = null;
            }
            else
            {
                var image = await Task.Run(() => IconExtractorService.LoadBitmapSafely(path, decodePixelWidth: 1920)).ConfigureAwait(true);
                if (image == null) return;
                _heroPath = path;
                _heroStamp = stamp;
                _heroImage = image;
            }
            OnPropertyChanged(nameof(HeroImage));
            OnPropertyChanged(nameof(HasHeroImage));
            // The Settings tab's Banner card shows the same picture.
            _editor?.RefreshBannerPreview();
        }
        catch (Exception ex)
        {
            LoggingService.Warn("GameDetails", $"Error loading the banner for '{Game.Name}': {ex.Message}");
        }
        finally
        {
            _heroLookups--;
        }
    }

    private static DateTime WrittenAt(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file that cannot be asked about is shown as it was found.
            return default;
        }
    }

    // --- The header's figures: facts TrayTrigger already records, never measured results ---

    public string PlayedStat => Game.CumulativePlaytimeMinutes > 0 ? GameEntry.FormatPlaytime(Game.CumulativePlaytimeMinutes) : "None yet";

    public string LastPlayedStat => Game.LastPlayed.HasValue ? Game.LastPlayedDisplay : "Never";

    public string ProfileStat => Game.PerformanceProfile.ToString();

    public string StoreName => Game.PlatformLabel;

    /// <summary>The store and the genres, as the one quiet line under the name: "Steam · Action • RPG".</summary>
    public string HeroMetaLine => string.IsNullOrWhiteSpace(GenresDisplay) ? StoreName : $"{StoreName} · {GenresDisplay}";

    /// <summary>The text on the page is from this source; the About card's source menu says so.</summary>
    public string SourceName => IsRawgActive ? "RAWG" : "Steam";

    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);
        // Both are worked out from what changes below, so they follow it without each place that changes it knowing of them.
        if (propertyName is nameof(GenresDisplay) or nameof(IsRawgActive) or "")
        {
            base.OnPropertyChanged(nameof(HeroMetaLine));
            base.OnPropertyChanged(nameof(SourceName));
        }
    }

    /// <summary>The Overview's "Your setup" cards. Empty on a page that cannot edit, where a link to Settings would go nowhere.</summary>
    public IReadOnlyList<GameSetupCard> SetupCards =>
        _editHost == null
            ? Array.Empty<GameSetupCard>()
            : GameSetupSummary.For(Game, _editHost.ScriptsEnabled(), _editHost.DefaultScriptsConfigured());

    /// <summary>A card's button: the Settings tab, on the tab that edits that area.</summary>
    public ICommand EditSectionCommand => _editSectionCommand ??= new RelayCommand(p =>
    {
        if (p is GameEditSection section) ShowSettings(section);
    });
    private ICommand? _editSectionCommand;
}
