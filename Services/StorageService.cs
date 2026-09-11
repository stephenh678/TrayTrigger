using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

public class StorageService : IProfileSnapshotStore
{
    // Every save/load logs who triggered it via [CallerMemberName]/[CallerFilePath] - with ~30
    // call sites across the app (many of them generic AutoSaveSettings()/SaveLibrary() wrappers),
    // a bare "Saved settings" line gave no way to tell which of them fired without instrumenting
    // every call site by hand.
    private static string CallerTag(string callerFilePath, string callerMember) =>
        $"{Path.GetFileNameWithoutExtension(callerFilePath)}.{callerMember}";

    private readonly Lock _gamesLock = new();
    private readonly Lock _settingsLock = new();

    private readonly string _baseDirectory;
    private readonly string _localCacheDirectory;
    private readonly string _iconsDirectory;
    private readonly string _coversDirectory;
    private readonly string _gamesFilePath;
    private readonly string _gamesBakFilePath;
    private readonly string _settingsFilePath;
    private readonly string _settingsBakFilePath;
    private readonly string _profileSessionFilePath;

    private bool _gamesPrimaryUnreadableThisSession;
    private bool _settingsPrimaryUnreadableThisSession;

    public string? GamesLoadWarning { get; private set; }
    public string? SettingsLoadWarning { get; private set; }

