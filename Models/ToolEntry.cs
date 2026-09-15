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
    /// <summary>The program or script to start: a local .exe, .bat, .cmd or .ps1 (see <see cref="ToolCatalog.ValidateTarget"/>). Blank for a Store app.</summary>
    public string TargetPath { get; set; } = string.Empty;
    /// <summary>
    /// A Microsoft Store (packaged) app's Application User Model ID, "&lt;PackageFamilyName&gt;!&lt;AppId&gt;",
    /// or blank for a program. When set the tool is a Store app: it is started by shell activation
    /// (<see cref="PackagedAppActivator"/>), and <see cref="TargetPath"/>, <see cref="WorkingDirectory"/>
    /// and <see cref="RunAsAdmin"/> don't apply. See <see cref="ToolCatalog.ValidateAppId"/>.
    /// </summary>
    public string AppId { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    /// <summary>Blank or missing means the target's own folder.</summary>
    public string WorkingDirectory { get; set; } = string.Empty;
    /// <summary>Start through the "runas" verb. Windows shows its own UAC prompt; TrayTrigger never intercepts it. Never set for a Store app.</summary>
    public bool RunAsAdmin { get; set; }
    /// <summary>A script only: run it with no console window, its output going to the log. Ignored for programs and Store apps.</summary>
    public bool HideWindow { get; set; }
    public string IconPath { get; set; } = string.Empty;
    public string Hotkey { get; set; } = string.Empty;
    /// <summary>A tools-only category; never shared with game categories.</summary>
    public string Category { get; set; } = LibraryConstants.Uncategorized;
    public bool IsFavorite { get; set; }
}
