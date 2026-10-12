using System.Text.RegularExpressions;
using Xunit;
using static TrayTrigger.Tests.SourceTree;

namespace TrayTrigger.Tests;

/// <summary>
/// Keeps every corner radius defined in one place, <c>Resources/Shapes.xaml</c>. A view names a radius
/// (CornerRadius="{StaticResource RadiusCard}"); code asks <c>AppShapes</c> for it. A number written anywhere
/// else is a radius that file cannot change and docs/style-guide.html cannot list. To add a radius, give it
/// a name in Shapes.xaml.
/// </summary>
public class ShapeGuardTests
{
    // CornerRadius="8" or "7,7,0,0", or a Setter on CornerRadius whose Value is a number.
    private static readonly Regex XamlNumber = new(
        "\\bCornerRadius=\"[0-9.,\\s]+\"|<Setter\\b[^>]*Property=\"(?:[\\w.]*\\.)?CornerRadius\"[^>]*\\bValue=\"[0-9.,\\s]+\"");

    [Fact]
    public void NoView_WritesACornerRadius()
    {
        var offenders = Offenders(Sources("*.xaml").Where(f => f != "Resources/Shapes.xaml"), XamlNumber);

        Assert.True(offenders.Count == 0,
            "Corner radii belong in Resources/Shapes.xaml, referenced by name. Found:\n" + string.Join('\n', offenders));
    }

    /// <summary>Drawn artwork rather than UI: the tray icon's tile, whose radius follows the icon's size.</summary>
    private static readonly HashSet<string> ArtworkFiles = new()
    {
        "App.xaml.cs",            // the tray icon tile
        "Services/AppShapes.cs",  // reads radii; names none
    };

    [Fact]
    public void NoCode_BuildsACornerRadiusFromNumbers()
    {
        var offenders = Offenders(Sources("*.cs").Where(f => !ArtworkFiles.Contains(f)), new Regex("new CornerRadius\\("));

        Assert.True(offenders.Count == 0,
            "Ask AppShapes for a named radius from Resources/Shapes.xaml instead of writing numbers. Found:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void EveryRadiusNameUsed_IsDefinedInShapesXaml()
    {
        var defined = Regex.Matches(Read("Resources/Shapes.xaml"), "x:Key=\"(\\w+)\"").Select(m => m.Groups[1].Value).ToHashSet();

        var used = new List<(string Where, string Key)>();
        var xamlRef = new Regex("\\{(?:Static|Dynamic)Resource\\s+(Radius\\w+)\\}");
        foreach (string f in Sources("*.xaml"))
            foreach (Match m in xamlRef.Matches(Read(f)))
                used.Add((f, m.Groups[1].Value));

        var codeRef = new Regex("AppShapes\\.Radius\\(\"(\\w+)\"\\)");
        foreach (string f in Sources("*.cs"))
            foreach (Match m in codeRef.Matches(Read(f)))
                used.Add((f, m.Groups[1].Value));

        var missing = used.Where(u => !defined.Contains(u.Key)).Select(u => $"{u.Where}: {u.Key}").Distinct().ToList();
        Assert.True(missing.Count == 0, "Names used but not defined in Resources/Shapes.xaml:\n" + string.Join('\n', missing));
        Assert.True(used.Count > 150, "The scan should find the app's corner radius references; found " + used.Count);
    }
}
