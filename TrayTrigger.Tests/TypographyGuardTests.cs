using System.Text.RegularExpressions;
using Xunit;
using static TrayTrigger.Tests.SourceTree;

namespace TrayTrigger.Tests;

/// <summary>
/// Keeps every text size defined in one place, <c>Resources/Typography.xaml</c>. A view names a size
/// (FontSize="{DynamicResource FontSizeBody}"); code asks <c>AppTypography</c> for it. A number written
/// anywhere else is a size that file cannot change and docs/style-guide.html cannot list. To add a size,
/// give it a name in Typography.xaml. A size is always a dynamic resource: the Text size setting changes the
/// sizes while the app runs, and a static one would stay as it was when its view loaded.
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
    public void NoView_ReadsATextSizeAsAStaticResource()
    {
        var offenders = Offenders(Sources("*.xaml"), new Regex("\\{StaticResource\\s+FontSize\\w+\\}"));

        Assert.True(offenders.Count == 0,
            "A text size is a {DynamicResource ...}: a static one is fixed when the view loads and ignores the Text size setting. Found:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void NoView_WritesAFontName()
    {
        // FontFamily="Segoe UI", or a Setter on FontFamily whose Value is a name; {StaticResource ...} and bindings pass.
        var named = new Regex(
            @"\bFontFamily=""[^{""]|<Setter\b[^>]*Property=""(?:[\w.]*\.)?FontFamily""[^>]*\bValue=""[^{""]");
        var offenders = Offenders(Sources("*.xaml").Where(f => f != "App.xaml"), named);

        Assert.True(offenders.Count == 0,
            "Font names belong in App.xaml (AppFont, IconFont, MonoFont), referenced by name. Found:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void NoCode_BuildsAFontFromAName()
    {
        var offenders = Offenders(Sources("*.cs").Where(f => f != "Services/AppFonts.cs"), new Regex(@"new\s+FontFamily\("));

        Assert.True(offenders.Count == 0,
            "Use a font from App.xaml with SetResourceReference instead of naming one. Found:\n" + string.Join('\n', offenders));
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
