using System;
using System.Diagnostics;
using System.Text;

namespace TrayTrigger.Services;

/// <summary>
/// Runs a short Windows PowerShell command with administrator rights: directly when TrayTrigger is
/// already elevated, otherwise through a UAC prompt. Shared by the Defender exclusion cmdlets and
/// the System Restore checkpoint.
/// </summary>
internal static class ElevatedPowerShell
{
    /// <summary>
    /// <paramref name="value"/> as a PowerShell single-quoted string literal. PowerShell closes such a
    /// string on the typographic quotes U+2018, U+2019, U+201A and U+201B as well as the ASCII
    /// apostrophe, and a folder called "Assassin’s Creed" is ordinary - so every one of them is
    /// doubled, not only the apostrophe. Anything else is literal inside single quotes.
    /// </summary>
    internal static string QuoteLiteral(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('\'');
        foreach (char c in value)
        {
            sb.Append(c);
            if (c is '\'' or '‘' or '’' or '‚' or '‛') sb.Append(c);
        }
        sb.Append('\'');
        return sb.ToString();
    }

    /// <summary>
    /// The script goes as -EncodedCommand (Base64 of UTF-16), so nothing in it meets the command
    /// line's own quoting rules on the way through ShellExecute.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(string script, bool alreadyElevated)
    {
        var psi = new ProcessStartInfo
        {
            FileName = SystemExecutables.WindowsPowerShell,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            UseShellExecute = !alreadyElevated
        };
        if (!alreadyElevated) psi.Verb = "runas";

        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        return psi;
    }

    /// <summary>True when the command ran and exited with code 0. Never throws.</summary>
    public static bool Run(string script, TimeSpan timeout, string logCategory)
    {
        try
        {
            using var proc = Process.Start(BuildStartInfo(script, SystemTweaksService.IsElevated));
            if (proc == null) return false;

            proc.WaitForExit((int)timeout.TotalMilliseconds);
            return proc.HasExited && proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            // Most commonly the user cancelled the UAC prompt (Win32Exception 1223).
            LoggingService.Warn(logCategory, $"Elevated PowerShell command failed: {ex.Message}");
            return false;
        }
    }
}
