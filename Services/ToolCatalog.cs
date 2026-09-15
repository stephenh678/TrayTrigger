using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// The Tools section's rules with no UI or disk access, so they are unit-testable: sort options,
/// view modes, category tabs, search, which tab a new tool lands in, and which targets may become
/// a tool. Categories follow <see cref="LibraryConstants.NormalizeCategory"/> like games do, but the
/// set of tool categories is built only from tools.
/// </summary>
public static class ToolCatalog
{
    // Same labels as the Library's sort list, so the two dropdowns read the same.
    public const string SortAlphabetical = "Alphabetical (A - Z)";
    public const string SortAlphabeticalDescending = "Alphabetical (Z - A)";
    public const string SortFavoritesFirst = "Favorites First (A - Z)";

    public static readonly IReadOnlyList<string> SortOptions = [SortAlphabetical, SortAlphabeticalDescending, SortFavoritesFirst];

    public const string ViewLargeIcons = "Large Icons";
    public const string ViewSmallIcons = "Small Icons";
    public const string ViewList = "List";

    public static readonly IReadOnlyList<string> ViewModes = [ViewLargeIcons, ViewSmallIcons, ViewList];

    /// <summary>
    /// Whether two tools start the same thing: the same program with the same arguments. What "already
    /// in Tools" means when adding one, and which running copy a tool with arguments comes back to.
    /// </summary>
    public static bool IsSameLaunch(ToolEntry a, ToolEntry b) =>
        IsStoreApp(a) == IsStoreApp(b)
        && (IsStoreApp(a)
            ? string.Equals(a.AppId.Trim(), b.AppId.Trim(), StringComparison.OrdinalIgnoreCase)
            : string.Equals(a.TargetPath?.Trim(), b.TargetPath?.Trim(), StringComparison.OrdinalIgnoreCase))
        && string.Equals(a.Arguments?.Trim() ?? string.Empty, b.Arguments?.Trim() ?? string.Empty, StringComparison.Ordinal);

    /// <summary>Why a game dropped on the Tools page isn't added: games belong in the Library, which Scan for Games already fills.</summary>
    public const string IsAGameReason = "it's a game; add it from the Library with Scan for Games";

    /// <summary>A shortcut that starts a Steam game (a steam://rungameid link, or steam.exe -applaunch) is a game, not a tool.</summary>
    public static string? GameReason(ShortcutResolution shortcut) => shortcut.IsSteamUrl ? IsAGameReason : null;

    /// <summary>A Store app that Gaming Services knows as a game (Game Pass, a Store game) is a game, not a tool.</summary>
    public static string? GameReason(string appId, Func<string, bool> isXboxGame) => isXboxGame(appId) ? IsAGameReason : null;

    /// <summary>A Store app, started by its app ID, rather than a program (.exe).</summary>
    public static bool IsStoreApp(ToolEntry tool) => !string.IsNullOrWhiteSpace(tool.AppId);

    /// <summary>What the list view and search show for where a tool starts from: the program's path, or the Store app's ID.</summary>
    public static string LaunchDisplay(ToolEntry tool) => IsStoreApp(tool) ? $"Store app: {tool.AppId.Trim()}" : tool.TargetPath;

    private const string AppsFolderPrefix = @"shell:AppsFolder\";

    /// <summary>The shell path a Store app's icon is read through. Also what a tool hands the icon cache as its icon source.</summary>
    public static string AppsFolderPath(string appId) => AppsFolderPrefix + appId.Trim();

