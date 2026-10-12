using System.Windows.Media;
using TrayTrigger.Services.Theming;
using Xunit;

namespace TrayTrigger.Tests;

public class ThemeTests
{
    // Reading Colors.xaml needs the WPF Application object to exist, as in the running app.
    public ThemeTests() => WpfTestHost.Run(() => { });

    private static Color C(string hex) { Assert.True(ThemeColor.TryParse(hex, out var c)); return c; }

    private static double Distance(Color a, Color b) =>
        Math.Max(Math.Max(Math.Abs(a.R - b.R), Math.Abs(a.G - b.G)), Math.Max(Math.Abs(a.B - b.B), Math.Abs(a.A - b.A)));

    // ---- Color parsing and the OKLCH round trip ---------------------------------------------------

    [Theory]
    [InlineData("#007ACC")]
    [InlineData("#66FFC400")]
    [InlineData("#FFFFFF")]
    public void AColor_FormatsBackToWhatWasParsed(string hex) =>
        Assert.Equal(hex, ThemeColor.Format(C(hex)));

    [Theory]
    [InlineData("007ACC")]
    [InlineData("#07C")]
    [InlineData("#GGGGGG")]
    [InlineData("blue")]
    [InlineData("")]
    [InlineData(null)]
    public void NotAColor_IsRefused(string? text) => Assert.False(ThemeColor.TryParse(text, out _));

    [Theory]
    [InlineData("#121214")]
    [InlineData("#007ACC")]
    [InlineData("#E06C75")]
    [InlineData("#FFFFFF")]
    [InlineData("#66FFC400")]
    public void OklchRoundTrip_ReturnsTheSameColor(string hex)
    {
        var c = C(hex);
        Assert.True(Distance(c, ThemeColor.FromOklch(ThemeColor.ToOklch(c))) <= 1);
    }

    [Fact]
    public void Contrast_OfBlackOnWhite_IsTwentyOne() =>
        Assert.InRange(ThemeColor.Contrast(C("#000000"), C("#FFFFFF")), 20.9, 21.1);

    // ---- Derivation ----------------------------------------------------------------------------------

    [Fact]
    public void ATheme_ThatChangesNothing_IsTheBaseExactly()
    {
        var dark = ThemeCatalog.DarkPalette();
        var resolved = ThemeDerivation.Resolve(dark, new Dictionary<string, Color>());
        Assert.Equal(dark.Count, resolved.Count);
        foreach (var (name, color) in dark) Assert.Equal(color, resolved[name]);
    }

    [Fact]
    public void AnAnchorSetToItsOwnValue_ChangesNothingElse()
    {
        var dark = ThemeCatalog.DarkPalette();
        var resolved = ThemeDerivation.Resolve(dark, new Dictionary<string, Color> { ["Accent"] = dark["Accent"] });
        foreach (string member in ThemeDerivation.Anchors["Accent"])
            Assert.True(Distance(dark[member], resolved[member]) <= 1, member);
    }

    [Fact]
    public void ANewAccent_MovesTheColorsDerivedFromIt_AndNoOthers()
    {
        var dark = ThemeCatalog.DarkPalette();
        var resolved = ThemeDerivation.Resolve(dark, new Dictionary<string, Color> { ["Accent"] = C("#238636") });

        Assert.Equal(C("#238636"), resolved["Accent"]);
        foreach (string member in ThemeDerivation.Anchors["Accent"]) Assert.NotEqual(dark[member], resolved[member]);
        // The hover stays a lighter version of the same hue: green, not blue.
        Assert.True(resolved["AccentHover"].G > resolved["AccentHover"].B);
        Assert.Equal(dark["Surface"], resolved["Surface"]);
        Assert.Equal(dark["Danger"], resolved["Danger"]);
    }

    [Fact]
    public void AColorTheThemeNamesItself_IsNotDerived()
    {
        var dark = ThemeCatalog.DarkPalette();
        var resolved = ThemeDerivation.Resolve(dark, new Dictionary<string, Color> { ["Accent"] = C("#238636"), ["AccentHover"] = C("#112233") });
        Assert.Equal(C("#112233"), resolved["AccentHover"]);
    }

