using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

public class StorageService
{
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

    public StorageService()
    {
        _baseDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrayTrigger");
        _localCacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrayTrigger");
        _iconsDirectory = Path.Combine(_localCacheDirectory, "Icons");
        _coversDirectory = Path.Combine(_localCacheDirectory, "Covers");

        _gamesFilePath = Path.Combine(_baseDirectory, "games.json");
        _gamesBakFilePath = Path.Combine(_baseDirectory, "games.json.bak");
        _settingsFilePath = Path.Combine(_baseDirectory, "settings.json");
        _settingsBakFilePath = Path.Combine(_baseDirectory, "settings.json.bak");

        EnsureDirectories();
        MigrateLegacyData();
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
                }
            }

            LoggingService.Info("Storage", "No existing games library found. Initializing empty library.");
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

    public void SaveGames(IEnumerable<GameEntry> games)
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

                // Keep rolling .bak copy
                if (File.Exists(_gamesFilePath))
                {
                    File.Copy(_gamesFilePath, _gamesBakFilePath, overwrite: true);
                }

                SafeReplaceFile(tempFile, _gamesFilePath);
                LoggingService.Verbose("Storage", $"Saved {list.Count} game(s) to '{_gamesFilePath}'.");
            }
            catch (Exception ex)
            {
                LoggingService.Error("Storage", $"Error saving games to '{_gamesFilePath}': {ex.Message}", ex);
            }
        }
    }

    public AppSettings LoadSettings()
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
                        LoggingService.Verbose("Storage", $"Loaded settings from '{_settingsFilePath}'.");
                        return settings;
                    }
                }
                catch (Exception ex)
                {
                    LoggingService.Warn("Storage", $"Primary settings.json failed to parse: {ex.Message}. Attempting backup recovery...");
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
                        LoggingService.Info("Storage", $"Recovered settings from '{_settingsBakFilePath}'.");
                        return bakSettings;
                    }
                }
                catch (Exception ex)
                {
                    LoggingService.Error("Storage", $"Backup settings.json.bak failed to parse: {ex.Message}", ex);
                }
            }

            var defaultSettings = new AppSettings();
            SaveSettings(defaultSettings);
            LoggingService.Info("Storage", "Created default settings file.");
            return defaultSettings;
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        lock (_settingsLock)
        {
            try
            {
                EnsureDirectories();
                string json = JsonSerializer.Serialize(settings, AppJsonContext.Default.AppSettings);
                string tempFile = _settingsFilePath + ".tmp";
                File.WriteAllText(tempFile, json);

                // Keep rolling .bak copy
                if (File.Exists(_settingsFilePath))
                {
                    File.Copy(_settingsFilePath, _settingsBakFilePath, overwrite: true);
                }

                SafeReplaceFile(tempFile, _settingsFilePath);
                LoggingService.Verbose("Storage", $"Saved settings to '{_settingsFilePath}'.");
            }
            catch (Exception ex)
            {
                LoggingService.Error("Storage", $"Error saving settings to '{_settingsFilePath}': {ex.Message}", ex);
            }
        }
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
            catch (IOException) when (i < 2)
            {
                Thread.Sleep(50);
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
