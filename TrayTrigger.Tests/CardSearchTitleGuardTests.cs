using System.Xml.Linq;
using Xunit;
using static TrayTrigger.Tests.SourceTree;

namespace TrayTrigger.Tests;

/// <summary>
/// The card search (Views/CardSearch) names a card by the first readable text in it, in XAML order: a TextBlock's
/// text, a button's or check box's words, a list's rows. So every card on a searchable page has to open with its
/// heading - an icon glyph first is fine, a count, a pill or a button first would be the card's name in the search
/// results. And a heading band of its own in a tab's section would be a result of its own: it stays inside its card.
/// A card that opens with a long sentence and no heading (over 60 characters) is named by its tab, on purpose.
/// </summary>
public class CardSearchTitleGuardTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Views = "clr-namespace:TrayTrigger.Views";

    private static readonly string[] HeadingStyles = ["{StaticResource CardHeaderTitle}", "{StaticResource CardTitle}"];
    private const int MaxTitle = 60;

    // About is searched the same way but not yet held to this: its hero, environment strip, feature grid and
    // four support cards open with a button or a label (a known gap, left for its own change).
    public static TheoryData<string> Pages() => new() { "Views/SettingsView.xaml", "Views/SystemView.xaml" };

    [Theory]
    [MemberData(nameof(Pages))]
    public void EveryCard_OpensWithItsHeading(string file)
    {
        var doc = XDocument.Parse(Read(file), LoadOptions.SetLineInfo);
        var offenders = new List<string>();
        foreach (var section in doc.Descendants().Where(e => e.Attribute(Views + "CardSearch.Tab") != null))
        {
            foreach (var card in section.Elements().Where(e => !e.Name.LocalName.Contains('.')))
            {
                if ((string?)card.Attribute(Views + "CardSearch.IsHeader") == "True") continue;
                var first = FirstText(card);
                int line = ((System.Xml.IXmlLineInfo)card).LineNumber;
                if (first == null) continue;
                var (element, text) = first.Value;
                bool heading = element.Name.LocalName == "TextBlock" && HeadingStyles.Contains((string?)element.Attribute("Style"));
                bool headless = !text.StartsWith('{') && text.Length > MaxTitle;
                if (!heading && !headless)
                    offenders.Add($"{file}:{line}  card opens with \"{text}\" ({element.Name.LocalName}, line {((System.Xml.IXmlLineInfo)element).LineNumber})");
            }
        }
        Assert.True(offenders.Count == 0,
            "The card search would name these cards by something other than their heading. Put the heading (CardHeaderTitle or CardTitle) first, " +
            "and counts, pills and buttons after it in the XAML:\n" + string.Join('\n', offenders));
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void HeadingBands_StayInsideTheirCard(string file)
    {
        var doc = XDocument.Parse(Read(file), LoadOptions.SetLineInfo);
        var loose = doc.Descendants().Where(e => e.Attribute(Views + "CardSearch.Tab") != null)
            .SelectMany(s => s.Elements())
            .Where(e => (string?)e.Attribute("Style") == "{StaticResource CardHeaderBand}")
            .Select(e => $"{file}:{((System.Xml.IXmlLineInfo)e).LineNumber}")
            .ToList();
        Assert.True(loose.Count == 0, "A CardHeaderBand sits directly in a tab's section, where the search counts it as a card:\n" + string.Join('\n', loose));
    }

    /// <summary>The first piece of readable text in <paramref name="node"/>, in the order CardSearch.Collect reads it.</summary>
    private static (XElement Element, string Text)? FirstText(XElement node)
    {
        string local = node.Name.LocalName;
        if (local.Contains('.')) return null;                              // a property element: a style, a template, a tooltip body
        if (local == "TextBox") return null;                               // what's typed isn't searched
        if (local == "ItemsControl" && node.Attribute("ItemsSource") != null) return (node, "{list rows}");

        string? own = local switch
        {
            "TextBlock" => (string?)node.Attribute("Text") ?? InlineText(node),
            "Run" => (string?)node.Attribute("Text") ?? node.Value,
            "Button" or "CheckBox" or "RadioButton" or "ToggleButton" => (string?)node.Attribute("Content")
                ?? (node.Elements().Any() ? null : node.Value),
            // Any other content control (Label, ContentControl, ComboBoxItem...) with its words in Content.
            _ when node.Attribute("Content") is { } content => content.Value,
            "Expander" or "GroupBox" or "HeaderedContentControl" => (string?)node.Attribute("Header"),
            _ => null,
        };
        if (Readable(node, own)) return (node, own!.Trim());
        if (local == "TextBlock") return null;                             // its inlines were read as its text

        foreach (var child in node.Elements())
        {
            if (FirstText(child) is { } hit) return hit;
        }
        string? tip = (string?)node.Attribute("ToolTip");
        return Readable(node, tip) ? (node, tip!.Trim()) : null;
    }

    private static string? InlineText(XElement block)
    {
        string text = string.Concat(block.DescendantNodes().Select(n => n switch
        {
            XText t => t.Value,
            XElement { Name.LocalName: "Run" } r => (string?)r.Attribute("Text") ?? "",
            _ => "",
        }));
        return text.Trim().Length == 0 ? null : text;
    }

    /// <summary>CardSearch.TitleOf skips anything of two characters or fewer: an icon glyph is one.</summary>
    private static bool Readable(XElement node, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.StartsWith('{')) return !text.StartsWith("{x:Null", StringComparison.Ordinal);
        return text.Trim().Length > 2;
    }
}
