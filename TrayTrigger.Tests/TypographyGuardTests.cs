using System.Text.RegularExpressions;
using Xunit;
using static TrayTrigger.Tests.SourceTree;

namespace TrayTrigger.Tests;

/// <summary>
/// Keeps every text size defined in one place, <c>Resources/Typography.xaml</c>. A view names a size
/// (FontSize="{StaticResource FontSizeBody}"); code asks <c>AppTypography</c> for it. A number written
/// anywhere else is a size that file cannot change and docs/style-guide.html cannot list. To add a size,
/// give it a name in Typography.xaml.
/// </summary>
public class TypographyGuardTests
{
    // FontSize="12", or a Setter on FontSize whose Value is a number.
    private static readonly Regex XamlNumber = new(
        "\\bFontSize=\"[0-9.]+\"|<Setter\\b[^>]*Property=\"(?:[\\w.]*\\.)?FontSize\"[^>]*\\bValue=\"[0-9.]+\"");

    [Fact]
    public void NoView_WritesATextSize()
    {
        var offenders = Offenders(Sources("*.xaml").Where(f => f != "Resources/Typography.xaml"), XamlNumber);

        Assert.True(offenders.Count == 0,
            "Text sizes belong in Resources/Typography.xaml, referenced by name. Found:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void NoCode_SetsATextSizeFromANumber()
    {
        var offenders = Offenders(Sources("*.cs"), new Regex("\\bFontSize\\s*=\\s*[0-9.]+\\s*[,;)]"));

        Assert.True(offenders.Count == 0,
            "Ask AppTypography for a named size from Resources/Typography.xaml instead of writing a number. Found:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void EveryTextSizeNameUsed_IsDefinedInTypographyXaml()
    {
        var defined = Regex.Matches(Read("Resources/Typography.xaml"), "x:Key=\"(\\w+)\"").Select(m => m.Groups[1].Value).ToHashSet();

        var used = new List<(string Where, string Key)>();
        var xamlRef = new Regex("\\{(?:Static|Dynamic)Resource\\s+(FontSize\\w+)\\}");
        foreach (string f in Sources("*.xaml"))
            foreach (Match m in xamlRef.Matches(Read(f)))
                used.Add((f, m.Groups[1].Value));

        var codeRef = new Regex("AppTypography\\.Size\\(\"(\\w+)\"\\)");
        foreach (string f in Sources("*.cs"))
            foreach (Match m in codeRef.Matches(Read(f)))
                used.Add((f, m.Groups[1].Value));

        var missing = used.Where(u => !defined.Contains(u.Key)).Select(u => $"{u.Where}: {u.Key}").Distinct().ToList();
        Assert.True(missing.Count == 0, "Names used but not defined in Resources/Typography.xaml:\n" + string.Join('\n', missing));
        Assert.True(used.Count > 500, "The scan should find the app's text size references; found " + used.Count);
    }
}
