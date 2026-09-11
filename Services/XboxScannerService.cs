using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace TrayTrigger.Services;

/// <param name="Aumid">Application User Model ID ("&lt;PackageFamilyName&gt;!&lt;AppId&gt;") - the
/// stable identity of the game across updates, and what shell activation launches.</param>
/// <param name="PackageFamilyName">"&lt;Name&gt;_&lt;PublisherHash&gt;" - stable across updates.</param>
/// <param name="PackageFullName">"&lt;Name&gt;_&lt;Version&gt;_&lt;Arch&gt;__&lt;PublisherHash&gt;" - changes on
/// every game update, so never store it; re-resolve from the AUMID when needed.</param>
/// <param name="InstallDir">Where the game's files really live. For a modern Xbox-app install
/// that's the junction target ("D:\XboxGames\&lt;Game&gt;\Content"), a plain readable folder that
/// survives updates; otherwise the package root itself.</param>
/// <param name="PackageRoot">The package folder under WindowsApps. Running game processes
/// report their image path through <em>this</em> path, not the junction target, so this is
/// what session tracking watches. Changes on every update.</param>
/// <param name="ExePath">The real game exe from MicrosoftGame.config, under
/// <paramref name="InstallDir"/>. Cannot be launched directly (needs package identity); kept
/// for icon extraction, dedupe against manually-added entries, and "open install folder".</param>
public record DiscoveredXboxGame(
    string Aumid,
    string PackageFamilyName,
    string PackageFullName,
    string Name,
    string InstallDir,
    string PackageRoot,
    string? ExePath,
    string? IconPath,
    bool IsAlreadyImported
);

/// <summary>
/// Discovers PC Game Pass / Microsoft Store games installed through the Xbox app. Only GDK
/// titles (the ones that ship a MicrosoftGame.config) are found: Windows' own Gaming Services
/// registers every one of those under
/// <c>HKLM\SOFTWARE\Microsoft\GamingServices\GameConfig\&lt;PackageFullName&gt;</c>, mirroring the
/// config's display name, publisher, executable list and application ID, so the scan is plain
/// registry reads with no WinRT dependency. Legacy UWP-era Store games have no such record and
/// there is no local way to tell a UWP game from any other UWP app, so they are not detected
/// (the Xbox app itself resolves that from the signed-in account's online library).
///
/// Install locations come from the per-user package repository
/// (<c>HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages</c>),
/// which is what Get-AppxPackage reads. Every field is derived from these two keys; the
/// package folder is only touched to pick an icon.
/// </summary>
public class XboxScannerService
{
    private const string GameConfigKeyPath = @"SOFTWARE\Microsoft\GamingServices\GameConfig";
    private const string PackageRepositoryKeyPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    /// <summary>Package family name of the Xbox app (Microsoft.GamingApp) - the "client" for
    /// <see cref="LauncherClientCloser"/> purposes. Not required to launch games; Gaming Services does that.</summary>
    public const string XboxAppPackageFamilyName = "Microsoft.GamingApp_8wekyb3d8bbwe";

