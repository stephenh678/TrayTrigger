using System.Collections.Generic;
using System.Linq;
using TrayTrigger.Models;

namespace TrayTrigger.ViewModels;

/// <summary>How one PCGamingWiki fact looks on the card: its words, an icon, and which colour group it is in.</summary>
public sealed class PcGamingWikiFactRowViewModel
{
    /// <summary>What the colour of the row means. The icon and the words say the same thing, so colour is never the only signal.</summary>
    public enum Tone { Good, Missing, Caution, Neutral }

    public PcGamingWikiFactRowViewModel(PcGamingWikiFact fact)
    {
        Label = fact.Label;
        (string word, string glyph, Tone tone) = fact.Support switch
        {
            PcGamingWikiSupport.Yes => ("Yes", "", Tone.Good),
            PcGamingWikiSupport.No => ("No", "", Tone.Missing),
            PcGamingWikiSupport.Fix => ("With a fix or mod", "", Tone.Caution),
            PcGamingWikiSupport.Limited => ("Limited", "", Tone.Caution),
            PcGamingWikiSupport.AlwaysOn => ("Always on", "", Tone.Neutral),
            _ => (string.Empty, string.Empty, Tone.Neutral),
        };

        ValueText = fact.Support == PcGamingWikiSupport.Info
            ? fact.Detail
            : string.IsNullOrEmpty(fact.Detail) ? word : $"{word}, {fact.Detail}";
        Glyph = glyph;
        Kind = tone;
        ToolTip = fact.Note;
    }

    public string Label { get; }
    public string ValueText { get; }
    public string Glyph { get; }
    public bool HasGlyph => Glyph.Length > 0;
    public Tone Kind { get; }
    public bool IsGood => Kind == Tone.Good;
    public bool IsMissing => Kind == Tone.Missing;
    public bool IsCaution => Kind == Tone.Caution;

    /// <summary>The wiki's own note for this line; shown on hover.</summary>
    public string ToolTip { get; }
    public bool HasToolTip => ToolTip.Length > 0;

    /// <summary>What a screen reader says for the row: the label, then the value.</summary>
    public string AutomationName => $"{Label}: {ValueText}";
}

/// <summary>One heading on the card (Display, Controls...) and its rows.</summary>
public sealed class PcGamingWikiGroupViewModel
{
    public PcGamingWikiGroupViewModel(string title, IEnumerable<PcGamingWikiFact> facts)
    {
        Title = title;
        Rows = facts.Select(f => new PcGamingWikiFactRowViewModel(f)).ToList();
    }

    public string Title { get; }
    public IReadOnlyList<PcGamingWikiFactRowViewModel> Rows { get; }

    /// <summary>The card's groups, in the order the wiki's facts arrive in (the parser already orders them).</summary>
    public static List<PcGamingWikiGroupViewModel> From(IEnumerable<PcGamingWikiFact> facts)
    {
        var groups = new List<PcGamingWikiGroupViewModel>();
        foreach (var group in facts.GroupBy(f => f.Group))
            groups.Add(new PcGamingWikiGroupViewModel(group.Key, group));
        return groups;
    }
}
