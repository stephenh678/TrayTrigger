using System.IO;
using TrayTrigger.Services;
using TrayTrigger.Services.Theming;
using TrayTrigger.ViewModels;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>The text size a theme carries: how a scale changes each size, how the file says it, and what the editor does with it.</summary>
[Collection("ThemeService")]
public class TextSizeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tt-textsize-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _themesBefore = ThemeCatalog.UserThemesDirectory;
    private string Themes => ThemeCatalog.UserThemesDirectory;

    public TextSizeTests()
    {
        WpfTestHost.Run(() => ThemeService.Instance.WindowShown = true);
        Directory.CreateDirectory(_folder);
        ThemeCatalog.UserThemesDirectory = Path.Combine(_folder, "Themes");
    }

    public void Dispose()
    {
        ThemeCatalog.UserThemesDirectory = _themesBefore;
        WpfTestHost.Run(() => ThemeService.Instance.Apply(ThemeCatalog.DarkId));
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { /* a test folder left in %TEMP% is not worth failing a test over */ }
    }

    private static ThemeDefinition Parse(string json) => ThemeFileReader.Parse(json, "t", ThemeOrigin.User).Theme!;
    private static ThemeEditorViewModel Open(string id) => new(ThemeCatalog.BuiltIn(id)!, ThemeService.Instance);

    private ThemeEditorViewModel OpenFile(string name, string text, out string path)
    {
        Directory.CreateDirectory(Themes);
        path = Path.Combine(Themes, name + ".theme.json");
        File.WriteAllText(path, text);
        return new ThemeEditorViewModel(ThemeFileReader.ReadFile(path, ThemeOrigin.User).Theme!, ThemeService.Instance);
    }

    private static bool Save(ThemeEditorViewModel vm)
    {
        bool? closedWith = null;
        vm.RequestClose += ok => closedWith = ok;
        vm.SaveCommand.Execute(null);
        return closedWith == true;
    }

    // ---- The sizes -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("FontSizeBody", 12, 1.0, 12)]
    [InlineData("FontSizeBody", 12, 1.1, 13)]     // 13.2 to the nearest half
    [InlineData("FontSizeBody", 12, 0.9, 11)]     // 10.8
    [InlineData("FontSizeHeading", 20, 1.5, 30)]
    [InlineData("FontSizeSubhead", 14, 1.15, 16)] // 16.1
    public void ASize_IsScaledToTheNearestHalfPixel(string key, double designed, double scale, double expected) =>
        Assert.Equal(expected, AppTypography.Scaled(key, designed, scale));

    [Theory]
    [InlineData(0.9)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void TheBadgeText_NeverGoesBelowItsOwnSize(double scale) =>
        Assert.True(AppTypography.Scaled("FontSizeBadge", 9, scale) >= 9);

    [Theory]
    [InlineData(0.5, 0.9)]
    [InlineData(3.0, 1.5)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(1.25, 1.25)]
    public void AScale_IsKeptToTheAllowedRange(double scale, double expected) =>
        Assert.Equal(expected, AppTypography.ClampScale(scale), 3);

    // ---- The file --------------------------------------------------------------------------------------------------

    [Fact]
    public void ATheme_WithNoTextSize_HasNone() => Assert.Null(Parse("{}").TextSize);

    [Fact]
    public void ATextSize_IsReadInPercent() => Assert.Equal(120, Parse("""{ "textSize": 120 }""").TextSize);

    [Fact]
    public void ATextSizeOutOfRange_IsBroughtIn_WithAWarning()
    {
        var theme = Parse("""{ "textSize": 400 }""");

        Assert.Equal(AppTypography.MaxScalePercent, theme.TextSize);
        Assert.Contains(theme.Warnings!, w => w.Contains("textSize"));
    }

    [Fact]
    public void ATextSizeThatIsNotANumber_IsSkipped_WithAWarning()
    {
        var theme = Parse("""{ "textSize": "big" }""");

        Assert.Null(theme.TextSize);
        Assert.Contains(theme.Warnings!, w => w.Contains("textSize"));
    }

    [Fact]
    public void TheEditor_ChangesALineThatIsThere_AndAddsOneThatIsNot()
    {
        Assert.Contains("\"textSize\": 125", ThemeFileEditor.SetTextSize("{\n  \"textSize\": 110,\n}", 125));
        Assert.Equal(125, Parse(ThemeFileEditor.SetTextSize("{ \"name\": \"A\" }", 125)!).TextSize);
    }

    [Fact]
    public void TheEditor_LeavesACommentedLineAlone()
    {
        string text = "{\n  // \"textSize\": 110,\n  \"name\": \"A\"\n}";

        Assert.Equal(125, Parse(ThemeFileEditor.SetTextSize(text, 125)!).TextSize);
    }

    [Fact]
    public void TheEditor_TakesTheLineOut_AndLeavesTheRest()
    {
        string text = "{\n  // my theme\n  \"name\": \"A\",\n  \"textSize\": 110,  // big\n  \"base\": \"dark\"\n}\n";

        string removed = ThemeFileEditor.RemoveTextSize(text)!;

        Assert.DoesNotContain("textSize", removed.Replace("// my theme", ""));
        Assert.Contains("// my theme", removed);
        Assert.Null(Parse(removed).TextSize);
        Assert.Equal("A", Parse(removed).Name);
    }

    [Fact]
    public void TheEditor_RemovingALineThatIsNotThere_ChangesNothing() =>
        Assert.Equal("{ \"name\": \"A\" }", ThemeFileEditor.RemoveTextSize("{ \"name\": \"A\" }"));

    // ---- The editor ------------------------------------------------------------------------------------------------

    private static void WithSettingsSize(int percent, Action test)
    {
        int before = AppTypography.SystemPercent;
        AppTypography.SystemPercent = percent;
        try { test(); }
        finally { AppTypography.SystemPercent = before; }
    }

    [Fact]
    public void ABuiltInTheme_OpensFollowingSettings() => WpfTestHost.Run(() => WithSettingsSize(120, () =>
    {
        var vm = Open("Dark");

        Assert.True(vm.UseSettingsTextSize);
        Assert.False(vm.HasOwnTextSize);
        Assert.Equal(120, vm.EffectiveTextSize);
        Assert.False(vm.HasUnsavedChanges);
        Assert.Contains("120%", vm.TextSizeFollowNote);
    }));

    [Fact]
    public void ATextSizeOfItsOwn_StaysInRange_AndWarnsPastTheRecommendedSize() => WpfTestHost.Run(() => WithSettingsSize(100, () =>
    {
        var vm = Open("Dark");
        vm.UseSettingsTextSize = false;
        Assert.True(vm.HasUnsavedChanges);

        vm.TextSize = 400;
        Assert.Equal(AppTypography.MaxScalePercent, vm.TextSize);
        Assert.True(vm.TextSizeIsLarge);

        vm.TextSize = 10;
        Assert.Equal(AppTypography.MinScalePercent, vm.TextSize);
        Assert.False(vm.TextSizeIsLarge);

        vm.TextSize = 130;
        Assert.True(vm.CanResetType);
        vm.ResetTypeCommand.Execute(null);
        Assert.True(vm.UseSettingsTextSize);
        Assert.False(vm.CanResetType);
    }));

    [Fact]
    public void TheWarning_GoesByTheSizeThatShows_NotTheOneKeptForLater() => WpfTestHost.Run(() => WithSettingsSize(100, () =>
    {
        var vm = Open("Dark");
        vm.UseSettingsTextSize = false;
        vm.TextSize = 150;
        Assert.True(vm.TextSizeIsLarge);

        vm.UseSettingsTextSize = true;

        Assert.False(vm.TextSizeIsLarge);
        Assert.Equal(100, vm.EffectiveTextSize);
    }));

    [Fact]
    public void ANewTheme_WithItsOwnTextSize_WritesTheLine_AndOpensAgainWithIt() => WpfTestHost.Run(() => WithSettingsSize(100, () =>
    {
        var vm = Open("Dark");
        vm.UseSettingsTextSize = false;
        vm.TextSize = 120;

        Assert.True(Save(vm));

        var saved = ThemeFileReader.ReadFile(vm.SavedPath!, ThemeOrigin.User).Theme!;
        Assert.Equal(120, saved.TextSize);
        var again = new ThemeEditorViewModel(saved, ThemeService.Instance);
        Assert.False(again.UseSettingsTextSize);
        Assert.Equal(120, again.TextSize);
    }));

    [Fact]
    public void ANewTheme_ThatFollowsSettings_WritesNoLine() => WpfTestHost.Run(() => WithSettingsSize(100, () =>
    {
        var vm = Open("Dark");
        vm.Accent.Color = System.Windows.Media.Color.FromRgb(0x23, 0x86, 0x36);

        Assert.True(Save(vm));

        Assert.DoesNotContain("textSize", File.ReadAllText(vm.SavedPath!));
    }));

    [Fact]
    public void ANewTheme_ForcedTo100_KeepsItsLine() => WpfTestHost.Run(() => WithSettingsSize(130, () =>
    {
        var vm = Open("Dark");
        vm.UseSettingsTextSize = false;
        vm.TextSize = 100;

        Assert.True(Save(vm));

        Assert.Equal(100, ThemeFileReader.ReadFile(vm.SavedPath!, ThemeOrigin.User).Theme!.TextSize);
    }));

    [Fact]
    public void ATheme_OfTheUser_ChangesOnlyItsTextSizeLine() => WpfTestHost.Run(() => WithSettingsSize(100, () =>
    {
        string text = "{\n  // my theme\n  \"name\": \"Forest\",\n  \"textSize\": 110,\n  \"colors\": { \"Accent\": \"#238636\" }\n}\n";
        var vm = OpenFile("Forest", text, out string path);
        Assert.False(vm.UseSettingsTextSize);
        Assert.Equal(110, vm.TextSize);

        vm.TextSize = 125;
        Assert.True(Save(vm));
        string after = File.ReadAllText(path);
        Assert.Contains("// my theme", after);
        Assert.Equal(125, ThemeFileReader.ReadFile(path, ThemeOrigin.User).Theme!.TextSize);
    }));

    [Fact]
    public void ATheme_OfTheUser_GoesBackToFollowingSettings_WhenItsLineIsTakenOut() => WpfTestHost.Run(() => WithSettingsSize(100, () =>
    {
        string text = "{\n  // my theme\n  \"name\": \"Forest\",\n  \"textSize\": 110,\n  \"colors\": { \"Accent\": \"#238636\" }\n}\n";
        var vm = OpenFile("Forest", text, out string path);

        vm.UseSettingsTextSize = true;
        Assert.True(Save(vm));

        string after = File.ReadAllText(path);
        Assert.Contains("// my theme", after);
        Assert.DoesNotContain("\"textSize\"", after);
        Assert.Null(ThemeFileReader.ReadFile(path, ThemeOrigin.User).Theme!.TextSize);
    }));

    // ---- The size in Settings, and on screen ----------------------------------------------------------------------

    [Fact]
    public void TheSizeInSettings_StaysInRange_ApplyAtOnce_AndSaves() => WpfTestHost.Run(() => WithSettingsSize(100, () =>
    {
        var settings = new TrayTrigger.Models.AppSettings();
        int saves = 0;
        var vm = new ThemeSettingsViewModel(settings, () => saves++);
        try
        {
            vm.TextSize = 400;
            Assert.Equal(AppTypography.MaxScalePercent, vm.TextSize);
            Assert.Equal(150, settings.TextSizePercent);
            Assert.Equal(150, AppTypography.SystemPercent);
            Assert.Equal(18, AppTypography.Size("FontSizeBody"));
            Assert.True(vm.TextSizeIsLarge);

            vm.TextSize = 10;
            Assert.Equal(AppTypography.MinScalePercent, vm.TextSize);
            Assert.False(vm.TextSizeIsLarge);

            vm.ResetTextSizeCommand.Execute(null);
            Assert.Equal(100, vm.TextSize);
            Assert.False(vm.CanResetTextSize);
            Assert.True(saves >= 3);
        }
        finally { AppTypography.ApplyForTheme(null); }
    }));

    [Fact]
    public void ATheme_WithItsOwnSize_WinsOverSettings_AndOthersFollowSettings() => WpfTestHost.Run(() => WithSettingsSize(120, () =>
    {
        try
        {
            AppTypography.ApplyForTheme(null);
            Assert.Equal(14.5, AppTypography.Size("FontSizeBody"));     // 12 at 120%, to the nearest half

            AppTypography.ApplyForTheme(150);
            Assert.Equal(18, AppTypography.Size("FontSizeBody"));
        }
        finally { AppTypography.SystemPercent = 100; AppTypography.ApplyForTheme(null); }
    }));
}