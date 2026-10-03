using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// After a restore: points the paths in the restored library, tools and settings at where things
/// are on this PC. Pure apart from the lookups it is handed, so every case can be tested.
///
/// <para>In order, for each path that isn't on disk as it stands:</para>
/// <list type="number">
///   <item>A launcher game (GOG, EA, Epic, Ubisoft) is re-linked by its launcher ID, to where that
///   launcher says it is installed now.</item>
///   <item>A path under the old TrayTrigger data or cache folder, or the old Windows user folder, is
///   moved to this PC's - a new PC, or a new Windows account.</item>
///   <item>A path on a drive letter that changed is looked for on every other drive, and taken when
///   exactly one has it.</item>
/// </list>
/// <para>What still can't be found is reported by name; it keeps its path, so the MISSING and NOT
/// INSTALLED badges and Locate Executable work on it as usual.</para>
/// </summary>
public static class BackupPathFixer
{
    /// <summary>Where things were on the PC the backup was made on, and how to look on this one.</summary>
    public sealed class Context
    {
        public string? OldDataDirectory { get; init; }
        public required string NewDataDirectory { get; init; }
        public string? OldCacheDirectory { get; init; }
        public required string NewCacheDirectory { get; init; }
        public string? OldUserProfile { get; init; }
        public required string NewUserProfile { get; init; }
        public Func<string, bool> FileExists { get; init; } = File.Exists;
        public Func<string, bool> DirectoryExists { get; init; } = Directory.Exists;
        /// <summary>"C:\", "D:\", ... - the drives a path whose letter changed is looked for on.</summary>
        public IReadOnlyList<string> DriveRoots { get; init; } = Array.Empty<string>();
        /// <summary>Where each launcher says its games are installed now, by platform and launcher ID.</summary>
        public IReadOnlyDictionary<(LauncherPlatform Platform, string Id), (string InstallDir, string? ExePath)> LauncherInstalls { get; init; }
            = new Dictionary<(LauncherPlatform, string), (string, string?)>();
    }

    public sealed class Report
    {
        /// <summary>Launcher games found where their launcher has them now.</summary>
        public int Relinked { get; set; }
        /// <summary>Paths moved to another drive letter, or this PC's user or TrayTrigger folders.</summary>
        public int Moved { get; set; }
        /// <summary>Games whose executable still isn't on this PC.</summary>
        public List<string> StillMissing { get; } = new();
    }

    public static Report Fix(List<GameEntry> games, List<ToolEntry> tools, AppSettings settings, Context context)
    {
        var report = new Report();

        foreach (var game in games)
        {
            game.IconPath = MoveTrayTriggerPath(game.IconPath, context);
            game.CoverImagePath = game.CoverImagePath == null ? null : MoveTrayTriggerPath(game.CoverImagePath, context);
            game.PreLaunchScriptPath = FixPath(game.PreLaunchScriptPath, isFile: true, context, report);
            game.PostExitScriptPath = FixPath(game.PostExitScriptPath, isFile: true, context, report);

            // Xbox games find their executable again on every launch; a Steam link has no file path.
            if (game.IsXboxGame || string.IsNullOrWhiteSpace(game.ExecutablePath) || ProcessLauncherService.IsNonFileProtocolUrl(game.ExecutablePath))
            {
                game.WorkingDirectory = FixPath(game.WorkingDirectory, isFile: false, context, report);
                continue;
            }

            if (!context.FileExists(game.ExecutablePath) && LauncherKey(game) is { } key
                && context.LauncherInstalls.TryGetValue(key, out var install) && !string.IsNullOrWhiteSpace(install.ExePath))
            {
                LoggingService.Verbose("Restore", $"'{game.Name}': {key.Platform} has it at '{install.ExePath}' now, not '{game.ExecutablePath}'.");
                game.ExecutablePath = install.ExePath!;
                if (!string.IsNullOrWhiteSpace(game.WorkingDirectory) && !context.DirectoryExists(game.WorkingDirectory))
                {
                    game.WorkingDirectory = install.InstallDir;
                }
                report.Relinked++;
                continue;
            }

            game.ExecutablePath = FixPath(game.ExecutablePath, isFile: true, context, report);
            game.WorkingDirectory = FixPath(game.WorkingDirectory, isFile: false, context, report);
            if (!context.FileExists(game.ExecutablePath) && WouldShowAsMissing(game, context))
            {
                report.StillMissing.Add(game.Name);
            }
        }

        foreach (var tool in tools)
        {
            tool.IconPath = MoveTrayTriggerPath(tool.IconPath, context);
            if (!string.IsNullOrWhiteSpace(tool.TargetPath) && !ProcessLauncherService.IsNonFileProtocolUrl(tool.TargetPath)
                && !tool.TargetPath.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                tool.TargetPath = FixPath(tool.TargetPath, isFile: true, context, report);
            }
            tool.WorkingDirectory = FixPath(tool.WorkingDirectory, isFile: false, context, report);
        }

        settings.ScriptDefaults.PreLaunchScriptPath = FixPath(settings.ScriptDefaults.PreLaunchScriptPath, isFile: true, context, report);
        settings.ScriptDefaults.PostExitScriptPath = FixPath(settings.ScriptDefaults.PostExitScriptPath, isFile: true, context, report);
        // Steam's own library folders are found again at every start; the user's own are moved here.
        foreach (var location in settings.ScanLocations.Where(l => l.Source != ScanLocationSource.Steam))
        {
            location.Path = FixPath(location.Path, isFile: false, context, report);
        }

        return report;
    }

