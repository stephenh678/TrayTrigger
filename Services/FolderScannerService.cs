using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using System.Collections.Frozen;

namespace TrayTrigger.Services;

public record GameCandidate(
    string Name,
    string ExePath,
    string WorkingDirectory,
    long FileSizeBytes,
    int ConfidenceScore = 0,
    string RelativePath = ""
)
{
    /// <summary>
    /// Set by ImportCoordinator.ResolvePlatforms once the candidate's exe has been matched to an
    /// installed Steam/GOG/EA/Epic/Ubisoft game (see PlatformLookupService). Null for a genuinely
    /// local exe. When set, <see cref="Name"/> has already been replaced by the platform's own
    /// title, ignore checks use the platform ID instead of the exe path, and the batch dialog
    /// shows the platform's logo - so what the user previews is what gets imported.
    /// </summary>
    public PlatformMatch? Platform { get; init; }

    /// <summary>"Steam", "GOG", ... or null for a local exe - for badges and log lines.</summary>
    public string? PlatformName => Platform?.Platform;

    public string DisplaySize
    {
        get
        {
            if (FileSizeBytes < 1024 * 1024)
                return $"{FileSizeBytes / 1024.0:F1} KB";
            return $"{FileSizeBytes / (1024.0 * 1024.0):F1} MB";
        }
    }

    public string DisplayPath => string.IsNullOrWhiteSpace(RelativePath) ? ExePath : RelativePath;
}

public class FolderScanResult
{
    public bool IsMultiGameLibrary { get; set; }
    public List<GameCandidate> DiscoveredGames { get; set; } = [];
    public List<GameCandidate> SingleGameCandidates { get; set; } = [];
}

public partial class FolderScannerService
{
    private const int MaxScanDepth = 5;

    private static readonly string[] MultiGameLibraryNames =
    [
        "games", "my games", "steamlibrary", "common", "steamapps",
        "gog games", "xboxgames", "installed games", "game library",
        "pc games", "epic games", "ubisoft games", "ea games"
    ];

    // Directories that should never be traversed (heavy asset trees or utilities)
    private static readonly FrozenSet<string> PrunedDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Version control & IDE
        ".git", ".svn", ".vs", ".vscode", "node_modules",

        // Heavy asset folders (never contain primary game executables)
        "data", "content", "paks", "assets", "textures", "texture",
        "shaders", "shader", "sound", "sounds", "audio", "music", "voices",
        "video", "videos", "movies", "cinematics", "localization", "languages",
        "fonts", "saves", "saved", "screenshots", "logs", "crashdumps", "dumps",
        "intermediate", "deriveddatacache", "patch", "dlc",

        // Redistributables, installers, dependencies
        "_commonredist", "commonredist", "redist", "redistributables",
        "directx", "dxsetup", "support", "installer", "installers",
        "prerequisites", "prereq", "tools", "sdk", "docs", "documentation",
        "manual", "manuals", "extras", "crashreporter", "crashpad",
        "easyanticheat", "battleye", "eac", "anticheat"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Substrings in executable filenames that indicate non-game utilities
    private static readonly string[] DisqualifiedPatterns =
    [
        // Uninstallers
        "unins", "uninstall", "uninst", "cleanup", "remover",

        // Crash handlers & telemetry
        "crashreporter", "crashhandler", "unitycrashhandler", "crashreportclient",
        "crashpad_handler", "crashpad", "blackbox", "bugsplat", "werfault",

        // Anti-cheat services & setup
        "easyanticheat_setup", "easyanticheat_eos_setup", "eac_server",
        "battleye_setup", "beservice", "pbsvc", "vgc",

        // Redistributables & setup tools
        "vcredist", "vc_redist", "dxsetup", "directx", "dotnetfx",
        "oalinst", "ue4prereq", "ueprereq", "physx",

        // Web view / sub-process helpers
        "qtwebengineprocess", "cefsharp.browsersubprocess", "epicwebhelper",

        // Updaters & maintenance
        "updater", "patcher", "repair", "maintenance", "quickinstaller"
    ];

