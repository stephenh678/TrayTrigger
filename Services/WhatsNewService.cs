using System;
using System.IO;
using System.Text;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// The "What's new" notes shown once after an update: the WhatsNew.md section of the stable
/// version the user has moved to, embedded in the exe. Betas share their stable version's
/// section, so a beta tester sees it once, not on every beta.
/// </summary>
public static class WhatsNewService
{
    /// <summary>"1.6.2" for 1.6.2, 1.6.2-beta.4 and v1.6.2: the heading the notes sit under.</summary>
    public static string StableKey(SemanticVersion v) => $"{v.Major}.{v.Minor}.{v.Patch}";

    /// <summary>
    /// Whether moving from <paramref name="previous"/> to <paramref name="current"/> should offer the
    /// notes: only forwards, never a first install (nothing recorded), and not again for a stable
    /// version already shown.
    /// </summary>
    public static bool IsDue(string previous, SemanticVersion current, string seenKey)
    {
        if (string.IsNullOrWhiteSpace(previous)) return false;
        if (SemanticVersion.TryParse(previous) is not { } was || was.CompareTo(current) >= 0) return false;
        return !string.Equals(StableKey(current), seenKey, StringComparison.Ordinal);
    }

    /// <summary>The bullets under "## <paramref name="key"/>", or null when the file has no such section.</summary>
    public static string? ExtractSection(string changelog, string key)
    {
        string heading = "## " + key;
        var body = new StringBuilder();
        bool inSection = false;
        foreach (string raw in changelog.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (inSection) break;
                inSection = string.Equals(line, heading, StringComparison.Ordinal);
                continue;
            }
            if (inSection) body.AppendLine(line);
        }
        string text = body.ToString().Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>The notes for <paramref name="key"/> from the embedded notes, or null when missing.</summary>
    public static string? LoadSection(string key)
    {
        try
        {
            using var stream = typeof(WhatsNewService).Assembly.GetManifestResourceStream("WhatsNew.md");
            if (stream == null) return null;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return ExtractSection(reader.ReadToEnd(), key);
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed("WhatsNew", ex, "reading the embedded notes");
            return null;
        }
    }
}
