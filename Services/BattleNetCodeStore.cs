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
/// </summary>
public sealed class BattleNetCodeStore
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private Dictionary<string, string>? _codes;

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
        public Dictionary<string, string> Codes { get; set; } = new();
    }

    public int Count
    {
        get { lock (_lock) { return Load().Count; } }
    }

    /// <summary>The saved program ID for <paramref name="uid"/>, or null.</summary>
    public string? Get(string? uid)
    {
        if (string.IsNullOrWhiteSpace(uid)) return null;
        lock (_lock)
        {
            return Load().TryGetValue(uid, out var code) ? code : null;
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
            var current = Load();
            int changed = 0;
            foreach (var (uid, code) in codes)
            {
                if (string.IsNullOrWhiteSpace(uid) || !BattleNetCatalog.IsValidProgramId(code)) continue;
                if (current.TryGetValue(uid, out var existing) && string.Equals(existing, code, StringComparison.Ordinal)) continue;
                current[uid] = code;
                changed++;
            }

            if (changed > 0) Save(current);
            return changed;
        }
    }

    private Dictionary<string, string> Load()
    {
        if (_codes != null) return _codes;

        _codes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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

    private void Save(Dictionary<string, string> codes)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var model = new FileModel { UpdatedUtc = DateTime.UtcNow, Codes = new Dictionary<string, string>(codes, StringComparer.OrdinalIgnoreCase) };
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
