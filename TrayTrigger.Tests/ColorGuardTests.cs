using System.Text.RegularExpressions;
using Xunit;
using static TrayTrigger.Tests.SourceTree;

namespace TrayTrigger.Tests;

/// <summary>
/// Keeps every color in the app defined in one place, <c>Resources/Colors.xaml</c>. A view names a color
/// ({StaticResource BrushSurface}); code asks <c>AppPalette</c> for it. A literal value anywhere else
/// is a color a theme cannot reach and docs/style-guide.html cannot list. To add a color, give it a name in
/// Colors.xaml. The few files below draw artwork, not UI, and are the only exceptions.
/// </summary>
public class ColorGuardTests
{
    // A hex value, or a color written as a word ("White", "Red"). Transparent is the same in any theme.
    private static readonly Regex XamlLiteral = new(
        "=\"#[0-9A-Fa-f]{3,8}\"" +
        "|\\b(Background|Foreground|Fill|Stroke|BorderBrush|Color|Value)=\"(White|Black|Red|Green|Blue|Yellow|Orange|Gray|Grey|Silver|DarkGray|LightGray|Navy|Teal|Purple|Pink|Cyan|Magenta|Gold|Brown)\"");

    [Fact]
    public void NoView_WritesAColorValue()
    {
        var offenders = Offenders(Sources("*.xaml").Where(f => f != "Resources/Colors.xaml"), XamlLiteral);

        Assert.True(offenders.Count == 0,
            "Colors belong in Resources/Colors.xaml, referenced by name. Found:\n" + string.Join('\n', offenders));
    }

    /// <summary>Drawn artwork rather than UI: the tray icon's tile, the cover-art overlay, the fallback game icon.</summary>
    private static readonly HashSet<string> ArtworkFiles = new()
    {
        "App.Tray.cs",                         // TrayIconTileBrush: the tray icon's own tile
        "Services/SteamMetadataService.cs", // System.Drawing overlay composited into poster images
        "Services/IconExtractorService.cs", // System.Drawing: draws the fallback game icon
        "Services/AppPalette.cs",           // formats colors; names none
        "ViewModels/PosterSample.cs",       // the plain covers of the made-up games in the theme preview: artwork, not theme colors
    };

    // Services/Theming/ is left out of the scan below: it is the code that parses, derives and builds colors, so it handles values by its nature.
    private static readonly Regex CodeLiteral = new(
        "Color\\.From(Rgb|Argb)\\(|\"#[0-9A-Fa-f]{6,8}\"|System\\.Drawing\\.Color\\.From|\\b(Colors|Brushes)\\.(?!Transparent\\b)\\w+");

    [Fact]
    public void NoCode_BuildsAColorFromValues()
    {
        var offenders = Offenders(Sources("*.cs").Where(f => !ArtworkFiles.Contains(f) && !f.StartsWith("Services/Theming/")), CodeLiteral);

        Assert.True(offenders.Count == 0,
            "Ask AppPalette for a named color from Resources/Colors.xaml instead of writing its value. Found:\n" + string.Join('\n', offenders));
    }

    /// <summary>
    /// Text is dimmed with a palette color, never with Opacity. Opacity blends the text with whatever is behind
    /// it, so the result is a color no theme names and none can set, and it drifts from surface to surface.
    /// Colors.xaml has TextSecondary, TextMuted and TextDisabled for the dimmer shades; a disabled link gets
    /// TextDisabled from HelpLinkButton itself. This looks at a TextBlock written with a number for Opacity,
    /// and at a Style for TextBlocks or for a link button that sets one. Images, shadows, cards and animated
    /// fades (an Opacity bound or animated, not written as a number) are not text and are not checked.
    /// </summary>
    [Fact]
    public void NoText_IsDimmedWithOpacity()
    {
        var textBlockOpacity = new Regex("<TextBlock\\b[^<>]*?\\sOpacity=\"[0-9.]+\"", RegexOptions.Singleline);
        var textStyle = new Regex(
            "<Style\\b(?=[^>]*(?:TargetType=\"(?:TextBlock|\\{x:Type TextBlock\\})\"|BasedOn=\"\\{StaticResource HelpLinkButton\\}\"))[^>]*>.*?</Style>",
            RegexOptions.Singleline);
        var opacitySetter = new Regex("<Setter\\b[^>]*Property=\"Opacity\"[^>]*Value=\"[0-9.]+\"");

        var offenders = new List<string>();
        foreach (var file in Sources("*.xaml").Where(f => f != "Resources/Colors.xaml"))
        {
            string text = Read(file);
            int LineOf(int index) => text.Take(index).Count(c => c == '\n') + 1;

            foreach (Match m in textBlockOpacity.Matches(text))
                offenders.Add($"{file}:{LineOf(m.Index + m.Value.LastIndexOf("Opacity", StringComparison.Ordinal))}  a TextBlock with Opacity");
            foreach (Match style in textStyle.Matches(text))
                foreach (Match setter in opacitySetter.Matches(style.Value))
                    offenders.Add($"{file}:{LineOf(style.Index + setter.Index)}  a text style that sets Opacity");
        }

        Assert.True(offenders.Count == 0,
            "Dim text with TextSecondary, TextMuted or TextDisabled from Resources/Colors.xaml, not with Opacity. Found:\n" + string.Join('\n', offenders));
    }

    /// <summary>
    /// A theme replaces Colors.xaml while the app runs. A {StaticResource} brush is looked up once, when
    /// its view loads, and never again, so it would keep the old theme's color. {DynamicResource} looks it
    /// up again when the dictionary changes.
    /// </summary>
    [Fact]
    public void NoView_ReadsAColorWithStaticResource()
    {
        var offenders = Offenders(Sources("*.xaml").Where(f => f != "Resources/Colors.xaml"),
            new Regex("\\{StaticResource\\s+(?:Brush|Color)\\w+\\}"));

        Assert.True(offenders.Count == 0,
            "Colors change with the theme, so a view reads them with {DynamicResource}. Found:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void EveryColorNameUsed_IsDefinedInColorsXaml()
    {
        var defined = Regex.Matches(Read("Resources/Colors.xaml"), "x:Key=\"(\\w+)\"").Select(m => m.Groups[1].Value).ToHashSet();

        var used = new List<(string Where, string Key)>();
        var xamlRef = new Regex("\\{(?:Static|Dynamic)Resource\\s+((?:Brush|Color)\\w+)\\}");
        foreach (string f in Sources("*.xaml"))
            foreach (Match m in xamlRef.Matches(Read(f)))
                used.Add((f, m.Groups[1].Value));

        var codeRef = new Regex("(?:AppPalette\\.(?:Brush|Hex|ColorOf)|TryFindResource|FindResource)\\(\"((?:Brush|Color)\\w+)\"");
        foreach (string f in Sources("*.cs"))
            foreach (Match m in codeRef.Matches(Read(f)))
                used.Add((f, m.Groups[1].Value));

        var missing = used.Where(u => !defined.Contains(u.Key)).Select(u => $"{u.Where}: {u.Key}").Distinct().ToList();
        Assert.True(missing.Count == 0, "Names used but not defined in Resources/Colors.xaml:\n" + string.Join('\n', missing));
        Assert.True(used.Count > 500, "The scan should find the app's color references; found " + used.Count);
    }
}
