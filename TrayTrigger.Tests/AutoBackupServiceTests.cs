using System.IO;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>Automatic backups: when one is due, that a manual backup resets the clock, what is kept, and what a failure records.</summary>
[Collection(ActivityCurrentCollection.Name)]
public class AutoBackupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerAutoBackup", Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        ActivityService.Current = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private StorageService Storage() => new(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"));

    [Fact]
    public void NothingIsDue_UntilItIsTurnedOn()
    {
        Assert.False(AutoBackupService.IsDue(new AppSettings(), Now));
        Assert.True(AutoBackupService.IsDue(new AppSettings { AutoBackupEnabled = true }, Now));
    }

    [Fact]
    public void ItIsWeeklyAndKeepsFourByDefault()
    {
        var settings = new AppSettings();
        Assert.Equal(7, settings.AutoBackupEveryDays);
        Assert.Equal(4, settings.AutoBackupKeep);
        Assert.False(settings.AutoBackupEnabled);
    }

    [Fact]
    public void ABackupIsDueOnceTheIntervalHasPassed_AndAManualOneResetsTheClock()
    {
        var settings = new AppSettings { AutoBackupEnabled = true, LastBackupUtc = Now.AddDays(-6) };
        Assert.False(AutoBackupService.IsDue(settings, Now));

        settings.LastBackupUtc = Now.AddDays(-7);
        Assert.True(AutoBackupService.IsDue(settings, Now));

        // A backup made by hand sets LastBackupUtc only, and that is what the check goes by.
        settings.LastAutoBackupUtc = Now.AddDays(-30);
        settings.LastBackupUtc = Now.AddDays(-1);
        Assert.False(AutoBackupService.IsDue(settings, Now));
    }

    [Theory]
    [InlineData(14, 13, false)]
    [InlineData(14, 14, true)]
    [InlineData(30, 29, false)]
    [InlineData(30, 30, true)]
    public void TheChosenIntervalIsUsed(int everyDays, int daysAgo, bool due)
    {
        var settings = new AppSettings { AutoBackupEnabled = true, AutoBackupEveryDays = everyDays, LastBackupUtc = Now.AddDays(-daysAgo) };
        Assert.Equal(due, AutoBackupService.IsDue(settings, Now));
    }

    [Fact]
    public void ARun_MakesTheBackup_ResetsTheClock_AndClearsAnEarlierFailure()
    {
        var storage = Storage();
        storage.SaveGames([new GameEntry { Id = "g1", Name = "Hades" }]);
        storage.SaveSettings(new AppSettings());
        var settings = new AppSettings { AutoBackupEnabled = true, AutoBackupLastError = "old", AutoBackupAttemptDay = "2026-10-24", AutoBackupAttemptsToday = 2 };

        var service = new AutoBackupService(storage);
        Assert.Null(service.Run(settings, Now));

        var file = Assert.Single(Directory.GetFiles(service.BackupFolder, AutoBackupService.FileNamePrefix + "*.zip"));
        Assert.Equal(1, BackupService.ReadManifest(file).Games);
        Assert.Equal(Now, settings.LastBackupUtc);
        Assert.Equal(Now, settings.LastAutoBackupUtc);
        Assert.Equal(string.Empty, settings.AutoBackupLastError);
        Assert.Equal(0, settings.AutoBackupAttemptsToday);
    }

    [Fact]
    public void ARun_KeepsOnlyTheNewest_AndLeavesEveryOtherFileAlone()
    {
        var storage = Storage();
        storage.SaveGames([]);
        storage.SaveSettings(new AppSettings());
        var service = new AutoBackupService(storage);
        Directory.CreateDirectory(service.BackupFolder);
        foreach (var day in new[] { "2026-10-01", "2026-10-08", "2026-10-15" })
            File.WriteAllText(Path.Combine(service.BackupFolder, $"{AutoBackupService.FileNamePrefix}{day} 1200.zip"), "x");
        string mine = Path.Combine(service.BackupFolder, "my own copy.zip");
        File.WriteAllText(mine, "x");

        var settings = new AppSettings { AutoBackupEnabled = true, AutoBackupKeep = 3 };
        Assert.Null(service.Run(settings, Now));

        var kept = Directory.GetFiles(service.BackupFolder, AutoBackupService.FileNamePrefix + "*.zip").Select(Path.GetFileName).Order().ToList();
        Assert.Equal(3, kept.Count);
        Assert.DoesNotContain(kept, n => n!.Contains("2026-10-01"));
        Assert.True(File.Exists(mine));
    }

    [Fact]
    public void AFailure_IsCounted_StopsAtThreeAttemptsADay_AndRecordsAProblem()
    {
        var storage = Storage();
        storage.SaveGames([]);
        storage.SaveSettings(new AppSettings());
        var activity = new ActivityService(Path.Combine(_root, "roaming"), readOnly: false);
        ActivityService.Current = activity;
        // A file where the folder should be, so the folder can't be made.
        Directory.CreateDirectory(Path.Combine(_root, "roaming"));
        File.WriteAllText(Path.Combine(_root, "roaming", "Backups"), "in the way");

        var settings = new AppSettings { AutoBackupEnabled = true };
        var service = new AutoBackupService(storage);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(AutoBackupService.IsDue(settings, Now));
            Assert.NotNull(service.Run(settings, Now));
        }

        Assert.False(AutoBackupService.IsDue(settings, Now));
        Assert.NotEmpty(settings.AutoBackupLastError);
        Assert.Contains(activity.Entries, e => e.Level == ActivityLevel.Problem && e.Text.Contains("automatic backup"));
        Assert.Contains(activity.States, s => s.Key == AutoBackupService.ProblemKey);
        // The next day it is tried again.
        Assert.True(AutoBackupService.IsDue(settings, Now.AddDays(1)));
    }

    [Fact]
    public void ASuccess_ClearsTheAttentionItem()
    {
        var storage = Storage();
        storage.SaveGames([]);
        storage.SaveSettings(new AppSettings());
        var activity = new ActivityService(Path.Combine(_root, "roaming"), readOnly: false);
        ActivityService.Current = activity;
        activity.SetState(new AttentionItem(AutoBackupService.ProblemKey, "x", "y", null, null, false));

        Assert.Null(new AutoBackupService(storage).Run(new AppSettings { AutoBackupEnabled = true }, Now));

        Assert.DoesNotContain(activity.States, s => s.Key == AutoBackupService.ProblemKey);
        Assert.Contains(activity.Entries, e => e.Text == "Automatic backup made");
    }

    [Fact]
    public void ARestore_KeepsThisPcsBackupDates()
    {
        var current = new AppSettings { LastBackupUtc = Now, LastAutoBackupUtc = Now, AutoBackupLastError = "disk full" };
        var restored = new AppSettings { AutoBackupEnabled = true, LastBackupUtc = Now.AddYears(-1) };
        BackupService.MergeMachineSettings(restored, current, _ => true);

        Assert.True(restored.AutoBackupEnabled);
        Assert.Equal(Now, restored.LastBackupUtc);
        Assert.Equal(Now, restored.LastAutoBackupUtc);
        Assert.Equal("disk full", restored.AutoBackupLastError);
    }
}
