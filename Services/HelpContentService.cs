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

/// <summary>One entry in the About tab's help index.</summary>
public sealed record HelpTopicLink(string Id, string Title);

/// <summary>A section of the help index: its display label and the topics under it.</summary>
public sealed record HelpTopicGroup(string Section, string Label, IReadOnlyList<HelpTopicLink> Topics);

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

    /// <summary>Display order and labels for the help index and the dialog breadcrumb.</summary>
    private static readonly (string Section, string Label)[] SectionOrder =
    {
        ("tweaks", "Performance Tweaks"),
        ("profiles", "Performance Profiles"),
        ("scanner", "Game Scanner"),
        ("traymenu", "Tray Menu"),
        ("scripts", "Game Scripts"),
        ("library", "Library & Artwork"),
        ("updates", "Updates"),
        ("troubleshooting", "Troubleshooting"),
    };

    /// <summary>Friendly section label for a topic id, e.g. "tweaks/hags" -> "Performance Tweaks".</summary>
    public static string SectionLabel(string topicId)
    {
        int slash = topicId.IndexOf('/');
        string section = slash < 0 ? topicId : topicId.Substring(0, slash);
        foreach (var (s, label) in SectionOrder)
        {
            if (string.Equals(s, section, StringComparison.OrdinalIgnoreCase)) return label;
        }
        return section.Length == 0 ? "Help" : char.ToUpperInvariant(section[0]) + section.Substring(1);
    }

    /// <summary>
    /// Every embedded topic grouped by section, in display order, with "overview"-style topics
    /// first within each group and the rest alphabetical by title.
    /// </summary>
    public static IReadOnlyList<HelpTopicGroup> GetIndex()
    {
        var groups = new List<HelpTopicGroup>();
        var bySection = ListTopicIds()
            .Select(id => (Id: id, Topic: GetTopic(id)))
            .Where(t => t.Topic != null)
            .GroupBy(t => t.Id.Contains('/') ? t.Id.Substring(0, t.Id.IndexOf('/')) : t.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> orderedSections = SectionOrder.Select(s => s.Section)
            .Concat(bySection.Keys.Where(k => !SectionOrder.Any(s => string.Equals(s.Section, k, StringComparison.OrdinalIgnoreCase)))
                                  .OrderBy(k => k, StringComparer.OrdinalIgnoreCase));

        foreach (string section in orderedSections)
        {
            if (!bySection.TryGetValue(section, out var items)) continue;

            var links = items
                .OrderBy(t => t.Id.EndsWith("/overview", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(t => t.Topic!.Title, StringComparer.OrdinalIgnoreCase)
                .Select(t => new HelpTopicLink(t.Id, t.Topic!.Title))
                .ToList();

            groups.Add(new HelpTopicGroup(section, SectionLabel(section + "/"), links));
        }

        return groups;
    }

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
