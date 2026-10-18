using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>What the Game Details card does with a lookup's answer.</summary>
public class PcGamingWikiCardTests
{
    private static PcGamingWikiFacts Facts(string title) => new() { PageTitle = title };

    [Fact]
    public void AFoundAnswer_ReplacesWhatTheCardShows()
    {
        var result = new PcGamingWikiResult(PcGamingWikiStatus.Found, Facts("New"));

        Assert.Equal("New", GameDetailsViewModel.FactsAfter(result, Facts("Old"))!.PageTitle);
    }

    [Fact]
    public void ANoFactsAnswer_TakesTheCardAway()
    {
        Assert.Null(GameDetailsViewModel.FactsAfter(PcGamingWikiResult.NoFacts, Facts("Old")));
    }

    [Fact]
    public void AFailedLookup_LeavesTheCardAsItWas()
    {
        // Pressing Refresh with no network must not take away the facts already on the card.
        var showing = Facts("Old");

        Assert.Same(showing, GameDetailsViewModel.FactsAfter(PcGamingWikiResult.Failed, showing));
        Assert.Null(GameDetailsViewModel.FactsAfter(PcGamingWikiResult.Failed, null));
    }
}
