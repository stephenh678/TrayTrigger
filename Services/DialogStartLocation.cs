using System;
using System.IO;
using Microsoft.Win32;

namespace TrayTrigger.Services;

/// <summary>
/// Where a Browse button's file or folder dialog opens: at the path in the box beside it, so
/// fixing a path that moved by one folder doesn't start from wherever Windows last was. A path
/// that no longer exists opens at the nearest folder above it that does; with nothing usable the
/// next fallback is tried, and with none the dialog keeps Windows' own choice.
/// </summary>
internal static class DialogStartLocation
{
    private const int MaxLevelsUp = 32;

    /// <summary>
    /// The existing folder a path points into: the folder itself, a file's folder, or the nearest
    /// existing parent. Null for text that is not a local, rooted path (empty, relative, a URL, a
    /// network share - which can stall a dialog - or illegal characters) and when nothing exists.
    /// </summary>
    internal static string? ExistingFolder(string? text)
    {
        string path = text?.Trim().Trim('"').Trim() ?? string.Empty;
        if (path.Length == 0 || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.Contains("://", StringComparison.Ordinal)) return null;

        try
        {
            if (!Path.IsPathFullyQualified(path)) return null;
            if (File.Exists(path)) return Path.GetDirectoryName(path);

            string? dir = path;
            for (int i = 0; i < MaxLevelsUp && !string.IsNullOrEmpty(dir); i++)
            {
                if (Directory.Exists(dir)) return dir;
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LoggingService.Swallowed("Dialogs", ex);
        }
        return null;
    }

    /// <summary>The first of these that points somewhere that exists.</summary>
    internal static string? FirstExisting(string? current, params string?[] fallbacks)
    {
        string? folder = ExistingFolder(current);
        if (folder != null) return folder;
        foreach (var fallback in fallbacks)
        {
            folder = ExistingFolder(fallback);
            if (folder != null) return folder;
        }
        return null;
    }

    /// <summary>Opens a file dialog at the path in the box, with that file selected when it exists.</summary>
    public static void ForFile(FileDialog dialog, string? currentPath, params string?[] fallbackPaths)
    {
        string? folder = FirstExisting(currentPath, fallbackPaths);
        if (folder != null) dialog.InitialDirectory = folder;

        string current = currentPath?.Trim().Trim('"').Trim() ?? string.Empty;
        try
        {
            if (current.Length > 0 && Path.IsPathFullyQualified(current) && File.Exists(current))
                dialog.FileName = Path.GetFileName(current);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            LoggingService.Swallowed("Dialogs", ex);
        }
    }

    /// <summary>Opens a folder dialog at the folder in the box.</summary>
    public static void ForFolder(OpenFolderDialog dialog, string? currentFolder, params string?[] fallbackPaths)
    {
        string? folder = FirstExisting(currentFolder, fallbackPaths);
        if (folder != null) dialog.InitialDirectory = folder;
    }
}
