using System.Collections.Generic;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>
/// Libraries imported before 1.4.2 kept ®, ™ and © in game names ("Overwatch®"), and only new
/// matches were cleaned - so the tray menu and every list showed them forever. Load strips them.
/// </summary>
public class StorageServiceNameCleanupTests
{
    [Fact]
    public void StripStoreSymbolsFromNames_CleansOnlyNamesThatCarryThem()
    {
        var games = new List<GameEntry>
        {
            new() { Name = "Overwatch®" },
            new() { Name = "STAR WARS Jedi: Fallen Order™" },
            new() { Name = "Tom Clancy’s Rainbow Six® Extraction" },
            new() { Name = "Fatekeeper" },
        };

        int changed = StorageService.StripStoreSymbolsFromNames(games);

        Assert.Equal(3, changed);
        Assert.Equal("Overwatch", games[0].Name);
        Assert.Equal("STAR WARS Jedi: Fallen Order", games[1].Name);
        Assert.Equal("Tom Clancy’s Rainbow Six Extraction", games[2].Name);
        Assert.Equal("Fatekeeper", games[3].Name);
    }

    [Fact]
    public void StripStoreSymbolsFromNames_NeverEmptiesAName()
    {
        // A name that is nothing but a symbol is left alone rather than blanked.
        var games = new List<GameEntry> { new() { Name = "™" } };

        Assert.Equal(0, StorageService.StripStoreSymbolsFromNames(games));
        Assert.Equal("™", games[0].Name);
    }
}
