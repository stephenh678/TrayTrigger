using System;
using System.Collections.Generic;

namespace TrayTrigger.Models;

/// <summary>
/// How a game does something, as its PCGamingWiki page rates it. The numbers are what is written to
/// the cache file, so a value is never renumbered.
/// </summary>
public enum PcGamingWikiSupport
{
    /// <summary>Not a rating: the fact is a piece of text (the graphics APIs, a save folder).</summary>
    Info = 0,
    Yes = 1,
    No = 2,
    /// <summary>The wiki's "hackable": not in the game, but a fix or mod adds it.</summary>
    Fix = 3,
    Limited = 4,
    AlwaysOn = 5,
}

/// <summary>One line of the game's PCGamingWiki facts: ultrawide, controller support, a save folder.</summary>
public class PcGamingWikiFact
{
    /// <summary>Display, Controls, Audio and accessibility, Online, Saves or Technical: the heading it sits under.</summary>
    public string Group { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public PcGamingWikiSupport Support { get; set; }
    /// <summary>What goes beside the rating, or the whole fact for <see cref="PcGamingWikiSupport.Info"/>:
    /// "up to 4 players", "DirectX 11, 12", a save folder. Empty when there is nothing to add.</summary>
    public string Detail { get; set; } = string.Empty;
    /// <summary>The wiki's own note for this line, cleaned to plain text. Empty when it had none or
    /// could not be cleaned safely.</summary>
    public string Note { get; set; } = string.Empty;
}

/// <summary>What PCGamingWiki says about one game, kept in the cache.</summary>
public class PcGamingWikiFacts
{
    /// <summary>The wiki page the facts came from, so the card can say which game (or edition) it read.</summary>
    public string PageTitle { get; set; } = string.Empty;
    public string PageUrl { get; set; } = string.Empty;
    public DateTime FetchedUtc { get; set; }
    public List<PcGamingWikiFact> Facts { get; set; } = new();
}

/// <summary>
/// One cached answer: the facts, or a note that the wiki had no usable page so the same game is not
/// asked about again for a few days.
/// </summary>
public class PcGamingWikiCacheEntry
{
    public DateTime FetchedUtc { get; set; }
    public bool NoFacts { get; set; }
    public PcGamingWikiFacts? Facts { get; set; }
}
