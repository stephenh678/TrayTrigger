using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

public class GameIdRepairTests
{
    [Fact]
    public void UsableUniqueIds_AreLeftAlone()
    {
        var games = new List<GameEntry>
        {
            new() { Id = "0123456789abcdef0123456789abcdef", Name = "A" },
            new() { Id = "legacy-id_1", Name = "B" }
        };

        StorageService.RepairGameIds(games);

        Assert.Equal("0123456789abcdef0123456789abcdef", games[0].Id);
        Assert.Equal("legacy-id_1", games[1].Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData(@"..\..\Startup\evil")]
    [InlineData("a/b")]
    [InlineData("C:evil")]
    [InlineData("name?")]
    public void AnIdThatCannotBeAFileName_IsReplaced(string id)
    {
        var games = new List<GameEntry> { new() { Id = id, Name = "G" } };

        StorageService.RepairGameIds(games);

        Assert.NotEqual(id, games[0].Id);
        Assert.Equal(32, games[0].Id.Length);
        Assert.All(games[0].Id, c => Assert.True(char.IsAsciiHexDigit(c)));
    }

    [Fact]
    public void ARepeatedId_IsReplacedOnTheLaterEntry()
    {
        var games = new List<GameEntry>
        {
            new() { Id = "same", Name = "First" },
            new() { Id = "SAME", Name = "Second" }
        };

        StorageService.RepairGameIds(games);

        Assert.Equal("same", games[0].Id);
        Assert.NotEqual("SAME", games[1].Id);
    }

    [Fact]
    public void NullEntries_AreDropped()
    {
        var games = new List<GameEntry> { new() { Id = "a", Name = "A" }, null! };

        StorageService.RepairGameIds(games);

        Assert.Single(games);
    }
}
