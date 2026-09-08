using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace TrayTrigger.Services;

public enum HelpBlockKind
{
    Heading,    // "## Text"
    Paragraph,  // plain line(s)
    Bullet,     // "- Text"
    Note        // "> Text" - rendered as a highlighted callout
}

public sealed record HelpBlock(HelpBlockKind Kind, string Text);

public sealed class HelpTopic
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public IReadOnlyList<HelpBlock> Blocks { get; init; } = Array.Empty<HelpBlock>();
}

/// <summary>
/// Loads "Learn more" help topics from files embedded in the exe. Topics live in the repo under
/// Help/&lt;section&gt;/&lt;name&gt;.md and are addressed as "section/name" (e.g. "tweaks/hags").
/// The csproj embeds Help\**\*.md, so adding a topic is adding a file - no code change.
///
/// Format is a deliberately tiny Markdown subset so it stays readable in any editor:
///   # Title            first line - becomes the dialog title
///   ## Heading         section heading
///   - bullet           bullet item
///   > note             highlighted callout (warnings, "requires restart", etc.)
///   blank line         paragraph break; consecutive plain lines join into one paragraph
/// No inline formatting is interpreted.
/// </summary>
public static class HelpContentService
{
    private const string ResourcePrefix = "TrayTrigger.Help.";
    private static readonly Dictionary<string, HelpTopic?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock _lock = new();

    /// <summary>All topic ids embedded in the assembly, e.g. "tweaks/hags".</summary>
    public static IReadOnlyList<string> ListTopicIds()
    {
        return typeof(HelpContentService).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .Select(n => n.Substring(ResourcePrefix.Length, n.Length - ResourcePrefix.Length - 3))
            .Select(n =>
            {
                // "tweaks.hags" -> "tweaks/hags". Only the first dot separates section from name;
                // topic names must not contain dots.
                int dot = n.IndexOf('.');
                return dot < 0 ? n : n.Substring(0, dot) + "/" + n.Substring(dot + 1);
            })
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool HasTopic(string topicId) => GetTopic(topicId) != null;

    /// <summary>Returns the parsed topic, or null if no such embedded file exists.</summary>
    public static HelpTopic? GetTopic(string topicId)
    {
        if (string.IsNullOrWhiteSpace(topicId)) return null;

        lock (_lock)
        {
            if (_cache.TryGetValue(topicId, out var cached)) return cached;

            HelpTopic? topic = null;
            try
            {
                string resourceName = ResourcePrefix + topicId.Trim().Replace('/', '.').Replace('\\', '.') + ".md";
                using var stream = typeof(HelpContentService).Assembly.GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    using var reader = new StreamReader(stream);
                    topic = Parse(topicId, reader.ReadToEnd());
                }
                else
                {
                    LoggingService.Warn("Help", $"No embedded help topic '{topicId}' (resource '{resourceName}').");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn("Help", $"Failed to load help topic '{topicId}': {ex.Message}");
            }

            _cache[topicId] = topic;
            return topic;
        }
    }

    /// <summary>Parses the tiny Markdown subset described on the class. Public for tests.</summary>
    public static HelpTopic Parse(string topicId, string content)
    {
        string title = topicId;
        var blocks = new List<HelpBlock>();
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            blocks.Add(new HelpBlock(HelpBlockKind.Paragraph, string.Join(" ", paragraph)));
            paragraph.Clear();
        }

        bool titleSeen = false;
        foreach (string raw in content.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();

            if (line.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            if (!titleSeen && line.StartsWith("# ", StringComparison.Ordinal))
            {
                title = line.Substring(2).Trim();
                titleSeen = true;
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                FlushParagraph();
                blocks.Add(new HelpBlock(HelpBlockKind.Heading, line.Substring(3).Trim()));
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                FlushParagraph();
                blocks.Add(new HelpBlock(HelpBlockKind.Bullet, line.Substring(2).Trim()));
                continue;
            }

            if (line.StartsWith("> ", StringComparison.Ordinal))
            {
                FlushParagraph();
                blocks.Add(new HelpBlock(HelpBlockKind.Note, line.Substring(2).Trim()));
                continue;
            }

            paragraph.Add(line.Trim());
        }

        FlushParagraph();

        return new HelpTopic { Id = topicId, Title = title, Blocks = blocks };
    }
}
