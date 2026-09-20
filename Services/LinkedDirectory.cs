using System;
using System.IO;

namespace TrayTrigger.Services;

/// <summary>What a folder path turns out to be once any junction or symlink is followed.</summary>
public enum LinkedDirectoryState
{
    /// <summary>A plain folder, or a link whose target couldn't be checked (unreadable reparse
    /// data, an access-denied or offline network target). Callers use the path as given.</summary>
    Folder,

    /// <summary>A link whose final target exists.</summary>
    Linked,

    /// <summary>A link whose target is gone: the folder was deleted, or its drive isn't connected.</summary>
    BrokenLink,
}

/// <summary>
/// <see cref="Directory.Exists"/> answers for a junction or symlink itself, not for what it points
/// at, so a junction whose target folder was deleted or whose drive was removed still "exists".
/// The Xbox app installs games as exactly such junctions (WindowsApps\&lt;package&gt; pointing at
/// D:\XboxGames\&lt;Game&gt;\Content), which is how a game with no files left looked installed.
/// </summary>
public static class LinkedDirectory
{
    private const int ErrorNotReady = unchecked((int)0x80070015);

    /// <summary>
    /// Follows <paramref name="path"/> if it is a link. <paramref name="resolved"/> is the final
    /// target for <see cref="LinkedDirectoryState.Linked"/>, otherwise <paramref name="path"/>.
    /// Only a target that is definitely missing reports <see cref="LinkedDirectoryState.BrokenLink"/>;
    /// anything that can't be checked stays <see cref="LinkedDirectoryState.Folder"/>, so a
    /// permissions quirk never hides a game that is really installed.
    /// </summary>
    public static LinkedDirectoryState Resolve(string path, out string resolved)
    {
        resolved = path;

        string? linkTarget;
        try
        {
            linkTarget = new DirectoryInfo(path).LinkTarget;
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed("LinkedDirectory", ex, "reading the link target");
            return LinkedDirectoryState.Folder;
        }
        if (linkTarget == null) return LinkedDirectoryState.Folder;

        string? target = null;
        try
        {
            target = new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch
        {
            // Some broken targets throw instead of resolving; fall back to the immediate target.
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                target = Path.GetFullPath(linkTarget, Path.GetDirectoryName(fullPath) ?? fullPath);
            }
            catch (Exception ex)
            {
                LoggingService.Swallowed("LinkedDirectory", ex, "resolving the link target");
                return LinkedDirectoryState.Folder;
            }
        }

        target = Path.TrimEndingDirectorySeparator(target);
        if (Directory.Exists(target))
        {
            resolved = target;
            return LinkedDirectoryState.Linked;
        }

        return IsDefinitelyMissing(target) ? LinkedDirectoryState.BrokenLink : LinkedDirectoryState.Folder;
    }

    private static bool IsDefinitelyMissing(string path)
    {
        try
        {
            File.GetAttributes(path);
            return false; // Present, just not listable (access denied): not ours to judge.
        }
        catch (FileNotFoundException) { /* the target is gone */ return true; }
        catch (DirectoryNotFoundException) { /* the target is gone */ return true; }
        catch (DriveNotFoundException) { /* the target's drive is gone */ return true; }
        catch (IOException ex) when (ex.HResult == ErrorNotReady) { return true; } // Removable drive gone.
        catch (Exception ex) { LoggingService.Swallowed("LinkedDirectory", ex, "listing the link target"); return false; }
    }
}