    public StorageService()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrayTrigger"),
               Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrayTrigger"),
               migrateLegacyData: true)
    {
    }

    /// <summary>
    /// Test seam: every file this service reads or writes lives under the two given folders, so a
    /// test can point it at a temp directory and never touch the real %AppData% library. The
    /// legacy Documents\TrayTrigger migration is skipped there for the same reason.
    /// </summary>
    internal StorageService(string baseDirectory, string localCacheDirectory, bool migrateLegacyData = false)
    {
        _baseDirectory = baseDirectory;
        _localCacheDirectory = localCacheDirectory;
        _iconsDirectory = Path.Combine(_localCacheDirectory, "Icons");
        _coversDirectory = Path.Combine(_localCacheDirectory, "Covers");

        _gamesFilePath = Path.Combine(_baseDirectory, "games.json");
        _gamesBakFilePath = Path.Combine(_baseDirectory, "games.json.bak");
        _settingsFilePath = Path.Combine(_baseDirectory, "settings.json");
        _settingsBakFilePath = Path.Combine(_baseDirectory, "settings.json.bak");
        _profileSessionFilePath = Path.Combine(_baseDirectory, "profile-session.json");

        EnsureDirectories();
        if (migrateLegacyData) MigrateLegacyData();
    }

    public string BaseDirectory => _baseDirectory;
    public string LocalCacheDirectory => _localCacheDirectory;
    public string IconsDirectory => _iconsDirectory;
    public string CoversDirectory => _coversDirectory;

    public void EnsureDirectories()
    {
        if (!Directory.Exists(_baseDirectory))
        {
            Directory.CreateDirectory(_baseDirectory);
        }

        if (!Directory.Exists(_localCacheDirectory))
        {
            Directory.CreateDirectory(_localCacheDirectory);
        }

        if (!Directory.Exists(_iconsDirectory))
        {
            Directory.CreateDirectory(_iconsDirectory);
        }

        if (!Directory.Exists(_coversDirectory))
        {
            Directory.CreateDirectory(_coversDirectory);
        }
    }

    private void MigrateLegacyData()
    {
        try
        {
            string legacyDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TrayTrigger");
            if (!Directory.Exists(legacyDir)) return;

            string legacyGames = Path.Combine(legacyDir, "games.json");
            if (File.Exists(legacyGames) && !File.Exists(_gamesFilePath))
            {
                File.Copy(legacyGames, _gamesFilePath, overwrite: true);
                LoggingService.Info("Storage", "Migrated legacy games.json to %AppData%.");
            }

            string legacyGamesBak = Path.Combine(legacyDir, "games.json.bak");
            if (File.Exists(legacyGamesBak) && !File.Exists(_gamesBakFilePath))
            {
                File.Copy(legacyGamesBak, _gamesBakFilePath, overwrite: true);
            }

            string legacySettings = Path.Combine(legacyDir, "settings.json");
            if (File.Exists(legacySettings) && !File.Exists(_settingsFilePath))
            {
                File.Copy(legacySettings, _settingsFilePath, overwrite: true);
                LoggingService.Info("Storage", "Migrated legacy settings.json to %AppData%.");
            }

            string legacySettingsBak = Path.Combine(legacyDir, "settings.json.bak");
            if (File.Exists(legacySettingsBak) && !File.Exists(_settingsBakFilePath))
            {
                File.Copy(legacySettingsBak, _settingsBakFilePath, overwrite: true);
            }

            string legacyIcons = Path.Combine(legacyDir, "Icons");
            if (Directory.Exists(legacyIcons))
            {
                foreach (var file in Directory.GetFiles(legacyIcons))
                {
                    string target = Path.Combine(_iconsDirectory, Path.GetFileName(file));
                    if (!File.Exists(target))
                    {
                        try { File.Copy(file, target, overwrite: false); } catch { }
                    }
                }
            }

            string legacyCovers = Path.Combine(legacyDir, "Covers");
            if (Directory.Exists(legacyCovers))
            {
                foreach (var file in Directory.GetFiles(legacyCovers))
                {
                    string target = Path.Combine(_coversDirectory, Path.GetFileName(file));
                    if (!File.Exists(target))
                    {
                        try { File.Copy(file, target, overwrite: false); } catch { }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Storage", $"Error during legacy data migration: {ex.Message}");
        }
    }

    public List<GameEntry> LoadGames()
    {
        lock (_gamesLock)
        {
            EnsureDirectories();

            // 1. Try loading primary file
            if (File.Exists(_gamesFilePath))
            {
                try
                {
                    string json = File.ReadAllText(_gamesFilePath);
                    var games = JsonSerializer.Deserialize(json, AppJsonContext.Default.ListGameEntry);
                    if (games != null)
                    {
                        RemapLegacyPaths(games);
                        LoggingService.Verbose("Storage", $"Loaded {games.Count} game(s) from '{_gamesFilePath}'.");
                        return games;
                    }
                }
                catch (Exception ex)
                {
                    LoggingService.Warn("Storage", $"Primary games.json failed to parse: {ex.Message}. Attempting backup recovery...");
                    string archivePath = ArchiveCorruptFile(_gamesFilePath);
                    _gamesPrimaryUnreadableThisSession = true;
                    GamesLoadWarning = $"Your game library file could not be read and a copy was kept at '{archivePath}'.";
                }
            }

            // 2. Fall back to .bak if primary is missing or corrupt
            if (File.Exists(_gamesBakFilePath))
            {
                try
                {
                    string bakJson = File.ReadAllText(_gamesBakFilePath);
                    var bakGames = JsonSerializer.Deserialize(bakJson, AppJsonContext.Default.ListGameEntry);
                    if (bakGames != null)
                    {
                        RemapLegacyPaths(bakGames);
                        LoggingService.Info("Storage", $"Recovered {bakGames.Count} game(s) from '{_gamesBakFilePath}'.");
                        return bakGames;
                    }
                }
                catch (Exception ex)
                {
                    LoggingService.Error("Storage", $"Backup games.json.bak failed to parse: {ex.Message}", ex);
                    // Same treatment as the corrupt primary above: keep a timestamped copy so
                    // the only remaining trace of the library isn't destroyed by the next save's
                    // rolling-backup copy.
                    string bakArchive = ArchiveCorruptFile(_gamesBakFilePath);
                    GamesLoadWarning = (GamesLoadWarning ?? string.Empty) +
                        $" The backup could not be read either; a copy was kept at '{bakArchive}'.";
                }
            }

            LoggingService.Info("Storage", "No existing games library found. Initializing empty library.");
            if (_gamesPrimaryUnreadableThisSession)
            {
                GamesLoadWarning += " Your library could not be recovered and was reset to empty.";
            }
            return new List<GameEntry>();
        }
    }

    private void RemapLegacyPaths(List<GameEntry> games)
    {
        bool changed = false;
        foreach (var g in games)
        {
            // Auto-heal games that have a local executable path on disk but were erroneously marked as IsSteamGame
            if (g.IsSteamGame && !string.IsNullOrWhiteSpace(g.ExecutablePath) &&
                !g.ExecutablePath.StartsWith("steam://", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(g.ExecutablePath))
            {
                g.IsSteamGame = false;
                changed = true;
            }

            if (!string.IsNullOrEmpty(g.IconPath) && !File.Exists(g.IconPath))
            {
                string candidate = Path.Combine(_iconsDirectory, Path.GetFileName(g.IconPath));
                if (File.Exists(candidate))
                {
                    g.IconPath = candidate;
                    changed = true;
                }
            }
            if (!string.IsNullOrEmpty(g.CoverImagePath) && !File.Exists(g.CoverImagePath))
            {
                string candidate = Path.Combine(_coversDirectory, Path.GetFileName(g.CoverImagePath));
                if (File.Exists(candidate))
                {
                    g.CoverImagePath = candidate;
                    changed = true;
                }
            }
        }

        if (changed)
        {
            SaveGames(games);
            LoggingService.Info("Storage", "Persisted updated AppData paths in games.json.");
        }
    }

    public void SaveGames(IEnumerable<GameEntry> games, [CallerMemberName] string callerMember = "", [CallerFilePath] string callerFile = "")
    {
        lock (_gamesLock)
        {
            try
            {
                EnsureDirectories();
                var list = games.ToList();
                string json = JsonSerializer.Serialize(list, AppJsonContext.Default.ListGameEntry);
                string tempFile = _gamesFilePath + ".tmp";
                File.WriteAllText(tempFile, json);

                // Keep rolling .bak copy, unless the primary was found unreadable earlier this
                // session - in that case it must not overwrite a still-good backup. The flag
                // stays set for the whole session (it used to clear after one save, so the
                // *second* save copied the now-empty primary over the last good backup); the
                // rolling backup resumes on the next launch, once the primary loads cleanly.
                if (File.Exists(_gamesFilePath) && !_gamesPrimaryUnreadableThisSession)
                {
                    File.Copy(_gamesFilePath, _gamesBakFilePath, overwrite: true);
                }

                SafeReplaceFile(tempFile, _gamesFilePath);
                LoggingService.Verbose("Storage", $"Saved {list.Count} game(s) to '{_gamesFilePath}' (from {CallerTag(callerFile, callerMember)}).");
            }
            catch (Exception ex)
            {
                LoggingService.Error("Storage", $"Error saving games to '{_gamesFilePath}': {ex.Message}", ex);
            }
        }
    }

    public AppSettings LoadSettings([CallerMemberName] string callerMember = "", [CallerFilePath] string callerFile = "")
    {
        lock (_settingsLock)
        {
            EnsureDirectories();

            // 1. Try loading primary file
            if (File.Exists(_settingsFilePath))
            {
                try
                {
                    string json = File.ReadAllText(_settingsFilePath);
                    var settings = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppSettings);
                    if (settings != null)
                    {
                        settings.SteamGridDbApiKey = DecryptApiKey(settings.SteamGridDbApiKey);
                        settings.RawgApiKey = DecryptApiKey(settings.RawgApiKey);
                        LoggingService.Verbose("Storage", $"Loaded settings from '{_settingsFilePath}' (from {CallerTag(callerFile, callerMember)}).");
                        return settings;
                    }
                }
                catch (Exception ex)
                {
                    LoggingService.Warn("Storage", $"Primary settings.json failed to parse: {ex.Message}. Attempting backup recovery...");
                    string archivePath = ArchiveCorruptFile(_settingsFilePath);
                    _settingsPrimaryUnreadableThisSession = true;
                    SettingsLoadWarning = $"Your settings file could not be read and a copy was kept at '{archivePath}'.";
                }
            }

            // 2. Fall back to .bak
            if (File.Exists(_settingsBakFilePath))
            {
                try
                {
                    string bakJson = File.ReadAllText(_settingsBakFilePath);
                    var bakSettings = JsonSerializer.Deserialize(bakJson, AppJsonContext.Default.AppSettings);
                    if (bakSettings != null)
                    {
                        bakSettings.SteamGridDbApiKey = DecryptApiKey(bakSettings.SteamGridDbApiKey);
                        bakSettings.RawgApiKey = DecryptApiKey(bakSettings.RawgApiKey);
                        LoggingService.Info("Storage", $"Recovered settings from '{_settingsBakFilePath}'.");
                        return bakSettings;
                    }
                }
                catch (Exception ex)
                {
                    LoggingService.Error("Storage", $"Backup settings.json.bak failed to parse: {ex.Message}", ex);
                }
            }

            if (_settingsPrimaryUnreadableThisSession)
            {
                SettingsLoadWarning += " Your settings could not be recovered and were reset to defaults.";
            }
            var defaultSettings = new AppSettings();
            SaveSettings(defaultSettings);
            LoggingService.Info("Storage", "Created default settings file.");
            return defaultSettings;
        }
    }

    public void SaveSettings(AppSettings settings, [CallerMemberName] string callerMember = "", [CallerFilePath] string callerFile = "")
    {
        lock (_settingsLock)
        {
            // Encrypt the API key for the on-disk representation only; restore the
            // plaintext on the caller's live object afterward since it's still in active
            // use (bound to Settings UI, passed to SteamGridDB requests, etc.). See L-06.
            string plainApiKey = settings.SteamGridDbApiKey;
            string plainRawgKey = settings.RawgApiKey;
            try
            {
                EnsureDirectories();
                settings.SteamGridDbApiKey = EncryptApiKey(plainApiKey);
                settings.RawgApiKey = EncryptApiKey(plainRawgKey);
                string json = JsonSerializer.Serialize(settings, AppJsonContext.Default.AppSettings);
                string tempFile = _settingsFilePath + ".tmp";
                File.WriteAllText(tempFile, json);

                // Keep rolling .bak copy, unless the primary was found unreadable earlier this
                // session - in that case it must not overwrite a still-good backup.
                if (File.Exists(_settingsFilePath) && !_settingsPrimaryUnreadableThisSession)
                {
                    File.Copy(_settingsFilePath, _settingsBakFilePath, overwrite: true);
                }
                _settingsPrimaryUnreadableThisSession = false;

                SafeReplaceFile(tempFile, _settingsFilePath);
                LoggingService.Verbose("Storage", $"Saved settings to '{_settingsFilePath}' (from {CallerTag(callerFile, callerMember)}).");
            }
            catch (Exception ex)
            {
                LoggingService.Error("Storage", $"Error saving settings to '{_settingsFilePath}': {ex.Message}", ex);
            }
            finally
            {
                settings.SteamGridDbApiKey = plainApiKey;
                settings.RawgApiKey = plainRawgKey;
            }
        }
    }

    private readonly Lock _profileSessionLock = new();

    /// <summary>Null if no Performance Profile session is currently recorded (none active, or already restored).</summary>
    public PerformanceProfileSessionSnapshot? LoadProfileSessionSnapshot()
    {
        lock (_profileSessionLock)
        {
            if (!File.Exists(_profileSessionFilePath)) return null;
            try
            {
                string json = File.ReadAllText(_profileSessionFilePath);
                return JsonSerializer.Deserialize(json, AppJsonContext.Default.PerformanceProfileSessionSnapshot);
            }
            catch (Exception ex)
            {
                LoggingService.Warn("Storage", $"Failed to read '{_profileSessionFilePath}': {ex.Message}");
                return null;
            }
        }
    }

    public void SaveProfileSessionSnapshot(PerformanceProfileSessionSnapshot snapshot)
    {
        lock (_profileSessionLock)
        {
            try
            {
                EnsureDirectories();
                string json = JsonSerializer.Serialize(snapshot, AppJsonContext.Default.PerformanceProfileSessionSnapshot);
                string tempFile = _profileSessionFilePath + ".tmp";
                File.WriteAllText(tempFile, json);
                SafeReplaceFile(tempFile, _profileSessionFilePath);
            }
            catch (Exception ex)
            {
                LoggingService.Error("Storage", $"Error saving '{_profileSessionFilePath}': {ex.Message}", ex);
            }
        }
    }

    public void DeleteProfileSessionSnapshot()
    {
        lock (_profileSessionLock)
        {
            try
            {
                if (File.Exists(_profileSessionFilePath))
                {
                    File.Delete(_profileSessionFilePath);
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn("Storage", $"Failed to delete '{_profileSessionFilePath}': {ex.Message}");
            }
        }
    }

    private const string EncryptedApiKeyPrefix = "dpapi:";

    private static string EncryptApiKey(string plainKey)
    {
        if (string.IsNullOrEmpty(plainKey)) return string.Empty;
        try
        {
            byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainKey), null, DataProtectionScope.CurrentUser);
            return EncryptedApiKeyPrefix + Convert.ToBase64String(encrypted);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Storage", $"Failed to encrypt SteamGridDB API key, storing as-is: {ex.Message}");
            return plainKey;
        }
    }

    private static string DecryptApiKey(string storedKey)
    {
        if (string.IsNullOrEmpty(storedKey)) return string.Empty;

        // A value without the prefix is a legacy plain-text key from before this fix;
        // it will be encrypted the next time settings are saved.
        if (!storedKey.StartsWith(EncryptedApiKeyPrefix, StringComparison.Ordinal))
        {
            return storedKey;
        }

        try
        {
            byte[] encrypted = Convert.FromBase64String(storedKey[EncryptedApiKeyPrefix.Length..]);
            byte[] plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Storage", $"Failed to decrypt SteamGridDB API key: {ex.Message}");
            return string.Empty;
        }
    }

    private static string ArchiveCorruptFile(string path)
    {
        string archivePath = path + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            File.Copy(path, archivePath, overwrite: true);
            LoggingService.Warn("Storage", $"Archived unreadable file to '{archivePath}'.");
        }
        catch (Exception ex)
        {
            LoggingService.Error("Storage", $"Failed to archive unreadable file '{path}': {ex.Message}", ex);
        }
        return archivePath;
    }

    private static void SafeReplaceFile(string tempFile, string targetFile)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                File.Move(tempFile, targetFile, overwrite: true);
                return;
            }
            catch (IOException)
            {
                // Only the first two failures sleep-and-retry via Move; the third must fall
                // through to the Copy+Delete fallback below rather than propagate, or a
                // briefly-locked target (AV scan, backup tool) drops the save entirely.
                if (i < 2) Thread.Sleep(50);
            }
        }

        try
        {
            File.Copy(tempFile, targetFile, overwrite: true);
            try { File.Delete(tempFile); } catch { }
        }
        catch
        {
            // Re-throw so caller logs error if disk is genuinely unwriteable
            throw;
        }
    }
}