    /// <summary>The app ID in a path made by <see cref="AppsFolderPath"/>, or null for anything else (a file path, say).</summary>
    public static string? AppIdFromAppsFolderPath(string? path) =>
        path != null && path.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase) && ValidateAppId(path[AppsFolderPrefix.Length..]) == null
            ? path[AppsFolderPrefix.Length..].Trim()
            : null;

    /// <summary>
    /// "&lt;PackageName&gt;_&lt;PublisherId&gt;!&lt;ApplicationId&gt;", as the package manifest schema allows each
    /// part. Strict on purpose: tools.json is user-editable and the ID ends up on explorer.exe's
    /// command line when activation falls back to shell:AppsFolder, so no quote, space, slash or
    /// switch may get through.
    /// </summary>
    private static readonly Regex AppIdPattern = new(
        @"^(?<name>[A-Za-z0-9.\-]{3,50})_(?<publisher>[A-Za-z0-9]{13})!(?<app>[A-Za-z][A-Za-z0-9]*(\.[A-Za-z][A-Za-z0-9]*)*)$",
        RegexOptions.CultureInvariant);

    /// <summary>Why <paramref name="appId"/> can't be a Store app tool, or null when it is a well-formed app ID.</summary>
    public static string? ValidateAppId(string? appId)
    {
        string id = appId?.Trim() ?? string.Empty;
        if (id.Length == 0) return "it has no app ID";
        var match = AppIdPattern.Match(id);
        if (!match.Success || match.Groups["app"].Length > 64) return "its app ID isn't a Store app ID";
        return null;
    }

    /// <summary>The package family name ("Microsoft.GamingApp_8wekyb3d8bbwe") of a well-formed app ID, else null.</summary>
    public static string? PackageFamilyNameOf(string? appId) =>
        ValidateAppId(appId) == null ? appId!.Trim()[..appId!.Trim().IndexOf('!')] : null;

    /// <summary>A stored sort option, or A to Z when it is blank or not one of <see cref="SortOptions"/>.</summary>
    public static string NormalizeSortOption(string? option) =>
        SortOptions.FirstOrDefault(o => string.Equals(o, option?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? SortAlphabetical;

    /// <summary>A stored view mode, or Large Icons when it is blank or not one of <see cref="ViewModes"/>.</summary>
    public static string NormalizeViewMode(string? mode) =>
        ViewModes.FirstOrDefault(m => string.Equals(m, mode?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? ViewLargeIcons;

    /// <summary>Orders two tools for <paramref name="sortOption"/>. Names compare case-insensitively, as the Library's do.</summary>
    public static int Compare(ToolEntry a, ToolEntry b, string? sortOption)
    {
        switch (NormalizeSortOption(sortOption))
        {
            case SortAlphabeticalDescending:
                return StringComparer.OrdinalIgnoreCase.Compare(b.Name, a.Name);
            case SortFavoritesFirst:
                int favorites = b.IsFavorite.CompareTo(a.IsFavorite);
                return favorites != 0 ? favorites : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            default:
                return StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        }
    }

    public static IEnumerable<ToolEntry> Sort(IEnumerable<ToolEntry> tools, string? sortOption) => Sort(tools, t => t, sortOption);

    /// <summary>Sorts anything that carries a tool (a card, say) by that tool.</summary>
    public static IEnumerable<T> Sort<T>(IEnumerable<T> items, Func<T, ToolEntry> toolOf, string? sortOption)
    {
        // List.Sort is not stable; ties (same name) keep their input order through the index.
        var indexed = items.Select((item, index) => (item, index)).ToList();
        indexed.Sort((x, y) =>
        {
            int c = Compare(toolOf(x.item), toolOf(y.item), sortOption);
            return c != 0 ? c : x.index.CompareTo(y.index);
        });
        return indexed.Select(x => x.item);
    }

    /// <summary>The distinct categories the tools use, A to Z, spelled as the first tool that uses each.</summary>
    public static IReadOnlyList<string> CategoriesOf(IEnumerable<ToolEntry> tools) =>
        tools.Select(t => LibraryConstants.NormalizeCategory(t.Category))
             .Distinct(StringComparer.OrdinalIgnoreCase)
             .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
             .ToList();

    /// <summary>The Tools page's tabs: All, Favorites, then each category A to Z.</summary>
    public static IReadOnlyList<string> TabsFor(IEnumerable<ToolEntry> tools) =>
        [LibraryConstants.AllCategory, LibraryConstants.FavoritesCategory, .. CategoriesOf(tools)];

    public static bool IsInTab(ToolEntry tool, string? tab)
    {
        if (string.IsNullOrWhiteSpace(tab) || string.Equals(tab, LibraryConstants.AllCategory, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(tab, LibraryConstants.FavoritesCategory, StringComparison.OrdinalIgnoreCase)) return tool.IsFavorite;
        return string.Equals(LibraryConstants.NormalizeCategory(tool.Category), tab, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Search matches the name, category, target path or Store app ID, case-insensitively. Blank matches everything.</summary>
    public static bool MatchesSearch(ToolEntry tool, string? text)
    {
        string query = text?.Trim() ?? string.Empty;
        if (query.Length == 0) return true;
        return tool.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || tool.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
            || tool.TargetPath.Contains(query, StringComparison.OrdinalIgnoreCase)
            || tool.AppId.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A tool added while a category tab is selected goes into that category; from All or Favorites it is Uncategorized.</summary>
    public static string CategoryForNewTool(string? selectedTab) => LibraryConstants.NormalizeCategory(selectedTab);

    /// <summary>
    /// Why <paramref name="target"/> can't be a tool, or null when it can: only an existing .exe on a
    /// local drive, by its full path. Links, network shares and mapped network drives, relative paths,
    /// scripts and other files are refused - a tool is launched through the shell, so anything else
    /// would run by file association, or resolve to a different program than the one checked.
    /// </summary>
    public static string? ValidateTarget(string? target, Func<string, bool>? fileExists = null, Func<string, bool>? isOnNetworkDrive = null)
    {
        fileExists ??= File.Exists;
        isOnNetworkDrive ??= IsOnNetworkDrive;
        string path = target?.Trim() ?? string.Empty;
        if (path.Length == 0) return "it has no target";
        if (path.Contains("://", StringComparison.Ordinal)) return "it's a link - Tools only take programs (.exe)";
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) || isOnNetworkDrive(path))
            return "it points at a network location - copy the program locally first";
        if (!Path.IsPathFullyQualified(path)) return "it isn't the full path to a program";
        if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            return "it doesn't point at a program (.exe)";
        if (!fileExists(path)) return "its program file doesn't exist";
        return null;
    }

    /// <summary>A drive letter mapped to a network share. False when the drive can't be read.</summary>
    private static bool IsOnNetworkDrive(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && Path.IsPathFullyQualified(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The working folder once a tool points at a new program: the new program's folder when the
    /// current one is blank, gone, or was just the old program's own folder; a folder the user chose
    /// elsewhere is kept.
    /// </summary>
    public static string WorkingDirectoryAfterRetarget(string? workingDirectory, string? oldTarget, string newTarget, Func<string, bool>? directoryExists = null)
    {
        directoryExists ??= Directory.Exists;
        string current = workingDirectory?.Trim() ?? string.Empty;
        string oldFolder = string.IsNullOrWhiteSpace(oldTarget) ? string.Empty : Path.GetDirectoryName(oldTarget.Trim()) ?? string.Empty;
        bool followsProgram = current.Length == 0
            || !directoryExists(current)
            || string.Equals(Path.TrimEndingDirectorySeparator(current), Path.TrimEndingDirectorySeparator(oldFolder), StringComparison.OrdinalIgnoreCase);
        return followsProgram ? Path.GetDirectoryName(newTarget) ?? string.Empty : current;
    }

    /// <summary>A tool's name from a dropped file: the file name without its extension or a " - Shortcut" suffix.</summary>
    public static string NameFromFile(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path)?.Trim() ?? string.Empty;
        foreach (string suffix in new[] { " - Shortcut", "_Shortcut" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length].Trim();
            }
        }
        return name.Length == 0 ? "Unnamed Tool" : name;
    }

    /// <summary>A batch favorite toggle: adds them all unless every one is already a favorite, then removes them all.</summary>
    public static bool ShouldFavoriteAll(IEnumerable<ToolEntry> tools) => !tools.All(t => t.IsFavorite);

    /// <summary>
    /// A batch Run as Administrator toggle: ticks them all unless every one already runs as admin, then
    /// unticks them all. Store apps can't be started elevated, so only programs count.
    /// </summary>
    public static bool ShouldRunAllAsAdmin(IEnumerable<ToolEntry> tools) => !tools.Where(t => !IsStoreApp(t)).All(t => t.RunAsAdmin);

    /// <summary>The category the batch dialog starts with: the shared one when every tool has the same, else blank.</summary>
    public static string CommonCategory(IEnumerable<ToolEntry> tools)
    {
        var categories = tools.Select(t => LibraryConstants.NormalizeCategory(t.Category)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return categories.Count == 1 ? categories[0] : string.Empty;
    }

    public static HotkeyBinding HotkeyBindingFor(ToolEntry tool) =>
        new(tool.Id, tool.Name, tool.Hotkey, HotkeyOwnerKind.Tool);
}
