using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TrayTrigger.Services;

public record ShortcutResolution(
    string Name,
    string TargetPath,
    string Arguments,
    string WorkingDirectory,
    string IconLocation,
    int IconIndex,
    bool IsSteamUrl,
    string? SteamAppId
);

public class ShortcutService
{
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, out WIN32_FIND_DATAW pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder ppszFileName);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    public ShortcutResolution Resolve(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path cannot be empty", nameof(filePath));

        string cleanName = CleanGameName(Path.GetFileNameWithoutExtension(filePath));
        string ext = Path.GetExtension(filePath).ToLowerInvariant();

        ShortcutResolution resolution;
        if (ext == ".url")
        {
            resolution = ResolveUrlShortcut(filePath, cleanName);
        }
        else if (ext == ".lnk")
        {
            resolution = ResolveShellLink(filePath, cleanName);
        }
        else
        {
            // Direct executable
            string workingDir = Path.GetDirectoryName(filePath) ?? string.Empty;
            string exeGameName = GameNameExtractor.ExtractGameName(filePath, Path.GetFileName(workingDir), preferExe: true);
            resolution = new ShortcutResolution(
                Name: exeGameName,
                TargetPath: filePath,
                Arguments: string.Empty,
                WorkingDirectory: workingDir,
                IconLocation: filePath,
                IconIndex: 0,
                IsSteamUrl: false,
                SteamAppId: null
            );
        }

        LoggingService.Verbose("ShortcutService", $"Resolved '{filePath}' -> Name='{resolution.Name}', Target='{resolution.TargetPath}', IsSteamUrl={resolution.IsSteamUrl}.");
        return resolution;
    }

    private ShortcutResolution ResolveUrlShortcut(string urlPath, string cleanName)
    {
        string targetUrl = string.Empty;
        string iconFile = string.Empty;
        int iconIndex = 0;

        try
        {
            var lines = File.ReadAllLines(urlPath);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                {
                    targetUrl = trimmed.Substring(4).Trim();
                }
                else if (trimmed.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase))
                {
                    iconFile = trimmed.Substring(9).Trim();
                }
                else if (trimmed.StartsWith("IconIndex=", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(trimmed.Substring(10).Trim(), out iconIndex);
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("ShortcutService", $"Error reading .url: {ex.Message}");
        }

        bool isSteam = targetUrl.StartsWith("steam://rungameid/", StringComparison.OrdinalIgnoreCase);
        string? steamAppId = null;
        if (isSteam)
        {
            // The App ID ends up in cache filenames and API query strings - only accept the
            // digits-only shape Steam actually uses.
            string rawId = targetUrl.Substring("steam://rungameid/".Length).Trim().Split('/')[0];
            steamAppId = UrlProtocolHelper.IsValidSteamAppId(rawId) ? rawId : null;
            if (steamAppId == null)
            {
                LoggingService.Warn("ShortcutService", $"Ignoring malformed Steam App ID in '{urlPath}'.");
                isSteam = false;
            }
        }

        return new ShortcutResolution(
            Name: cleanName,
            TargetPath: targetUrl,
            Arguments: string.Empty,
            WorkingDirectory: string.Empty,
            IconLocation: string.IsNullOrEmpty(iconFile) ? urlPath : iconFile,
            IconIndex: iconIndex,
            IsSteamUrl: isSteam,
            SteamAppId: steamAppId
        );
    }

    private ShortcutResolution ResolveShellLink(string lnkPath, string cleanName)
    {
        IShellLinkW? link = null;
        try
        {
            link = (IShellLinkW)new ShellLink();
            var persistFile = (IPersistFile)link;
            persistFile.Load(lnkPath, 0);

            // 1024 (rather than the classic MAX_PATH of 260) so long-path targets under
            // extended-length paths or deeply nested install dirs aren't silently truncated.
            // See L-21.
            var targetSb = new StringBuilder(1024);
            link.GetPath(targetSb, targetSb.Capacity, out _, 0);
            string targetPath = targetSb.ToString();

            var argsSb = new StringBuilder(1024);
            link.GetArguments(argsSb, argsSb.Capacity);
            string args = argsSb.ToString();

            var workDirSb = new StringBuilder(1024);
            link.GetWorkingDirectory(workDirSb, workDirSb.Capacity);
            string workDir = workDirSb.ToString();

            var iconSb = new StringBuilder(1024);
            link.GetIconLocation(iconSb, iconSb.Capacity, out int iconIndex);
            string iconLocation = iconSb.ToString();

            if (string.IsNullOrEmpty(workDir) && !string.IsNullOrEmpty(targetPath) && File.Exists(targetPath))
            {
                workDir = Path.GetDirectoryName(targetPath) ?? string.Empty;
            }

            // Check if target is steam.exe with -applaunch <id>
            bool isSteam = false;
            string? steamAppId = null;

            if (targetPath.EndsWith("steam.exe", StringComparison.OrdinalIgnoreCase) &&
                args.Contains("-applaunch", StringComparison.OrdinalIgnoreCase))
            {
                var parts = args.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    if (parts[i].Equals("-applaunch", StringComparison.OrdinalIgnoreCase) &&
                        UrlProtocolHelper.IsValidSteamAppId(parts[i + 1]))
                    {
                        isSteam = true;
                        steamAppId = parts[i + 1];
                        targetPath = $"steam://rungameid/{steamAppId}";
                        args = string.Empty;
                        break;
                    }
                }
            }

            string finalIcon = !string.IsNullOrEmpty(iconLocation) ? iconLocation :
                               (!string.IsNullOrEmpty(targetPath) ? targetPath : lnkPath);

            return new ShortcutResolution(
                Name: cleanName,
                TargetPath: targetPath,
                Arguments: args,
                WorkingDirectory: workDir,
                IconLocation: finalIcon,
                IconIndex: iconIndex,
                IsSteamUrl: isSteam,
                SteamAppId: steamAppId
            );
        }
        catch (Exception ex)
        {
            LoggingService.Warn("ShortcutService", $"Error resolving .lnk: {ex.Message}");
            return new ShortcutResolution(
                Name: cleanName,
                TargetPath: lnkPath,
                Arguments: string.Empty,
                WorkingDirectory: string.Empty,
                IconLocation: lnkPath,
                IconIndex: 0,
                IsSteamUrl: false,
                SteamAppId: null
            );
        }
        finally
        {
            if (link != null)
            {
                try
                {
                    Marshal.FinalReleaseComObject(link);
                }
                catch (Exception ex)
                {
                    LoggingService.Verbose("ShortcutService", $"FinalReleaseComObject failed while resolving '{lnkPath}': {ex.Message}");
                }
            }
        }
    }

    private static string CleanGameName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Unnamed Game";

        string name = raw;
        string[] suffixes = { " - Shortcut", " - shortcut", "_Shortcut" };
        foreach (var s in suffixes)
        {
            if (name.EndsWith(s, StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - s.Length);
            }
        }
        return name.Trim();
    }
}
