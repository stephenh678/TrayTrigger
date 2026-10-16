using System.IO;
using System.Windows;
using System.Windows.Media;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.Services.Theming;
using TrayTrigger.ViewModels;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>The font a theme carries. It behaves like the theme's text size: its own wins over Settings, none follows Settings.</summary>
[Collection("ThemeService")]
public class ThemeFontTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tt-themefont-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _themesBefore = ThemeCatalog.UserThemesDirectory;
    private string Themes => ThemeCatalog.UserThemesDirectory;

    // Two installed fonts that are not the default, whatever the machine has.
    private static string FontA => AppFonts.Installed.First(n => n != AppFonts.DefaultName);
    private static string FontB => AppFonts.Installed.First(n => n != AppFonts.DefaultName && n != FontA);

    public ThemeFontTests()
    {
        WpfTestHost.Run(() => ThemeService.Instance.WindowShown = true);
        Directory.CreateDirectory(_folder);
        ThemeCatalog.UserThemesDirectory = Path.Combine(_folder, "Themes");
    }

    public void Dispose()
    {
        ThemeCatalog.UserThemesDirectory = _themesBefore;
        AppFonts.SystemName = string.Empty;
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

    private static string OnScreen() => ((FontFamily)Application.Current.Resources["AppFont"]).Source;

    // ---- The file ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ATheme_WithNoFont_HasNone() => Assert.Null(Parse("{}").Font);

    [Fact]
    public void AFont_IsReadAsWritten_AndTrimmed() =>
        Assert.Equal(FontA, Parse("{ \"font\": \" " + FontA + " \" }").Font);

    [Fact]
    public void AFontThatIsNotInstalled_IsKept_ButWarnsAndFollowsSettings()
    {
        var theme = Parse("""{ "font": "No Such Font Exists Anywhere" }""");

        Assert.Equal("No Such Font Exists Anywhere", theme.Font);
        Assert.Contains(theme.Warnings!, w => w.Contains("font") && w.Contains("not installed"));
    }

    [Theory]
    [InlineData("""{ "font": 12 }""")]
    [InlineData("""{ "font": "" }""")]
    [InlineData("""{ "font": null }""")]
    public void AFontThatIsNotAName_IsSkipped_WithAWarning(string json)
    {
        var theme = Parse(json);

        Assert.Null(theme.Font);
        Assert.Contains(theme.Warnings!, w => w.Contains("font"));
    }

    [Fact]
    public void TheEditor_ChangesALineThatIsThere_AndAddsOneThatIsNot()
    {
        Assert.Equal(FontB, Parse(ThemeFileEditor.SetFont("{\n  \"font\": \"" + FontA + "\",\n}", FontB)!).Font);
        Assert.Equal(FontA, Parse(ThemeFileEditor.SetFont("{ \"name\": \"A\" }", FontA)!).Font);
    }

    [Fact]
    public void TheEditor_TakesTheLineOut_AndLeavesTheRest()
    {
        string text = "{\n  // my theme\n  \"name\": \"A\",\n  \"font\": \"" + FontA + "\",  // fancy\n  \"base\": \"dark\"\n}\n";

        string removed = ThemeFileEditor.RemoveFont(text)!;

        Assert.DoesNotContain("\"font\"", removed);
        Assert.Contains("// my theme", removed);
        Assert.Null(Parse(removed).Font);
        Assert.Equal("A", Parse(removed).Name);
    }

    [Fact]
    public void TheEditor_RemovingALineThatIsNotThere_ChangesNothing() =>
        Assert.Equal("{ \"name\": \"A\" }", ThemeFileEditor.RemoveFont("{ \"name\": \"A\" }"));

    // ---- The editor -------------------------------------------------------------------------------------------------

    [Fact]
    public void ABuiltInTheme_OpensFollowingSettings() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");

        Assert.True(vm.UseSettingsFont);
        Assert.False(vm.HasOwnFont);
        Assert.False(vm.CanResetType);
    });

    [Fact]
    public void AFontOfItsOwn_KeepsToInstalledFonts_AndTheCardsResetPutsBothBackToSettings() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");
        vm.UseSettingsFont = false;
        vm.Font = FontA;
        Assert.True(vm.CanResetType);

        vm.Font = "No Such Font Exists Anywhere";
        Assert.Equal(FontA, vm.Font);

        vm.UseSettingsTextSize = false;
        vm.ResetTypeCommand.Execute(null);
        Assert.True(vm.UseSettingsFont);
        Assert.True(vm.UseSettingsTextSize);
        Assert.False(vm.CanResetType);
    });

    [Fact]
    public void ThePreview_GoesByTheFontThatShows_NotTheOneKeptForLater() => WpfTestHost.Run(() =>
    {
        AppFonts.SystemName = FontB;
        var vm = Open("Dark");
        vm.UseSettingsFont = false;
        vm.Font = FontA;
        Assert.Equal(FontA, vm.EffectiveFont);

        vm.UseSettingsFont = true;
        Assert.Equal(FontB, vm.EffectiveFont);
    });

    [Fact]
    public void ChangingTheFont_IsAnUnsavedChange_AndTellsThePreview() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");
        int told = 0;
        vm.FontChanged += (_, _) => told++;
        Assert.False(vm.HasUnsavedChanges);

        vm.UseSettingsFont = false;

        Assert.True(vm.HasUnsavedChanges);
        Assert.Equal(1, told);
    });

    [Fact]
    public void ANewTheme_WithItsOwnFont_WritesTheLine_AndOpensAgainWithIt() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");
        vm.UseSettingsFont = false;
        vm.Font = FontA;

        Assert.True(Save(vm));

        var saved = ThemeFileReader.ReadFile(vm.SavedPath!, ThemeOrigin.User).Theme!;
        Assert.Equal(FontA, saved.Font);
        var again = new ThemeEditorViewModel(saved, ThemeService.Instance);
        Assert.False(again.UseSettingsFont);
        Assert.Equal(FontA, again.Font);
    });

    [Fact]
    public void ANewTheme_ThatFollowsSettings_WritesNoLine() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");
        vm.Accent.Color = System.Windows.Media.Color.FromRgb(0x23, 0x86, 0x36);

        Assert.True(Save(vm));

        Assert.DoesNotContain("\"font\"", File.ReadAllText(vm.SavedPath!));
    });

    [Fact]
    public void ANewTheme_ForcedToTheDefaultFont_KeepsItsLine() => WpfTestHost.Run(() =>
    {
        AppFonts.SystemName = FontA;
        var vm = Open("Dark");
        vm.UseSettingsFont = false;
        vm.Font = AppFonts.DefaultName;

        Assert.True(Save(vm));

        Assert.Equal(AppFonts.DefaultName, ThemeFileReader.ReadFile(vm.SavedPath!, ThemeOrigin.User).Theme!.Font);
    });

    [Fact]
    public void ATheme_OfTheUser_ChangesOnlyItsFontLine() => WpfTestHost.Run(() =>
    {
        string text = "{\n  // my theme\n  \"name\": \"Forest\",\n  \"font\": \"" + FontA + "\",\n  \"colors\": { \"Accent\": \"#238636\" }\n}\n";
        var vm = OpenFile("Forest", text, out string path);
        Assert.False(vm.UseSettingsFont);
        Assert.Equal(FontA, vm.Font);

        vm.Font = FontB;
        Assert.True(Save(vm));

        Assert.Contains("// my theme", File.ReadAllText(path));
        Assert.Equal(FontB, ThemeFileReader.ReadFile(path, ThemeOrigin.User).Theme!.Font);
    });

    [Fact]
    public void ATheme_OfTheUser_GoesBackToFollowingSettings_WhenItsLineIsTakenOut() => WpfTestHost.Run(() =>
    {
        string text = "{\n  // my theme\n  \"name\": \"Forest\",\n  \"font\": \"" + FontA + "\",\n  \"colors\": { \"Accent\": \"#238636\" }\n}\n";
        var vm = OpenFile("Forest", text, out string path);

        vm.UseSettingsFont = true;
        Assert.True(Save(vm));

        string after = File.ReadAllText(path);
        Assert.Contains("// my theme", after);
        Assert.DoesNotContain("\"font\"", after);
        Assert.Null(ThemeFileReader.ReadFile(path, ThemeOrigin.User).Theme!.Font);
    });

    [Theory]
    [InlineData("No Such Font Exists Anywhere")]
    [InlineData("arial")]
    public void ATheme_WithAFontLineAsItWasWritten_KeepsItWhenSavedForSomethingElse(string written) => WpfTestHost.Run(() =>
    {
        // A font this computer lacks (a theme shared by someone else), or one spelled in another case: opening the theme and
        // changing a color must not rewrite the author's line.
        string text = "{\n  \"name\": \"Forest\",\n  \"font\": \"" + written + "\",\n  \"colors\": { \"Accent\": \"#238636\" }\n}\n";
        var vm = OpenFile("Forest", text, out string path);

        vm.Accent.Color = System.Windows.Media.Color.FromRgb(0x11, 0x22, 0x33);
        Assert.True(Save(vm));

        Assert.Equal(written, ThemeFileReader.ReadFile(path, ThemeOrigin.User).Theme!.Font);
    });

    [Fact]
    public void AFont_IsFoundByAnyNameItHas_NotOnlyTheOneInTheLanguageOfTheComputer()
    {
        // A theme or a setting written on a computer in another language names the font in that language.
        foreach (var family in System.Windows.Media.Fonts.SystemFontFamilies.Where(f => f.FamilyNames.Count > 1))
        {
            if (AppFonts.Find(family.Source) is not { } shown) continue;   // a symbol font, left out of the list
            foreach (string name in family.FamilyNames.Values)
                Assert.True(AppFonts.Find(name) != null, $"{name} is a name of {shown}");
        }
    }

    [Fact]
    public void TheNoteAboutAThemesOwnFont_FollowsTheThemeOnScreen() => WpfTestHost.Run(() =>
    {
        Directory.CreateDirectory(Themes);
        File.WriteAllText(Path.Combine(Themes, "Forest.theme.json"), "{ \"name\": \"Forest\", \"font\": \"" + FontA + "\" }");
        var vm = new ThemeSettingsViewModel(new AppSettings(), () => { });
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        ThemeService.Instance.Apply(ThemeCatalog.DarkId);
        Assert.False(vm.HasFontNote);
        Assert.True(vm.FontCanBeSet);

        raised.Clear();
        ThemeService.Instance.Apply("user:Forest");

        Assert.Contains(nameof(ThemeSettingsViewModel.FontNote), raised);
        Assert.Contains(nameof(ThemeSettingsViewModel.HasFontNote), raised);
        Assert.True(vm.HasFontNote);
        Assert.Contains(FontA, vm.FontNote);
        // The Settings list is greyed out: a change there would show nothing while this theme is on.
        Assert.False(vm.FontCanBeSet);
        Assert.Contains(nameof(ThemeSettingsViewModel.FontCanBeSet), raised);

        ThemeService.Instance.Apply(ThemeCatalog.DarkId);
        Assert.True(vm.FontCanBeSet);
    });

    [Fact]
    public void TheTextSizeInSettings_IsGreyedOut_WhileTheThemeOnScreenHasItsOwn() => WpfTestHost.Run(() =>
    {
        Directory.CreateDirectory(Themes);
        File.WriteAllText(Path.Combine(Themes, "Big.theme.json"), "{ \"name\": \"Big\", \"textSize\": 125 }");
        var vm = new ThemeSettingsViewModel(new AppSettings(), () => { });
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        ThemeService.Instance.Apply(ThemeCatalog.DarkId);
        Assert.True(vm.TextSizeCanBeSet);

        raised.Clear();
        ThemeService.Instance.Apply("user:Big");

        Assert.False(vm.TextSizeCanBeSet);
        Assert.Contains("125%", vm.TextSizeNote);
        Assert.Contains(nameof(ThemeSettingsViewModel.TextSizeCanBeSet), raised);

        ThemeService.Instance.Apply(ThemeCatalog.DarkId);
        Assert.True(vm.TextSizeCanBeSet);
        Assert.False(vm.HasTextSizeNote);
    });

    // ---- The font in Settings, and on screen ------------------------------------------------------------------------

    [Fact]
    public void ATheme_WithItsOwnFont_WinsOverSettings_AndOthersFollowSettings() => WpfTestHost.Run(() =>
    {
        AppFonts.SystemName = FontB;
        AppFonts.ApplyForTheme(null);
        Assert.StartsWith(FontB, OnScreen());

        AppFonts.ApplyForTheme(FontA);
        Assert.StartsWith(FontA, OnScreen());

        // A font this computer does not have is not used: the theme shows in the Settings font.
        AppFonts.ApplyForTheme("No Such Font Exists Anywhere");
        Assert.StartsWith(FontB, OnScreen());
    });

    [Fact]
    public void AWindow_StartsInTheAppFont_AndFollowsIt_UnlessItHasOneOfItsOwn() => WpfTestHost.Run(() =>
    {
        AppFonts.SystemName = FontA;
        AppFonts.ApplyForTheme(null);
        var prepared = new Window();
        var followed = new Window();
        var own = new Window { FontFamily = new FontFamily(FontB) };

        WindowThemeService.PrepareForFirstShow(prepared);
        AppFonts.Follow(followed);
        AppFonts.Follow(own);

        Assert.StartsWith(FontA, prepared.FontFamily.Source);
        Assert.StartsWith(FontA, followed.FontFamily.Source);
        Assert.Equal(FontB, own.FontFamily.Source);

        AppFonts.SystemName = FontB;
        AppFonts.ApplyForTheme(null);
        Assert.StartsWith(FontB, followed.FontFamily.Source);

        // Windows that were never shown are still in the application's list until they are closed.
        prepared.Close();
        followed.Close();
        own.Close();
    });

    [Fact]
    public void TheFontInSettings_ApplyAtOnce_AndSaves() => WpfTestHost.Run(() =>
    {
        var settings = new AppSettings();
        int saved = 0;
        var vm = new ThemeSettingsViewModel(settings, () => saved++);
        ThemeService.Instance.Apply(ThemeCatalog.DarkId);

        vm.FontName = FontA;
        Assert.Equal(FontA, settings.FontName);
        Assert.StartsWith(FontA, OnScreen());
        Assert.Equal(1, saved);
        Assert.True(vm.CanResetFont);

        vm.FontName = "No Such Font Exists Anywhere";
        Assert.Equal(AppFonts.DefaultName, vm.FontName);
        Assert.Equal(string.Empty, settings.FontName);
        Assert.Equal(AppFonts.DefaultName, OnScreen());
    });
}
