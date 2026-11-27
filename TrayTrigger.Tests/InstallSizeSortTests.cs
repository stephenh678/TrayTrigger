using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// The saved install size behind the Library's Largest First sort: when a game's size is counted again, and that
/// the sort is offered. The count itself is <see cref="InstallSizeServiceTests"/>'.
/// </summary>
public class InstallSizeSortTests
{
    private static readonly DateTime Now = new(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AGameNeverCounted_IsCounted()
    {
        Assert.True(InstallSizeService.NeedsCount(new GameEntry { Name = "New" }, Now));
    }

    [Fact]
    public void ACountedGame_IsLeftAlone_UntilItIsPlayedOrTheCountIsOld()
    {
        var counted = new GameEntry { Name = "Game", InstallSizeBytes = 1_000, InstallSizeCountedUtc = Now.AddDays(-1), LastPlayed = Now.AddDays(-2).ToLocalTime() };
        Assert.False(InstallSizeService.NeedsCount(counted, Now));

        counted.LastPlayed = Now.AddHours(-1).ToLocalTime();
        Assert.True(InstallSizeService.NeedsCount(counted, Now));

        var old = new GameEntry { Name = "Old", InstallSizeBytes = 1_000, InstallSizeCountedUtc = Now - InstallSizeService.RecountAfter - TimeSpan.FromMinutes(1) };
        Assert.True(InstallSizeService.NeedsCount(old, Now));
    }

    [Fact]
    public void AGameWithNoFolder_IsRememberedAsCounted_NotTriedEachTime()
    {
        var noFolder = new GameEntry { Name = "Link", InstallSizeBytes = null, InstallSizeCountedUtc = Now.AddHours(-3) };
        Assert.False(InstallSizeService.NeedsCount(noFolder, Now));
    }

    [Fact]
    public void TheLibrary_OffersLargestFirst()
    {
        Assert.Equal("Size on Disk", LibraryViewModel.SortLargestFirst);
    }

    [Fact]
    public void BackgroundMode_CanBeEnteredAndLeft_OnAThreadOfItsOwn()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { using (InstallSizeService.BackgroundIo()) { } }
            catch (Exception ex) { error = ex; }
        });
        thread.Start();
        thread.Join();
        Assert.Null(error);
    }
}
