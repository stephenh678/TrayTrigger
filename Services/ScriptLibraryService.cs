using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace TrayTrigger.Services;

/// <summary>
/// The user's scripts folder (%AppData%\TrayTrigger\Scripts): where "New script..." writes, where
/// the Browse buttons open, and where the bundled blank templates, examples and README are
/// materialised from embedded resources. Nothing here acts on its own - a script only runs once the
/// user picks it for a game - and a file the user may have edited is never overwritten (the README
/// is the one exception: it is documentation, refreshed on every install pass).
/// </summary>
public class ScriptLibraryService
{
    /// <summary>Logical-name prefix set in TrayTrigger.csproj for Scripts\Library\*.</summary>
    private const string ResourcePrefix = "ScriptLibrary/";
    public const string ReadmeFileName = "README.txt";
    public const string BlankBatchFileName = "_Blank.bat";
    public const string BlankPowerShellFileName = "_Blank.ps1";

    public string ScriptsDirectory { get; }

    public ScriptLibraryService(string baseDirectory)
    {
        ScriptsDirectory = Path.Combine(baseDirectory, "Scripts");
    }

    /// <summary>File names of every bundled script and the README, as embedded in the assembly.</summary>
    public static IReadOnlyList<string> BundledFileNames()
    {
        return typeof(ScriptLibraryService).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Select(n => n.Substring(ResourcePrefix.Length))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The embedded content of one bundled file with Windows line endings, or null if there is no
    /// such resource. The repo may store these with LF; on disk they must be CRLF - cmd.exe has
    /// parsing quirks with LF-only batch files and older Notepad shows them as one line.
    /// </summary>
    public static string? ReadBundled(string fileName)
    {
        using var stream = typeof(ScriptLibraryService).Assembly.GetManifestResourceStream(ResourcePrefix + fileName);
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n").Replace("\n", "\r\n");
    }

    /// <summary>
    /// Creates the folder and writes any bundled file that is missing; refreshes the README.
    /// Idempotent and never throws. Returns the names of the files written.
    /// </summary>
    public IReadOnlyList<string> EnsureInstalled()
    {
        var written = new List<string>();
        try
        {
            Directory.CreateDirectory(ScriptsDirectory);
            foreach (string name in BundledFileNames())
            {
                string target = Path.Combine(ScriptsDirectory, name);
                bool isReadme = string.Equals(name, ReadmeFileName, StringComparison.OrdinalIgnoreCase);
                if (!isReadme && File.Exists(target)) continue;

                string? content = ReadBundled(name);
                if (content == null) continue;

                // Skip the README rewrite when it is already current, so the folder's timestamps
                // don't churn on every start.
                if (isReadme && File.Exists(target) && File.ReadAllText(target) == content) continue;

                File.WriteAllText(target, content);
                written.Add(name);
            }
            if (written.Count > 0)
            {
                LoggingService.Info("Scripts", $"Scripts folder updated ({string.Join(", ", written)}): {ScriptsDirectory}");
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Scripts", $"Could not prepare the scripts folder {ScriptsDirectory}: {ex.Message}");
        }
        return written;
    }

    /// <summary>
    /// Writes the blank template matching the target's extension (.bat/.cmd or .ps1) to the
    /// given path. Returns false without touching anything if the file already exists or the
    /// extension has no template. Throws on I/O failure so the caller can show the reason.
    /// </summary>
    public static bool CreateFromBlankTemplate(string targetPath)
    {
        string ext = Path.GetExtension(targetPath).ToLowerInvariant();
        string? templateName = ext switch
        {
            ".bat" or ".cmd" => BlankBatchFileName,
            ".ps1" => BlankPowerShellFileName,
            _ => null
        };
        if (templateName == null || File.Exists(targetPath)) return false;

        string? content = ReadBundled(templateName);
        if (content == null) return false;

        string? dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(targetPath, content);
        LoggingService.Info("Scripts", $"Created {targetPath} from {templateName}.");
        return true;
    }

    /// <summary>
    /// Opens a script for editing. Never through the file's default "open" action - for a .bat
    /// that would run it. Tries the shell's "edit" verb (Notepad or whatever the user has
    /// associated), then falls back to Notepad. An executable has nothing to edit, so its folder
    /// is opened with the file selected instead.
    /// </summary>
    public static void OpenInEditor(string path)
    {
        string p = path.Trim().Trim('"');
        if (!File.Exists(p))
        {
            LoggingService.Warn("Scripts", $"Cannot open for editing, file not found: {p}");
            return;
        }

        string ext = Path.GetExtension(p).ToLowerInvariant();
        if (ext is ".exe" or ".com")
        {
            RevealInExplorer(p);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(p) { Verb = "edit", UseShellExecute = true });
            return;
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("Scripts", $"'edit' verb unavailable for {p} ({ex.Message}); using Notepad.");
        }

        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{p}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Scripts", $"Could not open {p} in an editor: {ex.Message}");
        }
    }

    /// <summary>Opens the scripts folder in Explorer, creating and populating it first if needed.</summary>
    public void OpenFolder()
    {
        EnsureInstalled();
        try
        {
            Process.Start("explorer.exe", ScriptsDirectory);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Scripts", $"Could not open the scripts folder: {ex.Message}");
        }
    }

    private static void RevealInExplorer(string path)
    {
        try
        {
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Scripts", $"Could not reveal {path}: {ex.Message}");
        }
    }
}
