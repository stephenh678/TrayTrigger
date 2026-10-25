using System;
using System.IO;
using System.Linq;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// Opt-in automatic backups: a <see cref="BackupService"/> backup into Backups\Automatic in the data
/// folder when the last backup of any kind is older than the chosen interval. Checked at startup, once
/// a day while TrayTrigger runs and when the last game exits; never while a game is running. A failed
/// attempt is tried again at the next check, three times a day at most, and is shown in Activity &amp; History.
/// </summary>
public sealed class AutoBackupService
{
    public const string FileNamePrefix = "TrayTrigger auto backup ";
    public const int MaxAttemptsPerDay = 3;
    internal const string ProblemKey = "autobackup-failed";

    /// <summary>What the Keep choice offers.</summary>
    public static readonly int[] KeepChoices = [3, 4, 6, 10];

    private readonly StorageService _storage;
    private readonly BackupService _backup;

    public AutoBackupService(StorageService storage)
    {
        _storage = storage;
        _backup = new BackupService(storage);
    }

    public string BackupFolder => Path.Combine(_storage.BaseDirectory, "Backups", "Automatic");

    /// <summary>A backup is wanted now: it is on, the interval has passed since the last backup of any kind, and today's attempts are not used up.</summary>
    public static bool IsDue(AppSettings settings, DateTime utcNow)
    {
        if (!settings.AutoBackupEnabled) return false;
        if (AttemptsToday(settings, utcNow) >= MaxAttemptsPerDay) return false;
        int days = Math.Max(1, settings.AutoBackupEveryDays);
        return settings.LastBackupUtc is not { } last || utcNow - last >= TimeSpan.FromDays(days);
    }

    /// <summary>When the next backup is due, for the card; null when there has been none (it runs at the next check).</summary>
    public static DateTime? NextDueLocal(AppSettings settings) =>
        settings.LastBackupUtc is { } last ? last.ToLocalTime().AddDays(Math.Max(1, settings.AutoBackupEveryDays)) : null;

    private static string Today(DateTime utcNow) => utcNow.ToLocalTime().ToString("yyyy-MM-dd");

    private static int AttemptsToday(AppSettings settings, DateTime utcNow) =>
        settings.AutoBackupAttemptDay == Today(utcNow) ? settings.AutoBackupAttemptsToday : 0;

    /// <summary>
    /// Takes the backup and updates <paramref name="settings"/> (the caller saves them). Never throws;
    /// returns an error sentence, or null when it worked. The caller has saved the library first.
    /// </summary>
    public string? Run(AppSettings settings, DateTime utcNow)
    {
        try
        {
            Directory.CreateDirectory(BackupFolder);
            string path = Path.Combine(BackupFolder, $"{FileNamePrefix}{utcNow.ToLocalTime():yyyy-MM-dd HHmm}.zip");
            var manifest = _backup.Create(path, settings.BackupIncludesActivity);

            settings.LastBackupUtc = utcNow;
            settings.LastAutoBackupUtc = utcNow;
            settings.AutoBackupLastError = string.Empty;
            settings.AutoBackupAttemptDay = string.Empty;
            settings.AutoBackupAttemptsToday = 0;

            int removed = Prune(Math.Max(1, settings.AutoBackupKeep));
            ActivityService.Current?.ClearState(ProblemKey);
            ActivityService.Add(ActivityLevel.Activity, "Automatic backup made",
                detail: $"{manifest.Games} game(s), {manifest.Tools} tool(s) and your settings{(manifest.HasActivity ? ", with Activity & History" : "")}, saved to {path}."
                    + (removed > 0 ? $" {removed} older automatic backup(s) deleted." : ""));
            LoggingService.Info("Backup", $"Automatic backup made: '{path}'.");
            return null;
        }
        catch (Exception ex)
        {
            string reason = Reason(ex);
            string day = Today(utcNow);
            settings.AutoBackupAttemptsToday = (settings.AutoBackupAttemptDay == day ? settings.AutoBackupAttemptsToday : 0) + 1;
            settings.AutoBackupAttemptDay = day;
            settings.AutoBackupLastError = reason;

            LoggingService.Warn("Backup", $"Automatic backup failed: {ex.Message}");
            ActivityService.Add(ActivityLevel.Problem, "The automatic backup didn't work",
                detail: $"{reason} TrayTrigger tries again at its next check, up to {MaxAttemptsPerDay} times a day.", groupKey: ProblemKey);
            ActivityService.Current?.SetState(new AttentionItem(ProblemKey, "The automatic backup didn't work",
                reason, null, null, IsCritical: false));
            return reason;
        }
    }

    /// <summary>Deletes the automatic backups beyond the newest <paramref name="keep"/>. Only this feature's own files are touched.</summary>
    internal int Prune(int keep)
    {
        if (!Directory.Exists(BackupFolder)) return 0;
        int removed = 0;
        foreach (var old in new DirectoryInfo(BackupFolder).GetFiles(FileNamePrefix + "*.zip")
                     .OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(keep))
        {
            try { old.Delete(); removed++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.Verbose("Backup", $"Could not delete the old automatic backup '{old.FullName}': {ex.Message}");
            }
        }
        return removed;
    }

    private static string Reason(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "TrayTrigger isn't allowed to write to its backup folder.",
        IOException io when io.HResult == unchecked((int)0x80070070) => "The disk is full.",
        IOException io => $"The backup couldn't be written: {io.Message}",
        _ => $"The backup couldn't be made: {ex.Message}",
    };
}
