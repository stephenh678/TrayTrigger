using System.IO;
using System.Text.RegularExpressions;
using Xunit;
using static TrayTrigger.Tests.SourceTree;

namespace TrayTrigger.Tests;

/// <summary>
/// Keeps the words on buttons, menus and dialogs the same everywhere.
/// <list type="bullet">
/// <item>Putting back what TrayTrigger changed is <b>Put Back</b> (Put Back, Put Back Preset, Put Back Settings Now).
/// Restore is for backups, and Reset is for Settings' defaults.</item>
/// <item>Commands are Title Case: buttons, menu items, a dialog's buttons and its title. Links (HelpLinkButton)
/// and descriptions are sentence case.</item>
/// <item>A label's ellipsis is three dots ("Browse..."). The single "…" character is only for text cut short.</item>
/// </list>
/// </summary>
public class WordingGuardTests
{
    // Words that stay lower case inside a Title Case label.
    private static readonly HashSet<string> SmallWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "to", "in", "on", "of", "for", "by", "with", "from", "at", "as", "per", "vs", "&amp;", "&",
    };

    // Menu items that are a status line or a link to Help, not a command.
    private static readonly HashSet<string> NotCommands = new(StringComparer.Ordinal)
    {
        "RAWG needs a key in Settings",
        "Where this info comes from",
    };

    [Fact]
    public void OldNamesForPuttingBack_AreGone()
    {
        var retired = new Regex(@"Restore Previous|Undo Preset|Restore Settings Now|""Restore Defaults""|Open Containing Folder");
        var files = Sources("*.cs").Concat(Sources("*.xaml")).Concat(Sources("*.md").Where(f => f.StartsWith("Help/")));
        var offenders = Offenders(files, retired);

        Assert.True(offenders.Count == 0,
            "Putting back what TrayTrigger changed is \"Put Back\"; Restore is for backups. Found:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void ButtonsAndMenuItems_AreTitleCase()
    {
        var tag = new Regex(@"<(Button|MenuItem)\b([^>]*)>", RegexOptions.Singleline);
        var offenders = new List<string>();
        foreach (var file in Sources("*.xaml"))
        {
            string text = Read(file);
            foreach (Match m in tag.Matches(text))
            {
                string attributes = m.Groups[2].Value;
                if (attributes.Contains("HelpLinkButton")) continue;
                var label = Regex.Match(attributes, m.Groups[1].Value == "Button" ? @"\bContent=""([^""{]+)""" : @"\bHeader=""([^""{]+)""");
                if (!label.Success || IsTitleCase(label.Groups[1].Value)) continue;
                offenders.Add($"{file}:{LineOf(text, m.Index)}  {label.Groups[1].Value}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A button or menu item is Title Case (links stay sentence case). Found:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void DialogButtonsAndTitles_AreTitleCase()
    {
        var button = new Regex(@"\b(?:confirmText|cancelText|primaryText|secondaryText):\s*""([^""]+)""");
        var title = new Regex(@"ModernDialog\.\w+\(\s*[^,()]*(?:\([^()]*\))?[^,()]*,\s*""([^""]+)""", RegexOptions.Singleline);
        var offenders = new List<string>();
        foreach (var file in Sources("*.cs"))
        {
            string text = Read(file);
            foreach (Match m in button.Matches(text).Concat(title.Matches(text)))
            {
                if (!IsTitleCase(m.Groups[1].Value)) offenders.Add($"{file}:{LineOf(text, m.Index)}  {m.Groups[1].Value}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A dialog's title and buttons are Title Case. Found:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void NoLabel_UsesTheEllipsisCharacter()
    {
        // A letter right before "…" is a label ("Searching…"); "…" on its own marks text cut short.
        var offenders = Offenders(Sources("*.cs").Concat(Sources("*.xaml")), new Regex(@"\p{L}…"));

        Assert.True(offenders.Count == 0,
            "Write a label's ellipsis as three dots (\"...\"). Found:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void Text_UsesTheSharedTextRoles()
    {
        // Text written out in place with the size, weight and colour of a role in App.xaml: the drift that left one
        // Game Details heading blue and the rest grey, and System's card titles larger than Settings'.
        var roles = new (string Size, string Weight, string Brush, string Style)[]
        {
            ("FontSizeSubhead", "SemiBold", "BrushTextPrimary", "CardTitle"),
            ("FontSizeBody", "SemiBold", "BrushTextPrimary", "StrongText"),
            ("FontSizeBody", "SemiBold", "BrushTextMuted", "FieldLabel"),
            ("FontSizeBody", "SemiBold", "BrushTextSecondary", "CardLabel"),
            ("FontSizeBody", "SemiBold", "BrushAccentText", "SectionLabel"),
        };
        var tag = new Regex(@"<TextBlock\b([^>]*)>", RegexOptions.Singleline);
        var offenders = new List<string>();
        foreach (var file in Sources("*.xaml").Where(f => f != "App.xaml"))
        {
            string text = Read(file);
            foreach (Match m in tag.Matches(text))
            {
                string a = m.Groups[1].Value;
                if (a.Contains("Style=")) continue;
                string at = $"{file}:{LineOf(text, m.Index)}";
                // A badge's words: BadgeText sets their size and weight, the badge its colour.
                if (a.Contains("{DynamicResource FontSizeBadge}") && Regex.IsMatch(a, @"\bFontWeight=""(SemiBold|Bold)"""))
                    offenders.Add($"{at}  BadgeText");
                // SectionLabel is for a literal capitalised header; a bound value in the accent is something else.
                bool literalCaps = Regex.IsMatch(a, @"\bText=""[A-Z](?:[A-Z0-9 '/-]|&amp;){3,}""");
                foreach (var r in roles)
                {
                    if (r.Style == "SectionLabel" && !literalCaps) continue;
                    if (a.Contains($"{{DynamicResource {r.Size}}}") && a.Contains($"FontWeight=\"{r.Weight}\"") && a.Contains($"{{DynamicResource {r.Brush}}}"))
                        offenders.Add($"{at}  {r.Style}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Use the text role from App.xaml (Style=\"{StaticResource ...}\") instead of writing it out. Found:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void CardTitles_AreTitleCase()
    {
        var title = new Regex(@"<TextBlock\b[^>]*Style=""\{StaticResource CardTitle\}""[^>]*\bText=""([^""{]+)""", RegexOptions.Singleline);
        var offenders = Sources("*.xaml")
            .SelectMany(f => { string t = Read(f); return title.Matches(t).Select(m => (f, t, m)); })
            .Where(x => !IsTitleCase(x.m.Groups[1].Value))
            .Select(x => $"{x.f}:{LineOf(x.t, x.m.Index)}  {x.m.Groups[1].Value}")
            .ToList();

        Assert.True(offenders.Count == 0, "A card title is Title Case. Found:\n" + string.Join('\n', offenders));
    }

    [Theory]
    [InlineData("Put Back Settings Now", true)]
    [InlineData("Open the Steam Store Page", true)]
    [InlineData("All Cores (Windows default)", true)]
    [InlineData("S_kip for Now", true)]
    [InlineData("Change match...", false)]
    [InlineData("Close anyway", false)]
    public void TitleCaseCheck_ReadsLabelsTheWayItShould(string label, bool expected) =>
        Assert.Equal(expected, IsTitleCase(label));

    /// <summary>
    /// Every word after the first starts with a capital, a digit or a symbol, except the small words.
    /// A part in brackets is a note, not the command, and is not checked.
    /// </summary>
    private static bool IsTitleCase(string label)
    {
        if (NotCommands.Contains(label)) return true;
        string command = Regex.Replace(label, @"\([^)]*\)", string.Empty).Replace("_", string.Empty);
        var words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Skip(1).All(w => SmallWords.Contains(w) || !char.IsLower(w[0]));
    }

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;
}