    /// <summary>
    /// Whether the library will grey the game out for its executable being gone, the same test
    /// <see cref="InstalledGameIndex"/> makes: Steam and Battle.net answer for their own games
    /// whatever the path says, and a launcher game on a drive that isn't plugged in is left alone.
    /// </summary>
    private static bool WouldShowAsMissing(GameEntry game, Context context)
    {
        if (game.IsSteamGame || game.IsBattleNetGame) return false;
        if (!(game.IsGogGame || game.IsEaGame || game.IsEpicGame || game.IsUbisoftGame)) return true;
        string? root = Path.GetPathRoot(game.ExecutablePath);
        return !string.IsNullOrEmpty(root) && context.DirectoryExists(root);
    }

    private static (LauncherPlatform Platform, string Id)? LauncherKey(GameEntry game)
    {
        if (game.IsGogGame && !string.IsNullOrWhiteSpace(game.GogGameId)) return (LauncherPlatform.Gog, game.GogGameId);
        if (game.IsEaGame && !string.IsNullOrWhiteSpace(game.EaContentId)) return (LauncherPlatform.Ea, game.EaContentId);
        if (game.IsEpicGame && !string.IsNullOrWhiteSpace(game.EpicAppName)) return (LauncherPlatform.Epic, game.EpicAppName);
        if (game.IsUbisoftGame && !string.IsNullOrWhiteSpace(game.UbisoftGameId)) return (LauncherPlatform.Ubisoft, game.UbisoftGameId);
        return null;
    }

    /// <summary>
    /// Cached art and icons: always under TrayTrigger's own folders, so moved to this PC's whether or
    /// not the file is there yet (StorageService finds it by name later when it isn't).
    /// </summary>
    private static string MoveTrayTriggerPath(string path, Context context)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        return Replace(path, context.OldCacheDirectory, context.NewCacheDirectory)
            ?? Replace(path, context.OldDataDirectory, context.NewDataDirectory)
            ?? path;
    }

    /// <summary>The path as it is if it's on disk, else the first of the moves above that is; unchanged when none is.</summary>
    internal static string FixPath(string path, bool isFile, Context context, Report report)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        Func<string, bool> exists = isFile ? context.FileExists : context.DirectoryExists;
        if (exists(path)) return path;

        foreach (var (from, to) in new[]
        {
            (context.OldDataDirectory, context.NewDataDirectory),
            (context.OldCacheDirectory, context.NewCacheDirectory),
            (context.OldUserProfile, context.NewUserProfile),
        })
        {
            if (Replace(path, from, to) is { } moved && exists(moved))
            {
                report.Moved++;
                return moved;
            }
        }

        if (OnOtherDrive(path, context, exists) is { } onOtherDrive)
        {
            report.Moved++;
            return onOtherDrive;
        }
        return path;
    }

    /// <summary>"D:\Games\X\x.exe" on the one other drive that has "\Games\X\x.exe"; null when none or several do.</summary>
    private static string? OnOtherDrive(string path, Context context, Func<string, bool> exists)
    {
        if (path.Length < 3 || path[1] != ':' || (path[2] != '\\' && path[2] != '/') || !char.IsAsciiLetter(path[0])) return null;
        string rest = path[3..];
        var found = context.DriveRoots
            .Where(root => !root.StartsWith(path[..1], StringComparison.OrdinalIgnoreCase))
            .Select(root => Path.Combine(root, rest))
            .Where(exists)
            .Take(2)
            .ToList();
        return found.Count == 1 ? found[0] : null;
    }

    /// <summary><paramref name="path"/> with the folder <paramref name="from"/> swapped for <paramref name="to"/>, or null when it isn't under it or they're the same.</summary>
    private static string? Replace(string path, string? from, string to)
    {
        if (string.IsNullOrWhiteSpace(from)) return null;
        string oldRoot = from.TrimEnd('\\', '/');
        string newRoot = to.TrimEnd('\\', '/');
        if (string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase)) return null;
        if (path.Length == oldRoot.Length && path.Equals(oldRoot, StringComparison.OrdinalIgnoreCase)) return newRoot;
        if (path.Length > oldRoot.Length && path.StartsWith(oldRoot, StringComparison.OrdinalIgnoreCase) && path[oldRoot.Length] is '\\' or '/')
        {
            return newRoot + path[oldRoot.Length..];
        }
        return null;
    }
}
