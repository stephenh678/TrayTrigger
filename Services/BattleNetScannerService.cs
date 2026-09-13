using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TrayTrigger.Services;

/// <param name="Uid">Battle.net's install uid (hs_beta, prometheus, s2) - the game's identity.</param>
/// <param name="ProgramId">The launch code for <c>--exec="launch &lt;X&gt;"</c> (WTCG, Pro, S2), or
/// null when neither the live catalog nor the saved code map had one - the game still imports
/// and the launcher looks again.</param>
public record DiscoveredBattleNetGame(
    string Uid,
    string Name,
    string InstallDir,
    string? ExePath,
    string? IconPath,
    string? ProgramId,
    bool IsAlreadyImported
);

/// <summary>Result of looking up a launch code: the code (or null), where it came from, and why when it's missing.</summary>
public readonly record struct BattleNetCodeLookup(string? ProgramId, string Source, string Detail)
{
    public bool Found => ProgramId != null;
}

/// <summary>
/// Discovers games installed through Battle.net. Each one has a Windows uninstall entry written
/// by Blizzard's uninstaller: <c>--uid=&lt;uid&gt;</c> in UninstallString, plus DisplayName,
/// InstallLocation and DisplayIcon (usually the game's exe, but a switcher for StarCraft II).
/// The client's own entry (uid <c>battle.net</c>) is skipped.
///
/// Launch codes come from Battle.net's own catalog cache (see <see cref="BattleNetCatalog"/>),
/// with TrayTrigger's saved copy (<see cref="BattleNetCodeStore"/>) as the fallback. Every read of
/// the catalog refreshes that saved copy. No per-title table exists anywhere.
/// </summary>
public partial class BattleNetScannerService
{
    private const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>The client's own uninstall entry - not a game.</summary>
    public const string ClientUid = "battle.net";

    /// <summary>Process name of the client UI (not the Agent, which is Battle.net's updater service).</summary>
    public const string ClientProcessName = "Battle.net";

    private const long MaxCatalogFileBytes = 5 * 1024 * 1024;

    private readonly string _cacheDirectory;
    private readonly BattleNetCodeStore _codeStore;
    private readonly FolderScannerService _folderScannerService = new();

    public static string DefaultCacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Battle.net", "Cache");

    public BattleNetScannerService() : this(DefaultCacheDirectory, new BattleNetCodeStore()) { }

    /// <summary>Test seam: a temp catalog folder and a temp code store.</summary>
    public BattleNetScannerService(string cacheDirectory, BattleNetCodeStore codeStore)
    {
        _cacheDirectory = cacheDirectory;
        _codeStore = codeStore;
    }

    internal sealed record InstallEntry(string Uid, string Name, string InstallDir, string? DisplayIcon);

    /// <summary>What one read of the catalog cache produced.</summary>
    internal sealed record CatalogSnapshot(bool CacheFolderExists, int CatalogFiles, Dictionary<string, HashSet<string>> Owners);

    // ------------------------------------------------------------------ client

