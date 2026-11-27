using System;
using System.IO;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

/// <summary>"Open File Location" for a game, shared by the library card and Game Details: Explorer on the game's
/// exe, selected, or on its install folder when the exe isn't there.</summary>
internal static class GameFolder
{
    /// <summary>True when there is something to open: the exe, or the install folder.</summary>
    public static bool CanOpen(GameEntry game) => File.Exists(game.ExecutablePath) || Directory.Exists(game.WorkingDirectory);

    public static void Open(GameEntry game, string category)
    {
        try
        {
            string? targetPath = game.ExecutablePath;
            if (File.Exists(targetPath))
            {
                ShellOpen.Reveal(targetPath, category, $"the folder for '{game.Name}'");
            }
            else if (Directory.Exists(game.WorkingDirectory))
            {
                // Follow a junction to the real folder (an Xbox game's is a WindowsApps package
                // folder linked to D:\XboxGames\<Game>\Content). A junction whose target is gone
                // still passes Directory.Exists, and Explorer shows Documents for it instead.
                if (LinkedDirectory.Resolve(game.WorkingDirectory, out string folder) == LinkedDirectoryState.BrokenLink)
                {
                    LoggingService.Warn(category, $"Cannot open the folder for '{game.Name}': '{game.WorkingDirectory}' links to a folder or drive that no longer exists.");
                    Views.ModernDialog.ShowWarning(WindowHelper.ActiveOwner(), "Folder Not Found",
                        $"The folder for \"{game.Name}\" no longer exists.",
                        $"{game.WorkingDirectory} points to a folder or drive that has been removed or isn't connected.");
                }
                else
                {
                    ShellOpen.OpenFolder(folder, category, $"the folder for '{game.Name}'");
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn(category, $"Error opening folder: {ex.Message}");
        }
    }
}
