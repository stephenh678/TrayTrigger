using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>What a restore did, for the dialog after the restart. Null fields mean it didn't get that far.</summary>
public sealed record RestoreResult(
    bool Succeeded,
    BackupManifest? Manifest,
    string? SafetyBackupPath,
    BackupPathFixer.Report? Paths,
    bool ApiKeysNeedReentry,
    string? Error);

/// <summary>
/// Settings › Diagnostics &amp; Storage › Backup &amp; Restore: TrayTrigger's own data in one .zip -
/// the library, settings, tools, scripts and cached art - to move to a new PC or undo a mistake.
/// Game saves are deliberately not part of it.
///
/// <para><b>Backing up</b> reads the files as they are on disk; the caller saves first, so they
/// hold what is on screen.</para>
///
/// <para><b>Restoring</b> takes two starts. The running TrayTrigger saves what it has to a safety
/// backup, unpacks the chosen backup into restore-pending\ and restarts; the next start puts the
/// files in place before anything reads them (<see cref="ApplyPendingRestore"/>). Replacing the
/// files under a running TrayTrigger would be undone by the next save of what it holds in memory,
/// and a half-loaded library is worse than either.</para>
///
/// <para>Some settings describe this PC, not a preference, and are kept from it rather than taken
/// from the backup: what each tweak found before it was applied, the window's position, Start with
/// Windows (the Run key is the truth for it), and API keys the backup's Windows account encrypted
/// and this one can't read. Paths are then pointed at where things are now - see
/// <see cref="BackupPathFixer"/>.</para>
/// </summary>
public sealed class BackupService
{
    internal const string ManifestEntry = "backup.json";
    internal const string StagingFolderName = "restore-pending";
    internal const string ReadyMarkerName = "ready.txt";
    private const string DataPrefix = "data/";
    private const string ScriptsPrefix = "data/Scripts/";
    private const string IconsPrefix = "cache/Icons/";
    private const string CoversPrefix = "cache/Covers/";

    /// <summary>The files at the top of %AppData%\TrayTrigger that make up a backup. The rest there is
    /// this PC's state (an active profile session, a removal in its undo window) or a .bak.</summary>
    internal static readonly string[] DataFiles = ["games.json", "settings.json", "tools.json", "battlenet-codes.json", ScriptLibraryService.ManifestFileName];

    /// <summary>Safety backups kept from before earlier restores; older ones are deleted.</summary>
    private const int SafetyBackupsKept = 5;
    /// <summary>No real backup comes near these; a file that does isn't one.</summary>
    private const long MaxEntryBytes = 256L * 1024 * 1024;
    private const long MaxTotalBytes = 1024L * 1024 * 1024;

    private readonly StorageService _storage;

    public BackupService(StorageService storage)
    {
        _storage = storage;
    }

    /// <summary>Where the safety backups made before a restore go: Backups\ in the data folder.</summary>
    public string SafetyBackupDirectory => Path.Combine(_storage.BaseDirectory, "Backups");

    public static string DefaultFileName(DateTime now) => $"TrayTrigger backup {now:yyyy-MM-dd}.zip";

    // ------------------------------------------------------------------ back up

