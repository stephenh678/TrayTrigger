using System.Text.RegularExpressions;
using Xunit;
using static TrayTrigger.Tests.SourceTree;

namespace TrayTrigger.Tests;

/// <summary>
/// Keeps card spacing in Resources/Spacing.xaml. A card on a page or in a dialog (a Border with the card radius on the
/// surface or dialog-card background) takes CardPadding or DialogCardPadding by name, so the same kind of card has
/// the same room on every page. This is where System's cards were found tighter than Settings'.
/// </summary>
public class SpacingGuardTests
{
    [Fact]
    public void Cards_TakeTheirPaddingByName()
    {
        var border = new Regex(@"<Border\b([^>]*)>", RegexOptions.Singleline);
        var offenders = new List<string>();
        foreach (var file in Sources("*.xaml"))
        {
            string text = Read(file);
            foreach (Match m in border.Matches(text))
            {
                string a = m.Groups[1].Value;
                bool card = a.Contains("{DynamicResource RadiusCard}")
                            && (a.Contains("{DynamicResource BrushSurface}") || a.Contains("{DynamicResource BrushDialogCard}"));
                var padding = Regex.Match(a, @"\bPadding=""([0-9][^""]*)""");
                if (card && padding.Success && padding.Groups[1].Value is "14,12" or "14" or "12,10" or "14,10")
                    offenders.Add($"{file}:{text.AsSpan(0, m.Index).Count('\n') + 1}  Padding=\"{padding.Groups[1].Value}\"");
            }
        }

        Assert.True(offenders.Count == 0,
            "Use Padding=\"{StaticResource CardPadding}\" (a page card) or DialogCardPadding (a dialog card). Found:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void CardStyles_TakeTheirPaddingByName()
    {
        // The card styles kept in a view's resources (System's SettingsCard, Edit Theme's DialogCardBorder).
        var styles = new[] { ("Views/SystemView.xaml", "SettingsCard"), ("Views/ThemeEditDialog.xaml", "DialogCardBorder") };
        foreach (var (file, key) in styles)
        {
            string text = Read(file);
            int start = text.IndexOf($"x:Key=\"{key}\"", StringComparison.Ordinal);
            Assert.True(start >= 0, $"{file} has no style keyed {key}: update this test if the card style moved or was renamed.");
            string style = text[start..text.IndexOf("</Style>", start, StringComparison.Ordinal)];
            Assert.Contains("<Setter Property=\"Padding\" Value=\"{StaticResource ", style);
        }
    }
}
