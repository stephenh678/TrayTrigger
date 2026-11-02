using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;

namespace TrayTrigger.Views;

/// <summary>What a result of the window's search is, which decides where choosing it goes.</summary>
internal enum GlobalHitKind { Game, MoreGames, Tool, Setting, System, About, Activity }

/// <summary>A game or a tool as the search reads it: its name, the line shown under it, and what choosing it opens.</summary>
internal sealed record NamedItem(string Name, string Detail, object Target);

/// <summary>One line of the results list. <c>Target</c> is the <see cref="NamedItem"/> target, a <see cref="CardHit"/>, or the query.</summary>
internal sealed record GlobalHit(GlobalHitKind Kind, string Title, string Detail, string Snippet, object? Target);

/// <summary>A heading and the lines under it.</summary>
internal sealed record GlobalGroup(string Name, IReadOnlyList<GlobalHit> Hits);

/// <summary>
/// The window's search (Ctrl+K): games, tools, and the cards of Settings, System and About, in that order,
/// plus one line for Activity. Each group has its own cap, so a settings card never pushes a game down the
/// list and a long run of games never hides the settings. A group with nothing is left out and its room is
/// not lent to another, so the list doesn't jump about as words are added.
/// <para>
/// The pages are read the way their own search reads them (<see cref="CardSearch"/>), so text edited in a
/// page's XAML is found without touching this. Games and tools match on their name or category; every
/// word typed must be in one of the two.
/// </para>
/// </summary>
internal static class GlobalSearch
{
    internal const int MaxGames = 5, MaxTools = 3, MaxSettings = 3, MaxSystem = 2, MaxAbout = 2;

    internal static List<GlobalGroup> Find(
        string? query,
        IEnumerable<NamedItem> games,
        IEnumerable<NamedItem> tools,
        Panel? settings,
        Panel? system,
        Panel? about,
        int activityMatches)
    {
        var groups = new List<GlobalGroup>();
        string[] terms = CardSearch.SplitTerms(query);
        if (terms.Length == 0) return groups;
        string typed = string.Join(' ', terms);

        var gameHits = Match(games, terms);
        if (gameHits.Count > 0)
        {
            var hits = gameHits.Take(MaxGames).Select(i => new GlobalHit(GlobalHitKind.Game, i.Name, i.Detail, string.Empty, i.Target)).ToList();
            if (gameHits.Count > MaxGames)
                hits.Add(new GlobalHit(GlobalHitKind.MoreGames, $"Show all {gameHits.Count} games matching “{typed}”", "In the Library", string.Empty, typed));
            groups.Add(new GlobalGroup("Games", hits));
        }

        var toolHits = Match(tools, terms);
        if (toolHits.Count > 0)
            groups.Add(new GlobalGroup("Tools", toolHits.Take(MaxTools).Select(i => new GlobalHit(GlobalHitKind.Tool, i.Name, i.Detail, string.Empty, i.Target)).ToList()));

        AddCards(groups, "Settings", GlobalHitKind.Setting, settings, query, MaxSettings);
        AddCards(groups, "System", GlobalHitKind.System, system, query, MaxSystem);
        AddCards(groups, "About", GlobalHitKind.About, about, query, MaxAbout);

        if (activityMatches > 0)
        {
            string entries = activityMatches == 1 ? "1 entry" : $"{activityMatches} entries";
            groups.Add(new GlobalGroup("Activity", [new GlobalHit(GlobalHitKind.Activity, "Activity & History", $"{entries} matching “{typed}”", string.Empty, typed)]));
        }
        return groups;
    }

    /// <summary>Every item whose name or category holds all the words, a name that starts with the first word first, then the rest by name.</summary>
    private static List<NamedItem> Match(IEnumerable<NamedItem> items, string[] terms) =>
        items.Where(i => CardSearch.Matches(i.Name + " " + i.Detail, terms))
             .OrderBy(i => i.Name.StartsWith(terms[0], StringComparison.OrdinalIgnoreCase) ? 0 : CardSearch.Matches(i.Name, terms) ? 1 : 2)
             .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
             .ToList();

    private static void AddCards(List<GlobalGroup> groups, string name, GlobalHitKind kind, Panel? page, string? query, int cap)
    {
        if (page == null) return;
        var hits = CardSearch.Find(page, query).Take(cap)
            .Select(h => new GlobalHit(kind, h.Title, h.TabLabel, h.Snippet, h))
            .ToList();
        if (hits.Count > 0) groups.Add(new GlobalGroup(name, hits));
    }
}