    /// <summary>Writes a backup of what is on disk now to <paramref name="zipPath"/>, replacing any file there.</summary>
    public BackupManifest Create(string zipPath)
    {
        // Counts: what a file adds to the summary, so one left out of the backup is left out of the count too.
        var files = new List<(string Entry, string Path, char Counts)>();
        foreach (var name in DataFiles)
        {
            string path = Path.Combine(_storage.BaseDirectory, name);
            if (File.Exists(path)) files.Add((DataPrefix + name, path, '-'));
        }

        string scriptsDir = Path.Combine(_storage.BaseDirectory, "Scripts");
        int scripts = 0;
        if (Directory.Exists(scriptsDir))
        {
            // Counted for the summary: the user's own scripts, not TrayTrigger's copies or the
            // ".previous" versions of them it set aside.
            var bundled = new HashSet<string>(ScriptLibraryService.BundledFileNames(), StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(scriptsDir, "*", SearchOption.AllDirectories))
            {
                if (path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                string relative = Path.GetRelativePath(scriptsDir, path).Replace('\\', '/');
                string fileName = Path.GetFileName(path);
                bool own = !bundled.Contains(fileName) && !fileName.EndsWith(ScriptLibraryService.SetAsideSuffix, StringComparison.OrdinalIgnoreCase);
                files.Add((ScriptsPrefix + relative, path, own ? 's' : '-'));
                if (own) scripts++;
            }
        }

        int art = 0;
        foreach (var (prefix, dir) in new[] { (IconsPrefix, _storage.IconsDirectory), (CoversPrefix, _storage.CoversDirectory) })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var path in Directory.EnumerateFiles(dir))
            {
                files.Add((prefix + Path.GetFileName(path), path, 'a'));
                art++;
            }
        }

        var manifest = new BackupManifest
        {
            AppVersion = UpdateService.CurrentVersionDisplay,
            CreatedUtc = DateTime.UtcNow,
            MachineName = Environment.MachineName,
            DataDirectory = _storage.BaseDirectory,
            CacheDirectory = _storage.LocalCacheDirectory,
            UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Games = CountEntries(Path.Combine(_storage.BaseDirectory, "games.json"), isTools: false),
            Tools = CountEntries(Path.Combine(_storage.BaseDirectory, "tools.json"), isTools: true),
            Scripts = scripts,
            ArtFiles = art,
        };

