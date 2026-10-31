using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

/// <summary>
/// Edit Game > Identity and Library > Online match: which Steam listing this game is matched to, and what a new match
/// may change. It is one idea in place of Look Up, Fetch by ID and a bare App ID box.
/// </summary>
public partial class GameEditViewModel
{
    /// <summary>What a match found in the picker or by App ID may replace. Ticked to begin with; the banner and icon follow the match only while Automatic.</summary>
    public bool UpdateTitleFromMatch { get; set; } = true;
    public bool UpdateGenreFromMatch { get; set; } = true;
    public bool UpdatePosterFromMatch { get; set; } = true;

    public bool HasMatch => UrlProtocolHelper.IsValidSteamAppId(SteamAppId?.Trim());

    /// <summary>"The Blood of Dawnwalker · App ID 3751260", or the App ID alone until Steam's details are known.</summary>
    public string MatchSummary
    {
        get
        {
            string id = SteamAppId?.Trim() ?? string.Empty;
            if (!UrlProtocolHelper.IsValidSteamAppId(id)) return "Not matched to a Steam listing. A match gives this game Steam's title, genre, poster and banner.";
            return SteamMetadataService.TryGetCached(id, out var details) && details != null && !string.IsNullOrWhiteSpace(details.Name)
                ? $"{details.Name} · App ID {id}"
                : $"Steam App ID {id}";
        }
    }

    private bool _showAppIdEntry;

    /// <summary>The App ID box, which a refused ID brings into view.</summary>
    public bool ShowAppIdEntry
    {
        get => _showAppIdEntry;
        set => SetProperty(ref _showAppIdEntry, value);
    }

    private void NotifyMatchChanged()
    {
        OnPropertyChanged(nameof(HasMatch));
        OnPropertyChanged(nameof(MatchSummary));
        OnPropertyChanged(nameof(PosterChip));
    }

    public ICommand ChangeMatchCommand => _changeMatch ??= new RelayCommand(() => _ = ChangeMatchAsync(), () => !IsRefreshingMetadata);
    public ICommand EnterAppIdCommand => _enterAppId ??= new RelayCommand(() => ShowAppIdEntry = !ShowAppIdEntry);
    public ICommand RemoveMatchCommand => _removeMatch ??= new RelayCommand(RemoveMatch, () => HasMatch);
    private ICommand? _changeMatch, _enterAppId, _removeMatch;

    /// <summary>The same picker Game Details' Change match opens, pre-filled with this game's title.</summary>
    private async Task ChangeMatchAsync()
    {
        var search = new SteamSearchService();
        var dialog = new Views.GameMatchPickerDialog(
            "Change Steam Match",
            $"Pick the Steam store listing for “{Name}”. What is ticked under Online match is replaced with what it has.",
            Name,
            HasMatch ? SteamAppId!.Trim() : null,
            async (query, ct) =>
            {
                var matches = await search.SearchGamesAsync(query, ct);
                return new Views.MatchPickerResult(matches.Select(m => new Views.MatchPickerHit(m.AppId, m.Name, $"App ID {m.AppId}")).ToList(), null);
            });

        if (dialog.ShowDialog() != true || dialog.SelectedHit is not { Id: { Length: > 0 } appId }) return;

        // Before the App ID changes, so Undo can put it back.
        var before = CurrentLookUpState();
        SteamAppId = appId;
        await FetchBySteamIdCoreAsync(before);
    }

    private void RemoveMatch()
    {
        var before = CurrentLookUpState();
        SteamAppId = null;
        OfferUndo(before);
        RefreshBannerPreview();
        StatusMessage = "Match removed. The title, genre and artwork you have stay as they are; Undo puts the match back.";
    }
}
