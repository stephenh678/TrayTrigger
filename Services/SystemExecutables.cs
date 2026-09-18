using System;
using System.IO;

namespace TrayTrigger.Services;

/// <summary>
/// Full paths to the Windows programs TrayTrigger starts. Never start one of these by bare name:
/// CreateProcess looks in the application's own folder first, and TrayTrigger installs per user,
/// so that folder is writable without elevation; ShellExecute also consults the per-user
/// "App Paths" key. Either way a planted "powershell.exe" would run in place of the real one -
/// elevated, for the calls that use the "runas" verb.
/// </summary>
internal static class SystemExecutables
{
    private static readonly string WindowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public static readonly string CommandPrompt = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    public static readonly string WindowsPowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
    public static readonly string Reg = Path.Combine(Environment.SystemDirectory, "reg.exe");
    public static readonly string Powercfg = Path.Combine(Environment.SystemDirectory, "powercfg.exe");
    public static readonly string Notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
    public static readonly string Explorer = Path.Combine(WindowsDirectory, "explorer.exe");
}
