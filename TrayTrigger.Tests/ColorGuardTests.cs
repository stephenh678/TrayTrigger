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
        "App.xaml.cs",                      // TrayIconTileBrush: the tray icon's own tile
        "Services/SteamMetadataService.cs", // System.Drawing overlay composited into poster images
        "Services/IconExtractorService.cs", // System.Drawing: draws the fallback game icon
        "Services/AppPalette.cs",           // formats colors; names none
    };

    private static readonly Regex CodeLiteral = new(
        "Color\\.From(Rgb|Argb)\\(|\"#[0-9A-Fa-f]{6,8}\"|System\\.Drawing\\.Color\\.From|\\b(Colors|Brushes)\\.(?!Transparent\\b)\\w+");

    [Fact]
    public void NoCode_BuildsAColorFromValues()
    {
        var offenders = Offenders(Sources("*.cs").Where(f => !ArtworkFiles.Contains(f)), CodeLiteral);

        Assert.True(offenders.Count == 0,
            "Ask AppPalette for a named color from Resources/Colors.xaml instead of writing its value. Found:\n" + string.Join('\n', offenders));
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
