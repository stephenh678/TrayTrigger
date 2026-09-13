using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TrayTrigger.Services;

/// <summary>
/// TrayTrigger's own copy of the uid -> program ID pairs read from Battle.net's catalog, kept in
/// %APPDATA%\TrayTrigger\battlenet-codes.json next to games.json. It is a backup, used only when
/// the live catalog can't answer (cache cleared, or a client update changed its format): Battle.net
/// is always asked first. Entries are added and updated, never removed - a code that disappears
/// from Blizzard's catalog may still launch an older install. Nothing is shipped with the app;
/// the file fills only from the user's own Battle.net.
///
/// <c>Codes</c> mirrors Blizzard's whole catalog (every vendor, PTR and dev uid, several hundred
/// of them), because a game installed later must still resolve after the cache changes. That makes
/// it unreadable on its own, so <c>Installed</c> records the display name of every uid seen in
/// this machine's uninstall entries - the key a person needs to find, or hand-edit, the one line
/// that matters to them. Names come from the uninstall entries; Blizzard's catalog carries none.
/// </summary>
public sealed class BattleNetCodeStore
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private Dictionary<string, string>? _codes;
    private Dictionary<string, string>? _installed;

    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrayTrigger", "battlenet-codes.json");

    public BattleNetCodeStore() : this(DefaultFilePath) { }

    /// <summary>Test seam: point the store at a temp file.</summary>
    public BattleNetCodeStore(string filePath)
    {
        _filePath = filePath;
    }

    private sealed class FileModel
    {
        public int Version { get; set; } = 1;
        public DateTime UpdatedUtc { get; set; }
        /// <summary>uid -> display name, for the games installed on this machine. Written first so the file reads top-down.</summary>
        public Dictionary<string, string> Installed { get; set; } = new();
        /// <summary>uid -> program ID, for every uid Blizzard's catalog lists.</summary>
        public Dictionary<string, string> Codes { get; set; } = new();
    }

    public int Count
    {
        get { lock (_lock) { return LoadCodes().Count; } }
    }

    /// <summary>The saved program ID for <paramref name="uid"/>, or null.</summary>
    public string? Get(string? uid)
    {
        if (string.IsNullOrWhiteSpace(uid)) return null;
        lock (_lock)
        {
            return LoadCodes().TryGetValue(uid, out var code) ? code : null;
        }
    }

    /// <summary>The display name recorded for an installed <paramref name="uid"/>, or null.</summary>
    public string? GetInstalledName(string? uid)
    {
        if (string.IsNullOrWhiteSpace(uid)) return null;
        lock (_lock)
        {
            LoadCodes();
            return _installed!.TryGetValue(uid, out var name) ? name : null;
        }
    }

    /// <summary>
    /// Adds new pairs and updates changed ones; never removes. Writes the file only when something
    /// changed. Returns how many entries were added or updated.
    /// </summary>
    public int Merge(IReadOnlyDictionary<string, string> codes)
    {
        lock (_lock)
        {
            var current = LoadCodes();
            int changed = 0;
            foreach (var (uid, code) in codes)
            {
                if (string.IsNullOrWhiteSpace(uid) || !BattleNetCatalog.IsValidProgramId(code)) continue;
                if (current.TryGetValue(uid, out var existing) && string.Equals(existing, code, StringComparison.Ordinal)) continue;
                current[uid] = code;
                changed++;
            }

            if (changed > 0) Save();
            return changed;
        }
    }

    /// <summary>
    /// Records the display name of each uid found installed (from Blizzard's uninstall entries).
    /// Like <see cref="Merge"/>: adds and updates, never removes - an uninstalled game's line is
    /// still the one a person would look for - and writes only on change. Returns how many
    /// names were added or updated.
    /// </summary>
    public int RememberInstalled(IEnumerable<(string Uid, string Name)> installed)
    {
        lock (_lock)
        {
            LoadCodes();
            int changed = 0;
            foreach (var (uid, name) in installed)
            {
                if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(name)) continue;
                if (_installed!.TryGetValue(uid, out var existing) && string.Equals(existing, name, StringComparison.Ordinal)) continue;
                _installed[uid] = name;
                changed++;
            }

            if (changed > 0) Save();
            return changed;
        }
    }

    /// <summary>Loads both maps on first use; returns the code map. Callers hold <see cref="_lock"/>.</summary>
    private Dictionary<string, string> LoadCodes()
    {
        if (_codes != null) return _codes;

        _codes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(_filePath))
            {
                var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_filePath));
                if (model?.Codes != null)
                {
                    foreach (var (uid, code) in model.Codes)
                    {
                        if (!string.IsNullOrWhiteSpace(uid) && BattleNetCatalog.IsValidProgramId(code)) _codes[uid] = code;
                    }
                }
                if (model?.Installed != null)
                {
                    foreach (var (uid, name) in model.Installed)
                    {
                        if (!string.IsNullOrWhiteSpace(uid) && !string.IsNullOrWhiteSpace(name)) _installed[uid] = name;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // An unreadable backup is not worth failing a scan or launch over; the live catalog
            // still works, and the next successful read rewrites the file.
            LoggingService.Warn("BattleNetCodeStore", $"Could not read '{_filePath}': {ex.Message}");
        }
        return _codes;
    }

    /// <summary>Writes both maps. Callers hold <see cref="_lock"/> and have loaded.</summary>
    private void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var model = new FileModel
            {
                UpdatedUtc = DateTime.UtcNow,
                Installed = new Dictionary<string, string>(_installed!, StringComparer.OrdinalIgnoreCase),
                Codes = new Dictionary<string, string>(_codes!, StringComparer.OrdinalIgnoreCase)
            };
            string tempPath = _filePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("BattleNetCodeStore", $"Could not save '{_filePath}': {ex.Message}");
        }
    }
}
