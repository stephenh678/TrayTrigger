using System.IO;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// How a card takes an availability result, and in particular when it decides the question is
/// settled. Getting that wrong is silent: the marker simply never appears, and nothing in the log
/// says why, because as far as the card is concerned it already has an answer.
/// </summary>
public class GameCardAvailabilityTests : IDisposable
{
    private readonly InstalledGameIndex _indexBefore = InstalledGameIndex.Current;

    public void Dispose() => InstalledGameIndex.Current = _indexBefore;

    private static GameCardViewModel Card(GameEntry game) =>
        new(game, _ => { }, _ => { }, _ => { }, deferHeavyInit: true);

    private static GameEntry SteamGame(string appId = "1091500") =>
        new() { Name = "Cyberpunk 2077", IsSteamGame = true, SteamAppId = appId, ExecutablePath = $"steam://rungameid/{appId}" };

    /// <summary>
    /// The bug this guards: at startup the library's install pass and EnrichLibraryAsync run at
    /// the same time. Enrichment calls RefreshProperties on a card before the pass has captured
    /// anything, so the card would answer "available" from an index that had read nothing - and
    /// then treat that as settled, throwing away the real answer when it arrived.
    /// </summary>
    [Fact]
    public void ACheckAgainstTheStartingIndex_DoesNotBlockTheRealAnswer()
    {
        InstalledGameIndex.Current = InstalledGameIndex.Unknown;
        var card = Card(SteamGame());

        card.CheckAvailability();
        Assert.Equal(GameAvailability.Available, card.Availability);

        // The startup pass lands: Steam was read in full and does not have this game.
        card.ApplyHeavyState(GameAvailability.NotInstalled, null, null, null, null);

        Assert.True(card.IsNotInstalled);
    }

    /// <summary>The other half: an answer from an index that did read a launcher is authoritative,
    /// and a result computed before it must not overwrite it.</summary>
    [Fact]
    public void ACheckAgainstARealIndex_BlocksAnOlderAnswer()
    {
        InstalledGameIndex.Current = InstalledGameIndex.ForTest(steamAppIds: ["1091500"]);
        var card = Card(SteamGame());

        card.CheckAvailability();
        Assert.Equal(GameAvailability.Available, card.Availability);

        card.ApplyHeavyState(GameAvailability.NotInstalled, null, null, null, null);

        Assert.False(card.IsNotInstalled);
    }

    [Fact]
    public void AnAvailableGame_HasNoBadgeWordingAndAPlainTrayLabel()
    {
        var card = Card(new GameEntry { Name = "OpenTTD", ExecutablePath = "" });

        Assert.False(card.IsUnavailable);
        Assert.Equal(string.Empty, card.UnavailableLabel);
        Assert.Equal("OpenTTD", card.TrayMenuLabel);
    }

    [Fact]
    public void TheTwoMarkers_ReadDifferentlyOnTheBadgeAndInTheTray()
    {
        var card = Card(new GameEntry { Name = "OpenTTD", ExecutablePath = Path.Combine(Path.GetTempPath(), "nope", "openttd.exe") });

        card.Availability = GameAvailability.NotInstalled;
        Assert.Equal("NOT INSTALLED", card.UnavailableLabel);
        Assert.Contains("(not installed)", card.TrayMenuLabel);

        card.Availability = GameAvailability.ExecutableMissing;
        Assert.Equal("MISSING", card.UnavailableLabel);
        Assert.Contains("(missing)", card.TrayMenuLabel);
    }

    /// <summary>The tray menu label is built from Availability, so a change has to announce it -
    /// the card's own name never changed, and nothing else would tell a binding to look again.</summary>
    [Fact]
    public void ChangingAvailability_NotifiesEveryPropertyDerivedFromIt()
    {
        var card = Card(SteamGame());
        var seen = new List<string>();
        card.PropertyChanged += (_, e) => seen.Add(e.PropertyName ?? string.Empty);

        card.Availability = GameAvailability.NotInstalled;

        Assert.Contains(nameof(GameCardViewModel.Availability), seen);
        Assert.Contains(nameof(GameCardViewModel.IsMissing), seen);
        Assert.Contains(nameof(GameCardViewModel.IsNotInstalled), seen);
        Assert.Contains(nameof(GameCardViewModel.IsUnavailable), seen);
        Assert.Contains(nameof(GameCardViewModel.IsAvailable), seen);
        Assert.Contains(nameof(GameCardViewModel.UnavailableLabel), seen);
        Assert.Contains(nameof(GameCardViewModel.TrayMenuLabel), seen);
    }

    /// <summary>
    /// The removable-drive round trip: a game on a flash drive is marked missing while the drive
    /// is out, and pressing Play once it is back in has to start the game rather than send the
    /// user off to find a file that is sitting right where the entry already says it is.
    /// </summary>
    [Fact]
    public void AGameWhoseFileComesBack_IsPlayableAgainWithoutAScan()
    {
        string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "TrayTriggerTests", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string exe = Path.Combine(dir, "openttd.exe");
            var card = Card(new GameEntry { Name = "OpenTTD", ExecutablePath = exe });

            // Drive out.
            card.CheckAvailability();
            Assert.True(card.IsMissing);

            // Drive back in. Nothing has scanned, and the card still says missing...
            File.WriteAllBytes(exe, [1]);
            Assert.True(card.IsMissing);

            // ...until Play asks again, which is what LaunchGame does before refusing.
            card.Availability = InstalledGameIndex.Recheck(card.Game);
            Assert.True(card.IsAvailable);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) { Console.WriteLine($"Temp folder left behind: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Play re-asks the launcher before refusing, so a game reinstalled since the last scan still
    /// starts. For a local entry there is no launcher to ask and the file is the whole answer.
    /// </summary>
    [Fact]
    public void Recheck_AnswersForAnEntryWithNoLauncher()
    {
        string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "TrayTriggerTests", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string exe = Path.Combine(dir, "openttd.exe");
            File.WriteAllBytes(exe, [1]);

            Assert.Equal(GameAvailability.Available, InstalledGameIndex.Recheck(new GameEntry { Name = "OpenTTD", ExecutablePath = exe }));
            Assert.Equal(GameAvailability.ExecutableMissing, InstalledGameIndex.Recheck(
                new GameEntry { Name = "OpenTTD", ExecutablePath = Path.Combine(dir, "gone.exe") }));
            Assert.Equal(GameAvailability.NotInstalled, InstalledGameIndex.Recheck(
                new GameEntry { Name = "Witcher 3", IsGogGame = true, ExecutablePath = Path.Combine(dir, "gone.exe") }));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) { Console.WriteLine($"Temp folder left behind: {ex.Message}"); }
        }
    }
}
