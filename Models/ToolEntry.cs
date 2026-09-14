using System;
using TrayTrigger.Services;

namespace TrayTrigger.Models;

/// <summary>
/// A program in the Tools section (DLSS Swapper, Vortex, MSI Afterburner): a saved shortcut and
/// nothing more. Deliberately separate from <see cref="GameEntry"/> - a tool has no session,
/// performance profile, scripts, playtime, platform or metadata, and its categories never mix with
/// game categories. Stored in tools.json by <see cref="StorageService.SaveTools"/>.
/// </summary>
public class ToolEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    /// <summary>The program to start: always a local .exe (see <see cref="ToolCatalog.ValidateTarget"/>).</summary>
    public string TargetPath { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    /// <summary>Blank or missing means the target's own folder.</summary>
    public string WorkingDirectory { get; set; } = string.Empty;
    /// <summary>Start through the "runas" verb. Windows shows its own UAC prompt; TrayTrigger never intercepts it.</summary>
    public bool RunAsAdmin { get; set; }
    public string IconPath { get; set; } = string.Empty;
    public string Hotkey { get; set; } = string.Empty;
    /// <summary>A tools-only category; never shared with game categories.</summary>
    public string Category { get; set; } = LibraryConstants.Uncategorized;
    public bool IsFavorite { get; set; }
}