    /// <summary>Battle.net.exe from the client's own uninstall entry, else its default folder; null if not installed.</summary>
    public string? GetClientPath()
    {
        try
        {
            var client = ReadUninstallEntries().FirstOrDefault(e => e.Uid.Equals(ClientUid, StringComparison.OrdinalIgnoreCase));
            if (client != null)
            {
                string exe = Path.Combine(client.InstallDir, "Battle.net.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("BattleNetScannerService", $"Could not read the Battle.net client's uninstall entry: {ex.Message}");
        }

        string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Battle.net", "Battle.net.exe");
        return File.Exists(fallback) ? fallback : null;
    }

    public bool IsClientInstalled() => GetClientPath() != null;

    public static bool IsClientRunning()
    {
        var processes = Process.GetProcessesByName(ClientProcessName);
        try { return processes.Length > 0; }
        finally { foreach (var p in processes) p.Dispose(); }
    }

    // ------------------------------------------------------------------ discovery

    public List<DiscoveredBattleNetGame> ScanInstalledGames(IEnumerable<string> existingUids)
    {
        var existingSet = new HashSet<string>(existingUids, StringComparer.OrdinalIgnoreCase);
        var results = new List<DiscoveredBattleNetGame>();

        try
        {
            var installs = ReadUninstallEntries()
                .Where(e => !e.Uid.Equals(ClientUid, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (installs.Count == 0) return results;

            var catalog = ReadCatalog();
            RememberCodes(catalog);

            foreach (var install in installs)
            {
                try
                {
                    if (!Directory.Exists(install.InstallDir)) continue;

                    var lookup = ResolveCode(install.Uid, catalog);
                    if (!lookup.Found)
                    {
                        LoggingService.Warn("BattleNetScannerService", $"No launch code for '{install.Name}' (uid {install.Uid}): {lookup.Detail}. It imports anyway; the launcher looks again.");
                    }

                    string? exe = ResolveExe(install);
                    results.Add(new DiscoveredBattleNetGame(
                        Uid: install.Uid,
                        Name: install.Name,
                        InstallDir: install.InstallDir,
                        ExePath: exe,
                        IconPath: exe,
                        ProgramId: lookup.ProgramId,
                        IsAlreadyImported: existingSet.Contains(install.Uid)));
                }
                catch (Exception ex)
                {
                    LoggingService.Warn("BattleNetScannerService", $"Error reading Battle.net install '{install.Uid}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("BattleNetScannerService", $"Error scanning installed Battle.net games: {ex.Message}");
        }

        return results.OrderBy(g => g.Name).ToList();
    }

    /// <summary>
    /// Looks up <paramref name="uid"/>'s launch code: Battle.net's live catalog first, then the
    /// saved code map. Refreshes the saved map from whatever the catalog holds.
    /// </summary>
    public BattleNetCodeLookup LookUpProgramId(string uid)
    {
        try
        {
            var catalog = ReadCatalog();
            RememberCodes(catalog);
            return ResolveCode(uid, catalog);
        }
        catch (Exception ex)
        {
            string? saved = _codeStore.Get(uid);
            return new BattleNetCodeLookup(saved, saved != null ? "saved code map" : "none", $"catalog read failed: {ex.Message}");
        }
    }

    internal BattleNetCodeLookup ResolveCode(string uid, CatalogSnapshot catalog)
    {
        string detail;
        if (catalog.CatalogFiles > 0)
        {
            string? live = BattleNetCatalog.Resolve(catalog.Owners, uid, out bool ambiguous);
            if (live != null) return new BattleNetCodeLookup(live, "Battle.net catalog", string.Empty);

            detail = ambiguous
                ? $"claimed by several products in Battle.net's catalog ({string.Join(", ", catalog.Owners[uid])})"
                : $"not in Battle.net's catalog ({catalog.CatalogFiles} catalog file(s) read)";
        }
        else
        {
            detail = catalog.CacheFolderExists
                ? $"no catalog files found in '{_cacheDirectory}' - the cache format may have changed"
                : $"Battle.net's cache folder '{_cacheDirectory}' doesn't exist - it's rebuilt the next time Battle.net starts";
        }

        string? saved = _codeStore.Get(uid);
        return saved != null
            ? new BattleNetCodeLookup(saved, "saved code map", detail)
            : new BattleNetCodeLookup(null, "none", detail);
    }

    private void RememberCodes(CatalogSnapshot catalog)
    {
        if (catalog.CatalogFiles == 0) return;
        int changed = _codeStore.Merge(BattleNetCatalog.Unambiguous(catalog.Owners));
        LoggingService.Verbose("BattleNetScannerService", $"Battle.net catalog: {catalog.CatalogFiles} catalog file(s), {catalog.Owners.Count} install uid(s); saved code map {(changed > 0 ? $"updated ({changed} change(s), {_codeStore.Count} total)" : $"unchanged ({_codeStore.Count} total)")}.");
    }

    internal CatalogSnapshot ReadCatalog()
    {
        var owners = BattleNetCatalog.NewOwnerMap();
        if (!Directory.Exists(_cacheDirectory)) return new CatalogSnapshot(false, 0, owners);

        int catalogFiles = 0;
        foreach (var path in Directory.EnumerateFiles(_cacheDirectory, "*", SearchOption.AllDirectories))
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length == 0 || info.Length > MaxCatalogFileBytes) continue;

                // The client may be writing its cache while this reads it.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.ReadByte() != '{') continue;
                stream.Position = 0;
                using var reader = new StreamReader(stream);
                if (BattleNetCatalog.MergeInto(owners, reader.ReadToEnd())) catalogFiles++;
            }
            catch (Exception ex)
            {
                LoggingService.Verbose("BattleNetScannerService", $"Skipped cache file '{path}': {ex.Message}");
            }
        }
        return new CatalogSnapshot(true, catalogFiles, owners);
    }

    private string? ResolveExe(InstallEntry install)
    {
        string? icon = ParseDisplayIconPath(install.DisplayIcon);
        if (icon != null && File.Exists(icon) && PlatformLookupService.IsPathUnderDirectory(icon, install.InstallDir))
        {
            return icon;
        }

        // No usable pointer: the same scored search Add Folder uses.
        return _folderScannerService.ScanFolder(install.InstallDir, preferExe: false).FirstOrDefault()?.ExePath;
    }

    internal static IEnumerable<InstallEntry> ReadUninstallEntries()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            List<InstallEntry> entries = new();
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var uninstall = baseKey.OpenSubKey(UninstallKeyPath);
                if (uninstall == null) continue;

                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(name);
                    if (key == null) continue;

                    string? uid = ParseUid(key.GetValue("UninstallString") as string);
                    if (uid == null) continue;

                    string? installDir = (key.GetValue("InstallLocation") as string)?.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
                    if (string.IsNullOrWhiteSpace(installDir)) continue;

                    string displayName = (key.GetValue("DisplayName") as string)?.Trim() is { Length: > 0 } n ? n : Path.GetFileName(installDir);
                    entries.Add(new InstallEntry(uid, displayName, installDir, key.GetValue("DisplayIcon") as string));
                }
            }
            catch (Exception ex)
            {
                LoggingService.Verbose("BattleNetScannerService", $"Could not read uninstall entries ({view}): {ex.Message}");
            }

            foreach (var entry in entries)
            {
                if (seen.Add(entry.Uid)) yield return entry;
            }
        }
    }

