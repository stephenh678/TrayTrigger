using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace TrayTrigger.Services;

/// <summary>
/// What TrayTrigger did, and didn't do, for one launch of a game, in plain words: the Profile, Game,
/// Scripts and Tools lines of the game's Played row in Activity &amp; History. Each service notes
/// here as it goes (the profile applied, a tool left alone because it was already running, a script
/// that ran), by game id; the launcher takes the record when the session ends and writes the row.
/// Static, like <see cref="ActivityService.Current"/>, so a service needs no plumbing to say what it did.
/// </summary>
public static class LaunchRecord
{
    public const string Launch = "Launch";
    public const string Profile = "Profile";
    public const string Game = "Game";
    public const string Scripts = "Scripts";
    public const string Tools = "Tools";

    /// <summary>
    /// A Profile note that starts with one of these goes on the row's Skipped or Put back line
    /// instead, so what changed, what didn't and whether it was undone each read on their own.
    /// </summary>
    public const string SkippedPrefix = "skipped: ";
    public const string PutBackPrefix = "put back: ";

    /// <summary>The order the lines take on the row.</summary>
    public static readonly IReadOnlyList<string> Sections = [Launch, Profile, Game, Scripts, Tools];

    private static readonly Lock _lock = new();
    private static readonly Dictionary<string, Dictionary<string, List<string>>> _byGame = new(StringComparer.Ordinal);

    /// <summary>Starts the game's record afresh, at launch: notes from a launch whose row never took them (a link launch) don't carry over.</summary>
    public static void Begin(string gameId)
    {
        lock (_lock) _byGame[gameId] = new Dictionary<string, List<string>>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Adds a note under a section for the game's launch in progress. The same note twice is kept once.
    /// A note that arrives after the row was written (CPU Cores after its wait, DLSS seen late in a
    /// short session) goes to <see cref="LateNote"/> instead, so the row can be amended.
    /// </summary>
    public static void Note(string gameId, string section, string text)
    {
        if (string.IsNullOrWhiteSpace(gameId) || string.IsNullOrWhiteSpace(text)) return;
        Action<string, string, string>? late = null;
        lock (_lock)
        {
            if (!_byGame.TryGetValue(gameId, out var sections))
            {
                late = LateNote;
                if (late == null) _byGame[gameId] = sections = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            }
            if (sections != null)
            {
                if (!sections.TryGetValue(section, out var notes)) sections[section] = notes = new List<string>();
                if (!notes.Contains(text, StringComparer.Ordinal)) notes.Add(text);
            }
        }
        late?.Invoke(gameId, section, text);
    }

    /// <summary>
    /// Where a note goes once the game's record has been taken: the launcher sets this to amend the
    /// Played row it wrote. Null (tests, before startup) keeps late notes for the next Begin to drop.
    /// </summary>
    public static Action<string, string, string>? LateNote { get; set; }

    /// <summary>The record so far, in section order with only the sections that have notes, and forgotten here.</summary>
    public static IReadOnlyList<(string Section, IReadOnlyList<string> Notes)> Take(string gameId)
    {
        Dictionary<string, List<string>>? sections;
        lock (_lock)
        {
            if (!_byGame.Remove(gameId, out sections)) return [];
        }
        return Sections.Where(sections.ContainsKey).Select(s => (s, (IReadOnlyList<string>)sections[s])).ToList();
    }

    /// <summary>One section's notes so far, left in place. For tests.</summary>
    public static IReadOnlyList<string> Peek(string gameId, string section)
    {
        lock (_lock)
        {
            return _byGame.TryGetValue(gameId, out var sections) && sections.TryGetValue(section, out var notes) ? notes.ToList() : [];
        }
    }
}
