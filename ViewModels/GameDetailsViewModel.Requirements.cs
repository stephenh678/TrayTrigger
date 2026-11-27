using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

/// <summary>
/// The Overview's PC system requirements card: Steam's Minimum or Recommended list as labelled rows, this
/// PC's part under each, and a warning only where the PC is sure to fall short (SystemRequirements). A list
/// with no labels the card knows shows Steam's text as it is.
/// </summary>
public partial class GameDetailsViewModel
{
    private PcSummary? _pcSummary;
    private SystemRequirements.InstallState _installState;

    public bool IsShowingMinimumReqs => !IsShowingRecommendedReqs;

    private ParsedRequirements CurrentParsedRequirements =>
        SystemRequirements.Parse(IsShowingRecommendedReqs ? _details?.PcRequirementsRec : _details?.PcRequirementsMin);

    /// <summary>The rows of the list showing, each with this PC's part once it has been read.</summary>
    public IReadOnlyList<RequirementRow> RequirementRows
    {
        get
        {
            var parsed = CurrentParsedRequirements;
            return parsed.HasRows ? SystemRequirements.Compare(parsed.Rows, _pcSummary, _installState) : [];
        }
    }

    /// <summary>The lines with no label, and Additional Notes, under the rows.</summary>
    public string RequirementNotes
    {
        get
        {
            var parsed = CurrentParsedRequirements;
            return parsed.HasRows ? string.Join("\n", parsed.Notes) : string.Empty;
        }
    }

    public bool HasRequirementRows => CurrentParsedRequirements.HasRows;
    public bool HasRequirementNotes => RequirementNotes.Length > 0;
    /// <summary>Steam's text as it is: for a list the card can't split into rows, or one with nothing in it.</summary>
    public bool ShowRequirementsText => !HasRequirementRows;

    private void NotifyRequirementsChanged()
    {
        foreach (string name in new[]
                 {
                     nameof(IsShowingRecommendedReqs), nameof(IsShowingMinimumReqs), nameof(CurrentRequirementsText),
                     nameof(RequirementRows), nameof(RequirementNotes), nameof(HasRequirementRows),
                     nameof(HasRequirementNotes), nameof(ShowRequirementsText),
                 })
            OnPropertyChanged(name);
    }

    /// <summary>Reads this PC's parts (the hardware once per run, free space and sound each time) and the game's install, off the UI thread.</summary>
    public async Task LoadPcSummaryAsync()
    {
        try
        {
            var game = Game;
            // Not installed is the launcher's answer, the one behind the Library's NOT INSTALLED badge: a game with no
            // folder TrayTrigger can see (one added from a Steam shortcut) is still installed.
            _installState = await Task.Run(() => new SystemRequirements.InstallState(
                SystemInfoService.GameDriveLetter(game),
                NotInstalled: InstalledGameIndex.Current.AvailabilityOf(game) == GameAvailability.NotInstalled));
            _pcSummary = await SystemInfoService.GetPcSummaryAsync();
            NotifyRequirementsChanged();
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("GameDetails", $"Couldn't read this PC's parts for the requirements card: {ex.Message}");
        }
    }
}