    /// <summary>
    /// The uid from a Battle.net uninstall command, or null if the command isn't Blizzard's
    /// uninstaller: <c>"...\Blizzard Uninstaller.exe" --lang=enUS --uid=hs_beta --displayname="Hearthstone"</c>.
    /// </summary>
    internal static string? ParseUid(string? uninstallString)
    {
        if (string.IsNullOrWhiteSpace(uninstallString)) return null;
        if (!uninstallString.Contains("Blizzard Uninstaller", StringComparison.OrdinalIgnoreCase)) return null;

        var match = UidArgumentRegex().Match(uninstallString);
        return match.Success ? match.Groups["uid"].Value : null;
    }

    /// <summary>A DisplayIcon value as a plain path: quotes and a trailing ",&lt;index&gt;" removed.</summary>
    internal static string? ParseDisplayIconPath(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;
        string value = displayIcon.Trim();
        if (value.StartsWith('"'))
        {
            int end = value.IndexOf('"', 1);
            value = end > 1 ? value[1..end] : value.Trim('"');
        }
        else
        {
            value = IconIndexSuffixRegex().Replace(value, string.Empty);
        }
        value = value.Replace('/', '\\').Trim();
        return value.Length > 0 ? value : null;
    }

    [GeneratedRegex(@"(?:^|\s)--uid=(?:""(?<uid>[^""]+)""|(?<uid>[^\s""]+))", RegexOptions.IgnoreCase)]
    private static partial Regex UidArgumentRegex();

    [GeneratedRegex(@",\s*-?\d+\s*$")]
    private static partial Regex IconIndexSuffixRegex();
}
