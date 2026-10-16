using System.Text.RegularExpressions;
using TrayTrigger.Views;
using Xunit;
using static TrayTrigger.Tests.SourceTree;

namespace TrayTrigger.Tests;

/// <summary>The one look for notes under a setting: each kind has its own icon, and the colors are the theme's own brushes.</summary>
public class NoteBarTests
{
    [Theory]
    [InlineData(NoteKind.Info, "", "BrushAccentText")]
    [InlineData(NoteKind.Tip, "", "BrushWarning")]
    [InlineData(NoteKind.Warning, "", "BrushWarning")]
    public void EachKind_HasItsIconAndItsThemeColor(NoteKind kind, string glyph, string brush) =>
        Assert.Equal((glyph, brush), NoteBar.Look(kind));

    [Fact]
    public void NoTwoKinds_ShareAnIcon() =>
        Assert.Equal(Enum.GetValues<NoteKind>().Length, Enum.GetValues<NoteKind>().Select(k => NoteBar.Look(k).Glyph).Distinct().Count());

    [Fact]
    public void ANoteIsNeverBuiltByHand_ItIsANoteBar()
    {
        // The light bulb is the tip's icon and the triangle is the warning's: a line that carries one outside NoteBar is a note drawn its own way,
        // which is how the notes came to look different from page to page. The triangle also marks one color row in the theme editor
        // and "Needs attention" on the Activity page, which are not notes.
        var bulb = Offenders(Sources("*.xaml"), new Regex("&#xE82F;"));
        var triangle = Offenders(Sources("*.xaml").Where(f => f != "Views/ThemeColorRowControl.xaml" && f != "Views/ActivityView.xaml"), new Regex("&#xE7BA;"));

        var found = bulb.Concat(triangle).ToList();
        // A status line (a property named ...Status...) in the accent color is a note about what is so. The two that are not are short results
        // shown beside the button that produced them, where a card would weigh more than the button.
        var shortResults = new[] { "UpdateStatusText", "PosterRefreshStatus" };
        var statusLine = new Regex(@"<TextBlock\b[^>]*?Text=""\{Binding (\w*Status\w*)\}""[^>]*?>", RegexOptions.Singleline);
        foreach (string file in Sources("*.xaml"))
            foreach (Match m in statusLine.Matches(Read(file)))
                if (m.Value.Contains("BrushAccentText") && !shortResults.Contains(m.Groups[1].Value))
                    found.Add($"{file}: a status line, {m.Groups[1].Value}");

        Assert.True(found.Count == 0, "Use <views:NoteBar Kind=\"Tip\"/\"Warning\" Text=\"...\"/> for a note, so every note looks alike. Found:\n" + string.Join('\n', found));
    }
}