    [Fact]
    public void ANewBackground_MovesTheSurfacesWithIt()
    {
        var dark = ThemeCatalog.DarkPalette();
        var resolved = ThemeDerivation.Resolve(dark, new Dictionary<string, Color> { ["Background"] = C("#1E2430") });

        Assert.True(Distance(dark["Surface"], resolved["Surface"]) > 4);
        var (bgL, _, _) = ThemeColor.ToOklab(resolved["Background"]);
        var (surfaceL, _, _) = ThemeColor.ToOklab(resolved["Surface"]);
        Assert.True(surfaceL > bgL, "A card stays lighter than the window behind it.");
    }

    [Fact]
    public void ADerivedColor_KeepsItsTransparency()
    {
        var dark = ThemeCatalog.DarkPalette();
        var resolved = ThemeDerivation.Resolve(dark, new Dictionary<string, Color> { ["Warning"] = C("#CC8800") });
        Assert.Equal(dark["Highlight"].A, resolved["Highlight"].A);
    }

    // ---- Reading a file ------------------------------------------------------------------------------

    [Fact]
    public void AThemeFile_IsRead()
    {
        var result = ThemeFileReader.Parse("""{ "schema": 1, "name": "Forest", "author": "me", "base": "dark", "colors": { "Accent": "#238636", "BrushDanger": "#FF0000" } }""", "x", ThemeOrigin.User);

        Assert.NotNull(result.Theme);
        Assert.Equal("Forest", result.Theme!.Name);
        // The id is the file's name, not the name inside it, so renaming one never changes which theme is selected.
        Assert.Equal("user:x", result.Theme.Id);
        Assert.True(result.Theme.IsDark);
        Assert.Equal(C("#238636"), result.Theme.Colors["Accent"]);
        // A name written the way the brush is called is accepted too.
        Assert.Equal(C("#FF0000"), result.Theme.Colors["Danger"]);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void AFileWithoutAName_IsCalledByItsFileName() =>
        Assert.Equal("Mine", ThemeFileReader.Parse("{}", "Mine", ThemeOrigin.User).Theme!.Name);

    [Fact]
    public void ABadColor_IsSkippedWithAWarning_AndTheRestStillApplies()
    {
        var result = ThemeFileReader.Parse("""{ "colors": { "Accent": "blue", "Danger": "#FF0000" } }""", "t", ThemeOrigin.User);

        Assert.NotNull(result.Theme);
        Assert.DoesNotContain("Accent", result.Theme!.Colors.Keys);
        Assert.Contains("Danger", result.Theme.Colors.Keys);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void ABaseThatIsNeitherDarkNorLight_FallsBackToDark_WithAWarning()
    {
        var result = ThemeFileReader.Parse("""{ "base": "purple" }""", "t", ThemeOrigin.User);
        Assert.True(result.Theme!.IsDark);
        Assert.Single(result.Warnings);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1, 2]")]
    [InlineData("""{ "schema": 99 }""")]
    public void AFileThatCannotBeUsed_IsRefusedWithAReason(string json)
    {
        var result = ThemeFileReader.Parse(json, "t", ThemeOrigin.User);
        Assert.Null(result.Theme);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void AnEditorsTrailingCommaAndComments_AreAllowed() =>
        Assert.NotNull(ThemeFileReader.Parse("""{ /* mine */ "colors": { "Accent": "#238636", }, }""", "t", ThemeOrigin.User).Theme);

    // ---- The built-in themes ---------------------------------------------------------------------

    [Fact]
    public void TheBuiltInThemes_AreAllThere() =>
        Assert.Equal(["Dark", "Light", "Green", "Amber", "Purple"], ThemeCatalog.BuiltIns.Select(t => t.Id).ToArray());

    [Fact]
    public void Dark_IsTheColorsFile()
    {
        var dark = ThemeCatalog.DarkPalette();
        Assert.Equal(C("#121214"), dark["Background"]);
        Assert.Equal(40, dark.Count);
    }

    [Fact]
    public void Light_NamesEveryColorTheAppHas()
    {
        var light = ThemeCatalog.BuiltIn("Light")!;
        Assert.False(light.IsDark);
        // Each name the dark theme has is named here: a color left out would stay dark on a light page.
        Assert.Empty(ThemeCatalog.DarkPalette().Keys.Except(light.Colors.Keys));
        Assert.Empty(light.Colors.Keys.Except(ThemeCatalog.DarkPalette().Keys));
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    [InlineData("Green")]
    [InlineData("Amber")]
    [InlineData("Purple")]
    public void EveryBuiltInTheme_ReadsComfortably(string id) =>
        Assert.Empty(ThemeCatalog.ContrastProblems(ThemeCatalog.Resolve(ThemeCatalog.BuiltIn(id)!)));

    [Theory]
    [InlineData("Green")]
    [InlineData("Amber")]
    public void AnAccentTheme_ChangesOnlyTheAccentFamily(string id)
    {
        var theme = ThemeCatalog.BuiltIn(id)!;
        var resolved = ThemeCatalog.Resolve(theme);
        var dark = ThemeCatalog.DarkPalette();
        var accentFamily = ThemeDerivation.Anchors["Accent"].Append("Accent").ToHashSet();

        foreach (var (name, color) in dark)
        {
            if (accentFamily.Contains(name)) Assert.NotEqual(color, resolved[name]);
            else Assert.Equal(color, resolved[name]);
        }
    }

    [Fact]
    public void ALightBaseTheme_BuildsOnLight()
    {
        var theme = ThemeFileReader.Parse("""{ "base": "light", "colors": { "Accent": "#238636" } }""", "t", ThemeOrigin.User).Theme!;
        var resolved = ThemeCatalog.Resolve(theme);
        Assert.Equal(ThemeCatalog.LightPalette()["Surface"], resolved["Surface"]);
        Assert.Equal(C("#238636"), resolved["Accent"]);
    }

    // ---- High Contrast -------------------------------------------------------------------------

    [Fact]
    public void HighContrast_UsesWindowsOwnColors_AndNamesEveryColor()
    {
        var theme = ThemeCatalog.BuiltIn(ThemeCatalog.HighContrastId)!;
        var resolved = ThemeCatalog.Resolve(theme);

        Assert.Equal(ThemeCatalog.DarkPalette().Count, resolved.Count);
        Assert.Equal(System.Windows.SystemColors.WindowColor, resolved["Background"]);
        Assert.Equal(System.Windows.SystemColors.WindowTextColor, resolved["TextPrimary"]);
        Assert.Equal(System.Windows.SystemColors.HighlightColor, resolved["Accent"]);
    }

    [Fact]
    public void HighContrast_IsDarkWhenWindowsWindowIsDark() =>
        Assert.Equal(ThemeColor.Luminance(System.Windows.SystemColors.WindowColor) < 0.5, ThemeCatalog.HighContrast().IsDark);

    [Fact]
    public void HighContrast_IsOfferedInThePicker_ButIsNotAFile() =>
        Assert.DoesNotContain(ThemeCatalog.BuiltIns, t => t.Id == ThemeCatalog.HighContrastId);

    [Fact]
    public void Mix_GoesFromOneColorToTheOther()
    {
        Assert.Equal(C("#000000"), ThemeColor.Mix(C("#000000"), C("#FFFFFF"), 0));
        Assert.Equal(C("#FFFFFF"), ThemeColor.Mix(C("#000000"), C("#FFFFFF"), 1));
        Assert.Equal(C("#808080"), ThemeColor.Mix(C("#000000"), C("#FFFFFF"), 0.5));
    }

    // ---- Export ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    [InlineData("Green")]
    [InlineData("Amber")]
    [InlineData("Purple")]
    public void AnExportedTheme_ReadsBackAsTheSameColors(string id)
    {
        var builtIn = ThemeCatalog.BuiltIn(id)!;
        var palette = ThemeCatalog.Resolve(builtIn);
        string json = ThemeFileTemplate.Write("Copy", palette, builtIn.IsDark);

        var theme = ThemeFileReader.Parse(json, "Copy", ThemeOrigin.User).Theme!;
        var resolved = ThemeCatalog.Resolve(theme);

        Assert.Equal(builtIn.IsDark, theme.IsDark);
        foreach (var (name, color) in palette) Assert.Equal(color, resolved[name]);
    }

    [Fact]
    public void AnExportedFile_SetsOnlyTheMainColors_AndTheRestAreCommentedOut()
    {
        string json = ThemeFileTemplate.Write("Copy", ThemeCatalog.DarkPalette(), isDark: true);

        var set = ThemeFileReader.Parse(json, "Copy", ThemeOrigin.User).Theme!.Colors.Keys;

        Assert.Equal(ThemeDerivation.Anchors.Keys.OrderBy(n => n), set.OrderBy(n => n));
        Assert.Contains("// \"AccentHover\"", json);
    }

    [Fact]
    public void ChangingOnlyTheAccentInAnExportedFile_MovesTheColorsThatFollowIt()
    {
        string json = ThemeFileTemplate.Write("Copy", ThemeCatalog.DarkPalette(), isDark: true);
        string edited = json.Replace("\"Accent\": \"#007ACC\"", "\"Accent\": \"#238636\"");
        Assert.NotEqual(json, edited);

        var resolved = ThemeCatalog.Resolve(ThemeFileReader.Parse(edited, "Copy", ThemeOrigin.User).Theme!);

        // The same colors the built-in Green theme (a dark theme that sets only Accent) works out.
        var green = ThemeCatalog.Resolve(ThemeCatalog.BuiltIn("Green")!);
        foreach (string name in new[] { "AccentHover", "AccentPressed", "AccentText", "AccentWash", "BorderAccent" })
            Assert.Equal(green[name], resolved[name]);
    }

    [Fact]
    public void AColorTheThemeSetsItself_StaysALineInTheExportedFile()
    {
        var palette = new ThemePalette(ThemeCatalog.DarkPalette());
        palette["AccentHover"] = Color.FromRgb(0xFF, 0x00, 0xFF);

        string json = ThemeFileTemplate.Write("Copy", palette, isDark: true);

        var theme = ThemeFileReader.Parse(json, "Copy", ThemeOrigin.User).Theme!;
        Assert.Equal(Color.FromRgb(0xFF, 0x00, 0xFF), ThemeCatalog.Resolve(theme)["AccentHover"]);
        Assert.Contains("AccentHover", theme.Colors.Keys);
    }

    [Fact]
    public void RemovingTheSlashes_TurnsACommentedColorOn_WithoutBreakingTheFile()
    {
        string json = ThemeFileTemplate.Write("Copy", ThemeCatalog.DarkPalette(), isDark: true);

        var result = ThemeFileReader.Parse(json.Replace("// \"AccentHover\"", "\"AccentHover\""), "Copy", ThemeOrigin.User);

        Assert.NotNull(result.Theme);
        Assert.Contains("AccentHover", result.Theme!.Colors.Keys);
    }

    [Fact]
    public void ABuiltTheme_HasABrushForEveryColor_AndTheTabFades()
    {
        var palette = ThemeCatalog.Resolve(ThemeCatalog.BuiltIn("Light")!);
        var dictionary = ThemeService.Build(palette);

        foreach (var (name, color) in palette)
        {
            var brush = Assert.IsType<SolidColorBrush>(dictionary["Brush" + name]);
            Assert.Equal(color, brush.Color);
            Assert.True(brush.IsFrozen);
        }
        Assert.IsType<LinearGradientBrush>(dictionary["BrushTabStripFadeLeft"]);
        // The art overlays keep their colors in every theme.
        Assert.True(dictionary.Contains("ColorRaw990D0D12"));
    }
}