    // Substrings that heavily penalize score (but don't strictly disqualify)
    private static readonly string[] PenalizedSubstrings =
    [
        "dedicated", "server", "benchmark", "config", "settings", "configure",
        "editor", "devkit", "modtool", "authoring", "prelauncher"
    ];

    public List<GameCandidate> ScanFolder(string folderPath, bool preferExe = true)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return new List<GameCandidate>();

        string rootFolderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var discoveredCandidates = new List<GameCandidate>();

        // Breadth-first search / queue traversal up to MaxScanDepth
        var queue = new Queue<(string DirPath, int Depth)>();
        queue.Enqueue((folderPath, 0));

        while (queue.Count > 0)
        {
            var (currentDir, depth) = queue.Dequeue();

            // 1. Scan executables in currentDir
            try
            {
                var files = Directory.GetFiles(currentDir, "*.exe", SearchOption.TopDirectoryOnly);
                foreach (var file in files)
                {
                    if (IsDisqualified(file, rootFolderName))
                    {
                        LoggingService.Verbose("FolderScanner", $"Skipped disqualified utility/stub binary: '{file}'");
                        continue;
                    }

                    int score = ScoreExecutable(file, folderPath, rootFolderName, depth);
                    if (score > 0)
                    {
                        var fi = new FileInfo(file);
                        string relativePath = Path.GetRelativePath(folderPath, file);
                        string cleanName = DetermineGameName(file, rootFolderName, preferExe);

                        LoggingService.Verbose("FolderScanner", $"Candidate accepted: '{cleanName}' [Score={score}] ({relativePath})");

                        discoveredCandidates.Add(new GameCandidate(
                            Name: cleanName,
                            ExePath: file,
                            WorkingDirectory: currentDir,
                            FileSizeBytes: fi.Length,
                            ConfidenceScore: score,
                            RelativePath: relativePath
                        ));
                    }
                    else
                    {
                        LoggingService.Verbose("FolderScanner", $"Candidate scored 0 (ignored): '{file}'");
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn("FolderScannerService", $"Error scanning files in '{currentDir}': {ex.Message}");
            }

            // 2. Enqueue subdirectories if depth < MaxScanDepth
            if (depth < MaxScanDepth)
            {
                try
                {
                    var subdirs = Directory.GetDirectories(currentDir);
                    foreach (var sub in subdirs)
                    {
                        string dirName = Path.GetFileName(sub);
                        if (ShouldPruneDirectory(dirName))
                            continue;

                        queue.Enqueue((sub, depth + 1));
                    }
                }
                catch (Exception ex)
                {
                    LoggingService.Warn("FolderScannerService", $"Error getting subdirs of '{currentDir}': {ex.Message}");
                }
            }
        }

        // Sort by ConfidenceScore DESC, then FileSizeBytes DESC
        return discoveredCandidates
            .OrderByDescending(c => c.ConfidenceScore)
            .ThenByDescending(c => c.FileSizeBytes)
            .DistinctBy(c => c.ExePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public FolderScanResult ScanFolderOrLibrary(string folderPath, bool preferExe = true)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return new FolderScanResult();

        string rootDirName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).ToLowerInvariant();
        bool isNamedLikeLibrary = MultiGameLibraryNames.Any(n => rootDirName.Equals(n, StringComparison.OrdinalIgnoreCase) || rootDirName.Contains(n, StringComparison.OrdinalIgnoreCase));

        // Check immediate subdirectories
        var subdirs = new List<string>();
        try
        {
            subdirs = Directory.GetDirectories(folderPath)
                .Where(d => !ShouldPruneDirectory(Path.GetFileName(d)))
                .ToList();
        }
        catch (Exception ex)
        {
            LoggingService.Warn("FolderScanner", $"Failed to enumerate subdirectories of '{folderPath}': {ex.Message}");
        }

        // A library root often holds its games one or two container levels down rather than as
        // immediate children: D:\SteamLibrary -> steamapps -> common -> <games>. Without this, the
        // whole "steamapps" tree collapsed to a single best-scoring exe and dropping a Steam
        // library imported exactly one game. Any child that is itself named like a library
        // container is replaced by its own children (repeatedly, bounded), so the per-subfolder
        // game detection below runs against the real game folders.
        subdirs = ExpandLibraryContainers(subdirs);

        var detectedSubGames = new List<GameCandidate>();

        // If there are subdirectories, scan each subfolder to detect if it contains an independent game
        if (subdirs.Count > 0)
        {
            foreach (var sub in subdirs)
            {
                var candidates = ScanFolder(sub, preferExe);
                if (candidates.Count > 0 && candidates[0].ConfidenceScore >= 45)
                {
                    detectedSubGames.Add(candidates[0]);
                }
            }
        }

        // Decision logic:
        // 1. If 2 or more subdirectories are confirmed games -> Multi-Game Library!
        // 2. If 1 subdirectory is a game AND the parent folder is named like a games library (e.g. "Games", "SteamLibrary") -> Multi-Game Library!
        if (detectedSubGames.Count >= 2 || (detectedSubGames.Count == 1 && isNamedLikeLibrary))
        {
            LoggingService.Verbose("FolderScanner", $"'{folderPath}': {detectedSubGames.Count} sub-game(s) detected, isNamedLikeLibrary={isNamedLikeLibrary} -> treating as multi-game library.");
            return new FolderScanResult
            {
                IsMultiGameLibrary = true,
                DiscoveredGames = detectedSubGames
                    .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
        }

        // Otherwise, treat as a single game folder
        LoggingService.Verbose("FolderScanner", $"'{folderPath}': {detectedSubGames.Count} sub-game(s) detected, isNamedLikeLibrary={isNamedLikeLibrary} -> treating as a single game folder.");
        var singleCandidates = ScanFolder(folderPath, preferExe);
        return new FolderScanResult
        {
            IsMultiGameLibrary = false,
            SingleGameCandidates = singleCandidates
        };
    }

    /// <summary>
    /// For a folder the caller already knows is a library of many games - a user-configured Scan
    /// Location, not a folder of unknown shape - so unlike <see cref="ScanFolderOrLibrary"/> this
    /// doesn't need to guess whether the root itself is a single game or a library first. That
    /// guess requires each subfolder's best candidate to clear a 45-point confidence bar before
    /// it counts as "a real game", which exists to avoid misreading an ordinary single game's own
    /// bin/data/saves subfolders as separate games when the folder's identity is unknown. Here the
    /// identity is already known, so every immediate subfolder's own best-scoring candidate is
    /// taken directly - a new, correctly-installed but modestly-scored game no longer needs to
    /// outscore that bar just to be seen at all.
    /// </summary>
    public List<GameCandidate> ScanKnownLibraryLocation(string folderPath, bool preferExe = true)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return new List<GameCandidate>();

        List<string> subdirs;
        try
        {
            subdirs = Directory.GetDirectories(folderPath)
                .Where(d => !ShouldPruneDirectory(Path.GetFileName(d)))
                .ToList();
        }
        catch (Exception ex)
        {
            LoggingService.Warn("FolderScannerService", $"Error listing subdirectories of '{folderPath}': {ex.Message}");
            return new List<GameCandidate>();
        }

        // No subfolders at all - the scan location itself is (or currently only holds) one
        // game's own install tree rather than a container of many game folders.
        if (subdirs.Count == 0)
        {
            return ScanFolder(folderPath, preferExe);
        }

        var results = new List<GameCandidate>();
        foreach (var sub in subdirs)
        {
            var candidates = ScanFolder(sub, preferExe);
            if (candidates.Count > 0)
            {
                results.Add(candidates[0]);
            }
        }

        return results.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool IsDisqualified(string exePath, string rootFolderName)
    {
        string fileName = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();

        // 1. Check size: tiny stubs < 60 KB are almost never real games
        try
        {
            var fi = new FileInfo(exePath);
            if (fi.Length < 60 * 1024)
                return true;
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("FolderScanner", $"IsDisqualified: could not read file size for '{exePath}' ({ex.Message}) - treating as disqualified.");
            return true;
        }

        // 2. Check strict disqualification patterns (unless the game title itself contains it)
        string rootNorm = Normalize(rootFolderName);

        if (fileName.Contains("crash") && !rootNorm.Contains("crash"))
            return true;

        if (fileName.Contains("benchmark") && !rootNorm.Contains("benchmark"))
            return true;

        if (fileName.Contains("unins") || fileName.Contains("uninstall"))
            return true;

        if ((fileName.StartsWith("setup") || fileName.EndsWith("setup") || fileName.Contains("_setup") || fileName.Contains("setup_")) && !rootNorm.Contains("setup"))
            return true;

        foreach (var pattern in DisqualifiedPatterns)
        {
            if (fileName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                // If the root folder name itself contains this word (e.g. "Crash Bandicoot"), don't disqualify
                if (!rootNorm.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        // 3. Inspect FileVersionInfo for utility descriptions
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(exePath);
            string desc = (vi.FileDescription ?? "").ToLowerInvariant();
            string prod = (vi.ProductName ?? "").ToLowerInvariant();

            if (desc.Contains("crash report") || desc.Contains("crash handler") ||
                desc.Contains("uninstaller") || desc.Contains("setup tool") ||
                desc.Contains("redistributable") || desc.Contains("prerequisites") ||
                prod.Contains("crash report") || prod.Contains("uninstaller"))
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("FolderScanner", $"IsDisqualified: could not read FileVersionInfo for '{exePath}': {ex.Message}");
        }

        return false;
    }

    private static int ScoreExecutable(string exePath, string rootFolderPath, string rootFolderName, int depth)
    {
        int score = 0;
        string fileName = Path.GetFileNameWithoutExtension(exePath);
        string fileNorm = Normalize(fileName);
        string rootNorm = Normalize(rootFolderName);
        string relativeDir = Path.GetRelativePath(rootFolderPath, Path.GetDirectoryName(exePath) ?? rootFolderPath).ToLowerInvariant();

        // ------------------------------------------------------------------
        // Factor 1: Name Alignment with Root Folder (0 to 75 points)
        // ------------------------------------------------------------------
        if (fileNorm.Equals(rootNorm, StringComparison.OrdinalIgnoreCase))
        {
            score += 75; // Exact normalized match!
        }
        else if (rootNorm.Length > 3 && fileNorm.Contains(rootNorm, StringComparison.OrdinalIgnoreCase))
        {
            score += 60; // Exe contains folder name (e.g., Folder "Hades", Exe "Hades2")
        }
        else if (fileNorm.Length > 3 && rootNorm.Contains(fileNorm, StringComparison.OrdinalIgnoreCase))
        {
            score += 55; // Folder contains exe name (e.g., Folder "The Witcher 3 Wild Hunt", Exe "witcher3")
        }
        else
        {
            // Word overlap check
            var rootWords = SplitWords(rootFolderName);
            int matchingWords = 0;
            foreach (var w in rootWords)
            {
                if (w.Length > 2 && fileNorm.Contains(w, StringComparison.OrdinalIgnoreCase))
                {
                    matchingWords++;
                }
            }

            if (matchingWords > 0)
            {
                score += Math.Min(45, matchingWords * 20);
            }
        }

        // ------------------------------------------------------------------
        // Factor 2: Directory Path Patterns (0 to 40 points)
        // ------------------------------------------------------------------
        if (relativeDir.EndsWith(@"bin\x64") || relativeDir.EndsWith(@"bin\win64") || 
            relativeDir.Equals("bin") || relativeDir.EndsWith(@"\bin"))
        {
            score += 35; // Standard CDPR, Larian, Ubisoft, GOG path
        }
        else if (relativeDir.Contains(@"binaries\win64") || relativeDir.Contains(@"binaries\wingdk"))
        {
            score += 40; // Unreal Engine 4 & 5 standard game binary path
        }
        else if (relativeDir.Equals("game") || relativeDir.EndsWith(@"\game"))
        {
            score += 30; // FromSoftware (Elden Ring, Dark Souls, Armored Core)
        }
        else if (relativeDir.Contains("_retail_"))
        {
            score += 30; // Blizzard / Battle.net standard path
        }
        else if (depth == 0)
        {
            score += 25; // Standard root directory executable
        }

        // Penalize Unreal Engine internal engine binaries (they live under Engine\Binaries rather than <Game>\Binaries)
        if (relativeDir.StartsWith("engine") || relativeDir.Contains(@"\engine\"))
        {
            score -= 35;
        }

        // ------------------------------------------------------------------
        // Factor 3: Unreal Engine / Known Release Suffixes (0 to 35 points)
        // ------------------------------------------------------------------
        if (fileName.EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith("-WinGDK-Shipping", StringComparison.OrdinalIgnoreCase))
        {
            score += 35; // Definitive Unreal Engine production binary
        }
        else if (fileName.EndsWith("_dx12", StringComparison.OrdinalIgnoreCase) ||
                 fileName.EndsWith("_dx11", StringComparison.OrdinalIgnoreCase) ||
                 fileName.EndsWith("_vulkan", StringComparison.OrdinalIgnoreCase) ||
                 fileName.EndsWith("_vk", StringComparison.OrdinalIgnoreCase))
        {
            score += 20; // Explicit graphics API variants (e.g. bg3_dx11.exe)
        }

        // ------------------------------------------------------------------
        // Factor 4: File Size (0 to 30 points)
        // Real game binaries are substantial in size
        // ------------------------------------------------------------------
        try
        {
            var fi = new FileInfo(exePath);
            long bytes = fi.Length;

            if (bytes > 60 * 1024 * 1024) score += 30;      // > 60 MB
            else if (bytes > 25 * 1024 * 1024) score += 25; // > 25 MB
            else if (bytes > 10 * 1024 * 1024) score += 18; // > 10 MB
            else if (bytes > 3 * 1024 * 1024) score += 10;  // > 3 MB
            else if (bytes < 1024 * 1024) score -= 15;      // < 1 MB penalty
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("FolderScanner", $"ScoreExecutable: could not read file size for '{exePath}': {ex.Message}");
        }

        // ------------------------------------------------------------------
        // Factor 5: Windows GUI Subsystem vs Console (-25 to +15 points)
        // ------------------------------------------------------------------
        ushort subsystem = GetPeSubsystem(exePath);
        if (subsystem == 2) // IMAGE_SUBSYSTEM_WINDOWS_GUI
        {
            score += 15;
        }
        else if (subsystem == 3) // IMAGE_SUBSYSTEM_WINDOWS_CUI (Console)
        {
            score -= 30; // Real games are GUI applications, not console utilities
        }

        // ------------------------------------------------------------------
        // Factor 6: FileVersionInfo Metadata Match (0 to 25 points)
        // ------------------------------------------------------------------
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(exePath);
            string descNorm = Normalize(vi.FileDescription ?? "");
            string prodNorm = Normalize(vi.ProductName ?? "");

            if (descNorm.Contains(rootNorm, StringComparison.OrdinalIgnoreCase) ||
                prodNorm.Contains(rootNorm, StringComparison.OrdinalIgnoreCase))
            {
                score += 25;
            }
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("FolderScanner", $"ScoreExecutable: could not read FileVersionInfo for '{exePath}': {ex.Message}");
        }

        // ------------------------------------------------------------------
        // Factor 7: Penalized Keywords Check
        // ------------------------------------------------------------------
        string lowerFile = fileName.ToLowerInvariant();
        foreach (var pen in PenalizedSubstrings)
        {
            if (lowerFile.Contains(pen))
            {
                score -= 35;
                break;
            }
        }

        return score;
    }

    /// <summary>
    /// Replaces every directory whose own name is a library-container name (steamapps, common,
    /// games, ...) with its non-pruned children, up to three levels deep, so a dropped library
    /// root is scanned at the level where the individual game folders live. Directories that
    /// aren't containers pass through untouched. Public-static for unit testing.
    /// </summary>
    internal static List<string> ExpandLibraryContainers(List<string> subdirs)
    {
        const int maxLevels = 3;
        var current = subdirs;
        for (int level = 0; level < maxLevels; level++)
        {
            bool expandedAny = false;
            var next = new List<string>(current.Count);
            foreach (var dir in current)
            {
                string name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!MultiGameLibraryNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    next.Add(dir);
                    continue;
                }

                try
                {
                    var children = Directory.GetDirectories(dir)
                        .Where(d => !ShouldPruneDirectory(Path.GetFileName(d)))
                        .ToList();
                    if (children.Count == 0)
                    {
                        next.Add(dir);
                        continue;
                    }
                    LoggingService.Verbose("FolderScanner", $"'{dir}' is a library container - scanning its {children.Count} subfolder(s) instead.");
                    next.AddRange(children);
                    expandedAny = true;
                }
                catch (Exception ex)
                {
                    LoggingService.Warn("FolderScanner", $"Failed to enumerate library container '{dir}': {ex.Message}");
                    next.Add(dir);
                }
            }
            current = next;
            if (!expandedAny) break;
        }
        return current;
    }

    private static bool ShouldPruneDirectory(string dirName)
    {
        if (PrunedDirectoryNames.Contains(dirName))
            return true;

        string lower = dirName.ToLowerInvariant();

        // Blizzard uses _retail_ for the game files
        if (lower.StartsWith("_") && !lower.StartsWith("_retail"))
            return true;

        if (lower.StartsWith("redist") ||
            lower.StartsWith("directx") ||
            lower.StartsWith("installer") ||
            lower.StartsWith("prereq"))
        {
            return true;
        }

        return false;
    }

    private static string DetermineGameName(string exePath, string rootFolderName, bool preferExe = true)
    {
        return GameNameExtractor.ExtractGameName(exePath, rootFolderName, preferExe);
    }

    private static ushort GetPeSubsystem(string exePath)
    {
        try
        {
            using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 64) return 0;

            using var reader = new BinaryReader(fs);
            // Check MZ header
            if (reader.ReadUInt16() != 0x5A4D) return 0;

            // e_lfanew at 0x3C
            fs.Seek(0x3C, SeekOrigin.Begin);
            int peOffset = reader.ReadInt32();
            if (peOffset < 0 || peOffset > fs.Length - 128) return 0;

            // Check PE\0\0 signature
            fs.Seek(peOffset, SeekOrigin.Begin);
            if (reader.ReadUInt32() != 0x00004550) return 0;

            // Optional Header magic is at peOffset + 24
            fs.Seek(peOffset + 24, SeekOrigin.Begin);
            ushort magic = reader.ReadUInt16();

            // Subsystem is at offset 68 in Optional Header for both PE32 (0x10B) and PE32+ (0x20B)
            if (magic == 0x10B || magic == 0x20B)
            {
                fs.Seek(peOffset + 24 + 68, SeekOrigin.Begin);
                return reader.ReadUInt16();
            }
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("FolderScanner", $"GetPeSubsystem: could not parse PE header of '{exePath}': {ex.Message}");
        }

        return 0;
    }

    [GeneratedRegex(@"[^a-zA-Z0-9]")]
    private static partial Regex NonAlphaNumericRegex();

    [GeneratedRegex(@"[^a-zA-Z0-9]+")]
    private static partial Regex NonAlphaNumericSplitRegex();

    private static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return NonAlphaNumericRegex().Replace(s, "").ToLowerInvariant();
    }

    private static string[] SplitWords(string s)
    {
        if (string.IsNullOrEmpty(s)) return [];
        return NonAlphaNumericSplitRegex().Split(s)
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Select(w => w.ToLowerInvariant())
            .ToArray();
    }
}