        string temp = zipPath + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var (entryName, path, counts) in files)
                {
                    try
                    {
                        // The file is opened before its entry is made: an entry made first for a file
                        // that then can't be opened would be written as an empty file (a zip being
                        // created can't take an entry back), and a restore would put that empty file
                        // over the real one.
                        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        // Art is already compressed; squeezing it again only costs time.
                        var level = entryName.StartsWith("cache/", StringComparison.Ordinal) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                        var entry = zip.CreateEntry(entryName, level);
                        // A zip can only hold dates from 1980 to 2107; a file stamped outside them
                        // (a copy with no date, a clock set wrong) keeps the zip's default instead.
                        var modified = File.GetLastWriteTime(path);
                        if (modified.Year is >= 1980 and <= 2107) entry.LastWriteTime = modified;
                        using var target = entry.Open();
                        source.CopyTo(target);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // One unreadable icon must not cost the whole backup; the library is what matters.
                        if (entryName.StartsWith(DataPrefix, StringComparison.Ordinal) && !entryName.StartsWith(ScriptsPrefix, StringComparison.Ordinal)) throw;
                        LoggingService.Warn("Backup", $"Left '{path}' out of the backup: {ex.Message}");
                        if (counts == 's') manifest.Scripts--;
                        else if (counts == 'a') manifest.ArtFiles--;
                    }
                }

                // Written last, once it is known what went in. It is found by name, not by position.
                var manifestEntry = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
                using (var writer = manifestEntry.Open())
                {
                    JsonSerializer.Serialize(writer, manifest, AppJsonContext.Default.BackupManifest);
                }
            }
            File.Move(temp, zipPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { /* a stray temp file is harmless */ }
            throw;
        }

        LoggingService.Info("Backup", $"Backed up {manifest.Games} game(s), {manifest.Tools} tool(s), {manifest.Scripts} script(s) and {manifest.ArtFiles} art file(s) to '{zipPath}'.");
        return manifest;
    }

    private static int CountEntries(string path, bool isTools)
    {
        if (!File.Exists(path)) return 0;
        try
        {
            string json = File.ReadAllText(path);
            return isTools
                ? JsonSerializer.Deserialize(json, AppJsonContext.Default.ListToolEntry)?.Count ?? 0
                : JsonSerializer.Deserialize(json, AppJsonContext.Default.ListGameEntry)?.Count ?? 0;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            LoggingService.Verbose("Backup", $"Could not count the entries in '{path}': {ex.Message}");
            return 0;
        }
    }

    // ------------------------------------------------------------------ restore, part 1: before the restart

    /// <summary>
    /// What a backup file holds, or <see cref="InvalidDataException"/> with a sentence for the user
    /// when it isn't a backup TrayTrigger can restore.
    /// </summary>
    public static BackupManifest ReadManifest(string zipPath)
    {
        const string NotABackup = "This file isn't a TrayTrigger backup.";
        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(zipPath);
        }
        catch (InvalidDataException ex)
        {
            // Not a zip at all: "End of Central Directory record could not be found" says nothing useful.
            throw new InvalidDataException(NotABackup, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InvalidDataException($"This backup can't be read: {ex.Message}", ex);
        }

        using (zip)
        {
            var entry = zip.GetEntry(ManifestEntry) ?? throw new InvalidDataException(NotABackup);
            BackupManifest? manifest;
            try
            {
                using var stream = entry.Open();
                manifest = JsonSerializer.Deserialize(stream, AppJsonContext.Default.BackupManifest);
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
            {
                throw new InvalidDataException($"This backup is damaged and can't be read: {ex.Message}", ex);
            }
            if (manifest == null) throw new InvalidDataException(NotABackup);
            if (manifest.FormatVersion > BackupManifest.CurrentFormatVersion)
            {
                throw new InvalidDataException($"This backup was made by a newer TrayTrigger ({manifest.AppVersion}). Update TrayTrigger, then restore it.");
            }
            if (zip.GetEntry(DataPrefix + "games.json") == null || zip.GetEntry(DataPrefix + "settings.json") == null)
            {
                throw new InvalidDataException("This backup has no library or settings in it, so there is nothing to restore.");
            }
            return manifest;
        }
    }

    /// <summary>Backs up what is on disk now into <see cref="SafetyBackupDirectory"/>, keeping the last few. Returns its path.</summary>
    public string CreateSafetyBackup()
    {
        Directory.CreateDirectory(SafetyBackupDirectory);
        string path = Path.Combine(SafetyBackupDirectory, $"Before restore {DateTime.Now:yyyy-MM-dd HHmmss}.zip");
        Create(path);

        foreach (var old in new DirectoryInfo(SafetyBackupDirectory).GetFiles("Before restore *.zip")
                     .OrderByDescending(f => f.CreationTimeUtc).Skip(SafetyBackupsKept))
        {
            try
            {
                old.Delete();
                LoggingService.Verbose("Backup", $"Deleted the old safety backup '{old.FullName}'.");
            }
            catch (IOException ex)
            {
                LoggingService.Verbose("Backup", $"Could not delete the old safety backup '{old.FullName}': {ex.Message}");
            }
        }
        return path;
    }

    /// <summary>
    /// Unpacks the backup into restore-pending\, for the next start to put in place. Only the
    /// entries a backup is made of are taken, and none may land outside that folder, whatever the
    /// zip says its names are. The ready marker is written last: a restore cut short here is
    /// simply never applied.
    /// </summary>
    public void StageRestore(string zipPath, string safetyBackupPath)
    {
        string staging = Path.Combine(_storage.BaseDirectory, StagingFolderName);
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        string stagingRoot = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;

        try
        {
            long total = 0;
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                if (!IsBackupEntry(entry.FullName))
                {
                    LoggingService.Verbose("Restore", $"Skipped '{entry.FullName}' in the backup: not something a backup holds.");
                    continue;
                }
                if (entry.Length > MaxEntryBytes || (total += entry.Length) > MaxTotalBytes)
                {
                    throw new InvalidDataException("This backup is far larger than any TrayTrigger backup, so it wasn't restored.");
                }

                string target = Path.GetFullPath(Path.Combine(staging, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"This backup names a file outside TrayTrigger's folders ('{entry.FullName}'), so it wasn't restored.");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }

            File.WriteAllText(Path.Combine(staging, ReadyMarkerName), safetyBackupPath);
            LoggingService.Info("Restore", $"Unpacked '{zipPath}' for restoring at the next start; the data from before is in '{safetyBackupPath}'.");
        }
        catch
        {
            try { Directory.Delete(staging, recursive: true); }
            catch (IOException ex) { LoggingService.Verbose("Restore", $"Could not clear '{staging}' after a failed restore: {ex.Message}"); }
            throw;
        }
    }

    internal static bool IsBackupEntry(string name)
    {
        if (name == ManifestEntry) return true;
        if (name.Contains("..", StringComparison.Ordinal) || name.Contains('\\') || name.Contains(':')) return false;
        if (name.StartsWith(ScriptsPrefix, StringComparison.Ordinal)) return name.Length > ScriptsPrefix.Length;
        foreach (var prefix in new[] { IconsPrefix, CoversPrefix })
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal)) return name.Length > prefix.Length && name.IndexOf('/', prefix.Length) < 0;
        }
        return name.StartsWith(DataPrefix, StringComparison.Ordinal)
            && DataFiles.Contains(name[DataPrefix.Length..], StringComparer.Ordinal);
    }

    // ------------------------------------------------------------------ restore, part 2: the next start

    /// <summary>A restore is unpacked and waiting for this start to put it in place.</summary>
    public static bool HasPendingRestore(string baseDirectory) =>
        File.Exists(Path.Combine(baseDirectory, StagingFolderName, ReadyMarkerName));

    /// <summary>
    /// Puts an unpacked restore in place, before anything has read the library or settings. Never
    /// throws: a restore that fails partway says so in its result, and the safety backup holds what
    /// there was before. Either way restore-pending\ is cleared, so a failure isn't retried at every start.
    /// </summary>
    public RestoreResult ApplyPendingRestore(Func<IReadOnlyDictionary<(LauncherPlatform, string), (string, string?)>>? launcherInstalls = null)
    {
        string staging = Path.Combine(_storage.BaseDirectory, StagingFolderName);
        string? safety = null;
        BackupManifest? manifest = null;
        try
        {
            safety = File.ReadAllText(Path.Combine(staging, ReadyMarkerName)).Trim();
            using (var stream = File.OpenRead(Path.Combine(staging, ManifestEntry)))
            {
                manifest = JsonSerializer.Deserialize(stream, AppJsonContext.Default.BackupManifest);
            }

            string stagedData = Path.Combine(staging, "data");
            var restoredSettings = ReadSettings(Path.Combine(stagedData, "settings.json"))
                ?? throw new InvalidDataException("the backup's settings couldn't be read");
            var currentSettings = ReadSettings(Path.Combine(_storage.BaseDirectory, "settings.json"));
            bool keysLost = MergeMachineSettings(restoredSettings, currentSettings, StorageService.CanDecryptStoredKey);

            // The library and tools first, through the usual write; settings are written once the
            // paths in them are fixed too.
            CopyInto(Path.Combine(stagedData, "games.json"), Path.Combine(_storage.BaseDirectory, "games.json"));
            string stagedTools = Path.Combine(stagedData, "tools.json");
            if (File.Exists(stagedTools)) CopyInto(stagedTools, Path.Combine(_storage.BaseDirectory, "tools.json"));
            else File.WriteAllText(Path.Combine(_storage.BaseDirectory, "tools.json"), "[]");
            foreach (var name in new[] { "battlenet-codes.json", ScriptLibraryService.ManifestFileName })
            {
                string staged = Path.Combine(stagedData, name);
                if (File.Exists(staged)) CopyInto(staged, Path.Combine(_storage.BaseDirectory, name));
            }

            int scripts = CopyFolder(Path.Combine(stagedData, "Scripts"), Path.Combine(_storage.BaseDirectory, "Scripts"));
            int art = CopyFolder(Path.Combine(staging, "cache", "Icons"), _storage.IconsDirectory)
                + CopyFolder(Path.Combine(staging, "cache", "Covers"), _storage.CoversDirectory);

            var games = _storage.LoadGames();
            var tools = _storage.LoadTools();
            var report = BackupPathFixer.Fix(games, tools, restoredSettings, new BackupPathFixer.Context
            {
                OldDataDirectory = manifest?.DataDirectory,
                NewDataDirectory = _storage.BaseDirectory,
                OldCacheDirectory = manifest?.CacheDirectory,
                NewCacheDirectory = _storage.LocalCacheDirectory,
                OldUserProfile = manifest?.UserProfile,
                NewUserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                DriveRoots = DriveRoots(),
                LauncherInstalls = (launcherInstalls ?? ReadLauncherInstalls)(),
            });
            _storage.SaveGames(games);
            _storage.SaveTools(tools);
            // As stored, not through SaveSettings: the keys in it are still the backup's ciphertext,
            // which SaveSettings would take for plain text and encrypt a second time.
            WriteSettings(restoredSettings, Path.Combine(_storage.BaseDirectory, "settings.json"));

            LoggingService.Info("Restore", $"Restored {games.Count} game(s), {tools.Count} tool(s), {scripts} script file(s) and {art} art file(s) from the backup made {manifest?.CreatedUtc.ToLocalTime():g} on {manifest?.MachineName}. "
                + $"{report.Relinked} game(s) re-linked through their launcher, {report.Moved} path(s) moved, {report.StillMissing.Count} game(s) not found{(keysLost ? "; API keys need entering again" : "")}.");
            if (report.StillMissing.Count > 0 && LoggingService.IsVerboseEnabled)
            {
                LoggingService.Verbose("Restore", $"Not found on this PC: {string.Join(", ", report.StillMissing)}.");
            }
            return new RestoreResult(true, manifest, safety, report, keysLost, null);
        }
        catch (Exception ex)
        {
            LoggingService.Error("Restore", $"Restoring from the backup failed: {ex.Message}", ex);
            return new RestoreResult(false, manifest, safety, null, false, ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (IOException ex) { LoggingService.Warn("Restore", $"Could not clear '{staging}': {ex.Message}"); }
        }
    }

    /// <summary>
    /// Keeps the settings that describe this PC rather than a preference. Returns true when an API
    /// key in the backup can't be read here and there was no readable one to keep instead, so it
    /// was cleared and has to be entered again.
    /// </summary>
    internal static bool MergeMachineSettings(AppSettings restored, AppSettings? current, Func<string, bool> canDecrypt)
    {
        restored.TweakPriorState = current?.TweakPriorState ?? new Dictionary<string, string>();
        restored.MainWindowLeft = current?.MainWindowLeft;
        restored.MainWindowTop = current?.MainWindowTop;
        restored.MainWindowWidth = current?.MainWindowWidth;
        restored.MainWindowHeight = current?.MainWindowHeight;
        restored.MainWindowMaximized = current?.MainWindowMaximized ?? false;
        restored.StartWithWindows = current?.StartWithWindows ?? false;
        if (current != null)
        {
            // A one-time prompt already answered on either side stays answered.
            restored.HasSeenWelcomePrompt |= current.HasSeenWelcomePrompt;
            restored.HasSeenPerformanceProfileMigrationPrompt |= current.HasSeenPerformanceProfileMigrationPrompt;
            restored.HasSeenMetadataSourcesReminder |= current.HasSeenMetadataSourcesReminder;
            restored.HasSeenTrayHideNotice |= current.HasSeenTrayHideNotice;
            restored.HasSeenLauncherDetectionPrompt |= current.HasSeenLauncherDetectionPrompt;
            // A backup from before 1.4.8 would otherwise switch Tools back on after the restore,
            // over an "off" chosen since.
            restored.HasTurnedOnToolsFor148 |= current.HasTurnedOnToolsFor148;
            restored.SessionsStarted = Math.Max(restored.SessionsStarted, current.SessionsStarted);
        }

        bool lost = false;
        restored.SteamGridDbApiKey = KeepReadableKey(restored.SteamGridDbApiKey, current?.SteamGridDbApiKey, canDecrypt, ref lost);
        restored.RawgApiKey = KeepReadableKey(restored.RawgApiKey, current?.RawgApiKey, canDecrypt, ref lost);
        return lost;
    }

    private static string KeepReadableKey(string restored, string? current, Func<string, bool> canDecrypt, ref bool lost)
    {
        if (string.IsNullOrEmpty(restored) || canDecrypt(restored)) return restored;
        if (!string.IsNullOrEmpty(current) && canDecrypt(current)) return current;
        lost = true;
        return string.Empty;
    }

    private static AppSettings? ReadSettings(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize(File.ReadAllText(path), AppJsonContext.Default.AppSettings); }
        catch (JsonException ex)
        {
            LoggingService.Warn("Restore", $"'{path}' couldn't be read: {ex.Message}");
            return null;
        }
    }

    private static void WriteSettings(AppSettings settings, string path)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, AppJsonContext.Default.AppSettings));
        StorageService.SafeReplaceFile(temp, path);
    }

    private static void CopyInto(string source, string target)
    {
        string temp = target + ".tmp";
        File.Copy(source, temp, overwrite: true);
        StorageService.SafeReplaceFile(temp, target);
    }

    /// <summary>Copies every file under <paramref name="source"/> into <paramref name="target"/>, replacing same-named ones and leaving the rest. Returns how many.</summary>
    private static int CopyFolder(string source, string target)
    {
        if (!Directory.Exists(source)) return 0;
        int count = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
            count++;
        }
        return count;
    }

    private static List<string> DriveRoots()
    {
        var roots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable) roots.Add(drive.RootDirectory.FullName);
            }
            catch (IOException ex)
            {
                LoggingService.Verbose("Restore", $"Drive {drive.Name} couldn't be read: {ex.Message}");
            }
        }
        return roots;
    }

    /// <summary>Where GOG Galaxy, the EA app, Epic and Ubisoft Connect say each game is installed now.</summary>
    private static IReadOnlyDictionary<(LauncherPlatform, string), (string, string?)> ReadLauncherInstalls()
    {
        var installs = new Dictionary<(LauncherPlatform, string), (string, string?)>();
        void Read(LauncherPlatform platform, Func<IEnumerable<(string Id, string InstallDir, string? ExePath)>> scan)
        {
            try
            {
                foreach (var (id, dir, exe) in scan())
                {
                    if (!string.IsNullOrWhiteSpace(id)) installs[(platform, id)] = (dir, exe);
                }
            }
            catch (Exception ex)
            {
                LoggingService.Verbose("Restore", $"Couldn't ask {platform} where its games are: {ex.Message}");
            }
        }
        Read(LauncherPlatform.Gog, () => new GogScannerService().ScanInstalledGames([]).Select(g => (g.GameId, g.InstallDir, g.ExePath)));
        Read(LauncherPlatform.Ea, () => new EaScannerService().ScanInstalledGames([]).Select(g => (g.ContentId, g.InstallDir, g.ExePath)));
        Read(LauncherPlatform.Epic, () => new EpicScannerService().ScanInstalledGames([]).Select(g => (g.AppName, g.InstallDir, g.ExePath)));
        Read(LauncherPlatform.Ubisoft, () => new UbisoftScannerService().ScanInstalledGames([]).Select(g => (g.GameId, g.InstallDir, g.ExePath)));
        return installs;
    }
}