    public List<DiscoveredXboxGame> ScanInstalledGames(IEnumerable<string> existingAumids)
    {
        var existingSet = new HashSet<string>(existingAumids, StringComparer.OrdinalIgnoreCase);
        var results = new List<DiscoveredXboxGame>();

        try
        {
            foreach (var game in EnumerateInstalledGames())
            {
                results.Add(game with { IsAlreadyImported = existingSet.Contains(game.Aumid) });
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("XboxScannerService", $"Error scanning installed Xbox games: {ex.Message}");
        }

        return results.OrderBy(g => g.Name).ToList();
    }

    /// <summary>
    /// The current record for an AUMID, or null if that game is no longer installed. Used at
    /// launch time because the package root (and therefore the path running processes report)
    /// changes with every game update.
    /// </summary>
    public DiscoveredXboxGame? FindByAumid(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return null;
        try
        {
            return EnumerateInstalledGames().FirstOrDefault(g => g.Aumid.Equals(aumid, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            LoggingService.Warn("XboxScannerService", $"Error resolving Xbox game '{aumid}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The installed game whose install directory or package root contains <paramref name="path"/>,
    /// or null. Used by <see cref="PlatformLookupService"/> so a folder dropped from
    /// "D:\XboxGames\&lt;Game&gt;" is imported as that game rather than as a Local exe (which
    /// could never launch anyway - GDK exes need package identity).
    /// </summary>
    public DiscoveredXboxGame? FindGameByPath(string path)
    {
        foreach (var game in EnumerateInstalledGames())
        {
            if (PlatformLookupService.IsPathUnderDirectory(path, game.InstallDir) ||
                PlatformLookupService.IsPathUnderDirectory(path, game.PackageRoot))
            {
                return game;
            }
        }
        return null;
    }

    private IEnumerable<DiscoveredXboxGame> EnumerateInstalledGames()
    {
        using var gameConfigKey = Registry.LocalMachine.OpenSubKey(GameConfigKeyPath);
        if (gameConfigKey == null) yield break;

        using var repositoryKey = Registry.CurrentUser.OpenSubKey(PackageRepositoryKeyPath);

        foreach (var packageFullName in gameConfigKey.GetSubKeyNames())
        {
            DiscoveredXboxGame? game = null;
            try
            {
                game = ParseGameConfig(gameConfigKey, repositoryKey, packageFullName);
            }
            catch (Exception ex)
            {
                LoggingService.Warn("XboxScannerService", $"Error parsing Xbox game config '{packageFullName}': {ex.Message}");
            }
            if (game != null) yield return game;
        }
    }

    private static DiscoveredXboxGame? ParseGameConfig(RegistryKey gameConfigKey, RegistryKey? repositoryKey, string packageFullName)
    {
        // A GameConfig entry with no per-user package registration belongs to another Windows
        // user (or is a leftover from an uninstall) - not launchable from this account.
        string? packageRoot = ReadPackageRoot(repositoryKey, packageFullName);
        if (packageRoot == null || !Directory.Exists(packageRoot)) return null;

        using var configKey = gameConfigKey.OpenSubKey(packageFullName);
        if (configKey == null) return null;

        string? familyName = ToPackageFamilyName(packageFullName);
        if (familyName == null) return null;

        // ExecutableList\Executable[0]: the game's own exe (relative) and the AppId that,
        // combined with the family name, forms the AUMID shell activation needs.
        string? appId = null;
        string? exeRelative = null;
        using (var executableKey = configKey.OpenSubKey("Executable"))
        {
            var first = executableKey?.GetSubKeyNames().OrderBy(n => n, StringComparer.Ordinal).FirstOrDefault();
            if (first != null)
            {
                using var exeKey = executableKey!.OpenSubKey(first);
                appId = exeKey?.GetValue("Id") as string;
                exeRelative = exeKey?.GetValue("Name") as string;
            }
        }
        if (string.IsNullOrWhiteSpace(appId)) return null;

        string? name = null;
        string? logoRelative = null;
        using (var visualsKey = configKey.OpenSubKey("ShellVisuals"))
        {
            name = visualsKey?.GetValue("DefaultDisplayName") as string;
            logoRelative = visualsKey?.GetValue("Square150x150Logo") as string
                ?? visualsKey?.GetValue("StoreLogo") as string;
        }
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
        {
            // Fall back to the package repository's already-resolved display name, then the
            // package name's last dotted segment ("436609B6.FortniteClient" -> "FortniteClient").
            name = ReadDisplayName(repositoryKey, packageFullName)
                ?? packageFullName.Split('_')[0].Split('.').Last();
        }

        string installDir = ResolveJunctionTarget(packageRoot) ?? packageRoot;
        string? exePath = string.IsNullOrWhiteSpace(exeRelative) ? null : SafeCombine(installDir, exeRelative);
        if (exePath != null && !File.Exists(exePath)) exePath = null;

        string? iconPath = FindLogo(installDir, logoRelative) ?? exePath;

        return new DiscoveredXboxGame(
            Aumid: $"{familyName}!{appId}",
            PackageFamilyName: familyName,
            PackageFullName: packageFullName,
            Name: name.Trim(),
            InstallDir: installDir,
            PackageRoot: packageRoot,
            ExePath: exePath,
            IconPath: iconPath,
            IsAlreadyImported: false);
    }

    private static string? ReadPackageRoot(RegistryKey? repositoryKey, string packageFullName)
    {
        using var packageKey = repositoryKey?.OpenSubKey(packageFullName);
        string? root = packageKey?.GetValue("PackageRootFolder") as string;
        return string.IsNullOrWhiteSpace(root) ? null : root.TrimEnd('\\');
    }

    private static string? ReadDisplayName(RegistryKey? repositoryKey, string packageFullName)
    {
        using var packageKey = repositoryKey?.OpenSubKey(packageFullName);
        string? name = packageKey?.GetValue("DisplayName") as string;
        return string.IsNullOrWhiteSpace(name) || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? null : name;
    }

    /// <summary>"Name_Version_Arch_ResourceId_PublisherHash" → "Name_PublisherHash".</summary>
    internal static string? ToPackageFamilyName(string packageFullName)
    {
        if (string.IsNullOrWhiteSpace(packageFullName)) return null;
        int firstUnderscore = packageFullName.IndexOf('_');
        int lastUnderscore = packageFullName.LastIndexOf('_');
        if (firstUnderscore <= 0 || lastUnderscore <= firstUnderscore || lastUnderscore == packageFullName.Length - 1) return null;
        return packageFullName.Substring(0, firstUnderscore) + "_" + packageFullName.Substring(lastUnderscore + 1);
    }

    /// <summary>The Xbox app installs modern titles to "&lt;drive&gt;:\XboxGames\&lt;Game&gt;\Content" and
    /// registers the WindowsApps package folder as a junction to it. Null when it's a real folder.</summary>
    private static string? ResolveJunctionTarget(string packageRoot)
    {
        try
        {
            var info = new DirectoryInfo(packageRoot);
            string? target = info.LinkTarget;
            if (string.IsNullOrWhiteSpace(target)) return null;
            target = target.TrimEnd('\\');
            return Directory.Exists(target) ? target : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeCombine(string root, string relative)
    {
        try
        {
            return Path.GetFullPath(Path.Combine(root, relative.Replace('/', '\\')));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The logo MicrosoftGame.config names, or its highest-resolution ".scale-N" variant when
    /// the plain file isn't shipped (both layouts occur in real packages).
    /// </summary>
    private static string? FindLogo(string installDir, string? logoRelative)
    {
        if (string.IsNullOrWhiteSpace(logoRelative)) return null;
        string? logo = SafeCombine(installDir, logoRelative);
        if (logo == null) return null;
        if (File.Exists(logo)) return logo;

        try
        {
            string? dir = Path.GetDirectoryName(logo);
            if (dir == null || !Directory.Exists(dir)) return null;
            string stem = Path.GetFileNameWithoutExtension(logo);
            string ext = Path.GetExtension(logo);
            return Directory.EnumerateFiles(dir, $"{stem}.scale-*{ext}")
                .Select(f => (Path: f, Scale: ParseScale(f)))
                .OrderByDescending(x => x.Scale)
                .Select(x => x.Path)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }

        static int ParseScale(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            int idx = name.LastIndexOf(".scale-", StringComparison.OrdinalIgnoreCase);
            return idx >= 0 && int.TryParse(name.AsSpan(idx + 7), out int scale) ? scale : 0;
        }
    }
}
