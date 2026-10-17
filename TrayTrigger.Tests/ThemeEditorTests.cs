using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TrayTrigger.Services.Theming;
using TrayTrigger.ViewModels;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>The Edit Theme window's model, and what saving does to the Themes folder.</summary>
[Collection("ThemeService")]
public class ThemeEditorTests : IDisposable
{
    private static readonly Color Green = Color.FromRgb(0x23, 0x86, 0x36);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tt-editor-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _themesBefore = ThemeCatalog.UserThemesDirectory;
    private string Themes => ThemeCatalog.UserThemesDirectory;

    public ThemeEditorTests()
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

    private static ThemeEditorViewModel Open(string id) => new(ThemeCatalog.BuiltIn(id)!, ThemeService.Instance);

    /// <summary>Writes a theme file and opens it in the editor.</summary>
    private ThemeEditorViewModel OpenFile(string name, string text, out string path)
    {
        Directory.CreateDirectory(Themes);
        path = Path.Combine(Themes, name + ".theme.json");
        File.WriteAllText(path, text);
        return new ThemeEditorViewModel(ThemeFileReader.ReadFile(path, ThemeOrigin.User).Theme!, ThemeService.Instance);
    }

    private string Picture(string name, int width = 1920, int height = 1080)
    {
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, new byte[width * height], width);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        string path = Path.Combine(_folder, name);
        using (var file = File.Create(path)) encoder.Save(file);
        return path;
    }

    private static bool Save(ThemeEditorViewModel vm)
    {
        bool? closedWith = null;
        vm.RequestClose += ok => closedWith = ok;
        vm.SaveCommand.Execute(null);
        return closedWith == true;
    }

    private static ThemeDefinition Read(string path) => ThemeFileReader.ReadFile(path, ThemeOrigin.User).Theme!;

    // ---- Opening ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ABuiltInTheme_OpensAsANewTheme_NamedAfterIt() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");

        Assert.Equal("My Dark", vm.Name);
        Assert.Equal("New Theme", vm.WindowTitle);
        Assert.False(vm.CanOpenFile);
        Assert.False(vm.HasUnsavedChanges);
        Assert.Equal(ThemeCatalog.DarkPalette()["Accent"], vm.Accent.Color);
        Assert.Equal(7, vm.Rows.Count);
    });

    [Fact]
    public void ANameThatIsTaken_IsNotOfferedAgain() => WpfTestHost.Run(() =>
    {
        Directory.CreateDirectory(Themes);
        File.WriteAllText(Path.Combine(Themes, "My Dark.theme.json"), "{ }");

        Assert.Equal("My Dark 2", Open("Dark").Name);
    });

    [Fact]
    public void ThemeOfTheUser_OpensForEditing_WithItsOwnName() => WpfTestHost.Run(() =>
    {
        var vm = OpenFile("Forest", ThemeFileTemplate.Write("Forest", ThemeCatalog.DarkPalette(), isDark: true), out _);

        Assert.Equal("Forest", vm.Name);
        Assert.Equal("Edit Theme", vm.WindowTitle);
        Assert.True(vm.CanOpenFile);
    });

    // ---- Colors -----------------------------------------------------------------------------------------------------

    [Fact]
    public void ChangingTheAccent_MovesTheColorsThatFollowIt() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");

        vm.Accent.Color = Green;

        var green = ThemeCatalog.Resolve(ThemeCatalog.BuiltIn("Green")!);
        foreach (string name in new[] { "AccentHover", "AccentPressed", "AccentText", "AccentWash", "BorderAccent" })
            Assert.Equal(green[name], vm.Palette[name]);
        Assert.True(vm.HasUnsavedChanges);
    });

    [Fact]
    public void ThePaletteChanged_IsRaisedOncePerEdit() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");
        int raised = 0;
        vm.PaletteChanged += (_, _) => raised++;

        vm.Accent.Color = Green;
        vm.Danger.Color = Color.FromRgb(1, 2, 3);

        Assert.Equal(2, raised);
    });

    [Fact]
    public void TheCardColor_FollowsTheBackground_UntilItIsSetByHand() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");
        var cardBefore = vm.Surface.Color;

        vm.Background.Color = Color.FromRgb(0x10, 0x20, 0x30);
        Assert.NotEqual(cardBefore, vm.Surface.Color);
        Assert.Equal(vm.Palette["Surface"], vm.Surface.Color);

        vm.Surface.Hex = "#445566";
        vm.Background.Color = Color.FromRgb(0x30, 0x20, 0x10);
        Assert.Equal(Color.FromRgb(0x44, 0x55, 0x66), vm.Surface.Color);
    });

    [Fact]
    public void TypingAColor_UsesItAtOnce_AndComplainsOnlyWhenTheBoxIsLeft() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");
        var before = vm.Accent.Color;

        vm.Accent.Hex = "#23";
        Assert.Null(vm.Accent.Error);
        Assert.Equal(before, vm.Accent.Color);

        vm.Accent.CommitHex();
        Assert.NotNull(vm.Accent.Error);

        vm.Accent.Hex = "238636";
        Assert.Null(vm.Accent.Error);
        Assert.Equal(Green, vm.Accent.Color);

        vm.Accent.CommitHex();
        Assert.Equal("#238636", vm.Accent.Hex);
    });

    [Fact]
    public void ASeeThroughColorInTheFile_IsNotFlaggedWhenItsBoxIsLeftAlone_AndSurvivesASave() => WpfTestHost.Run(() =>
    {
        // Cards set to let the picture show through: #AARRGGBB, which the box shows as it is but cannot be typed back as six digits.
        var vm = OpenFile("Glass", "{ \"name\": \"Glass\", \"colors\": { \"Surface\": \"#CC1C1C23\" } }", out string path);
        Assert.Equal("#CC1C1C23", vm.Surface.Hex);

        vm.Surface.CommitHex();

        Assert.Null(vm.Surface.Error);
        Assert.Equal("#CC1C1C23", vm.Surface.Hex);
        vm.Accent.Color = Green;
        Assert.True(Save(vm));
        Assert.Equal(Color.FromArgb(0xCC, 0x1C, 0x1C, 0x23), Read(path).Colors["Surface"]);
    });

    [Fact]
    public void AColorThatIsNotOne_StopsASave() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");
        vm.Accent.Hex = "green";
        vm.Accent.CommitHex();

        Assert.False(Save(vm));
        Assert.Contains("Accent", vm.ValidationMessage);
        Assert.False(Directory.Exists(Themes));
    });

    [Fact]
    public void ChangingWhereItStartsFrom_StartsTheColorsAgainFromThere() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");
        vm.Accent.Color = Green;

        vm.IsDark = false;

        var light = ThemeCatalog.LightPalette();
        Assert.Equal(light["Background"], vm.Background.Color);
        Assert.Equal(light["Accent"], vm.Accent.Color);
        Assert.Equal(light["Surface"], vm.Surface.Color);
        Assert.True(vm.IsLight);
    });

    [Theory]
    [InlineData("Light", true)]
    [InlineData("Dark", false)]
    public void FlippingACopyToTheOtherStartingPoint_GivesThatThemeWholeNotAMixOfBoth(string from, bool flipsToDark) => WpfTestHost.Run(() =>
    {
        var vm = Open(from);

        vm.IsDark = flipsToDark;

        // Every color of the copy is the other built-in theme's: nothing of the first one's own fine-tuning is left behind.
        var other = flipsToDark ? ThemeCatalog.DarkPalette() : ThemeCatalog.LightPalette();
        foreach (var (name, color) in other) Assert.Equal(color, vm.Palette[name]);
    });

    [Fact]
    public void ResetColors_AfterFlippingTheStartingPoint_PutsTheStartingPointBackToo() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");
        vm.IsDark = false;

        vm.ResetColorsCommand.Execute(null);

        Assert.True(vm.IsDark);
        Assert.False(vm.ColorsDiffer);
    });

    [Fact]
    public void FlippingACopy_BringsTheOtherStartingThemesPicture() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");
        Assert.Contains("Dark", vm.PictureInfo);

        vm.IsDark = false;
        Assert.Contains("Light", vm.PictureInfo);

        vm.IsDark = true;
        Assert.Contains("Dark", vm.PictureInfo);
    });

    [Fact]
    public void ATypedHexError_GoesWhenAColorIsPickedInstead() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");
        vm.Accent.Hex = "#12";
        vm.Accent.CommitHex();
        Assert.True(vm.Accent.HasError);

        vm.Accent.Color = Green;

        Assert.False(vm.Accent.HasError);
    });

    [Fact]
    public void ACornerShapeAndSizeSetOnlyAbove_ComeBackAsThatChoiceWhenReopened() => WpfTestHost.Run(() =>
    {
        var leaf = ThemeCorners.Shapes.First(s => s.Id == "leaf");
        var radii = new Dictionary<string, System.Windows.CornerRadius>();
        foreach (var t in ThemeCorners.Targets.Take(3)) radii[t.Key] = leaf.At(t.Px(6), t.Max, t.FullPx);
        var theme = ThemeCatalog.BuiltIn("Dark")! with { Shape = radii };

        var corners = new ThemeCornersEditor(theme);

        Assert.Equal(leaf, corners.Shape);
        Assert.Equal(6, corners.Roundness);
        Assert.True(corners.DefaultUsed);
        Assert.True(corners.RoundnessEnabled);
    });

    [Fact]
    public void TheContrastLine_SaysWhenSomethingIsHardToRead() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");
        Assert.True(vm.ContrastOk);

        vm.Text.Color = vm.Background.Color;

        Assert.False(vm.ContrastOk);
        Assert.True(vm.HasContrastLine);
        Assert.Contains("may be hard to read", vm.ContrastLine);
    });

    [Fact]
    public void TheColorsThatMakeTextHardToRead_AreMarked_AndTheMarkGoesWhenItIsFixed() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");
        Assert.All(vm.Rows, r => Assert.False(r.HasContrastIssue));

        vm.Text.Color = vm.Background.Color;

        // Text on the window background and on a card: the Text, Background and card colors can all fix it.
        Assert.True(vm.Text.HasContrastIssue);
        Assert.True(vm.Background.HasContrastIssue);
        Assert.True(vm.Surface.HasContrastIssue);
        Assert.False(vm.Danger.HasContrastIssue);
        Assert.Contains("Text on", vm.Text.ContrastNote);
        // The note lists every pair the color is part of, in the words of the line at the foot.
        Assert.Contains("• Text on the window background is", vm.Background.ContrastNote);
        Assert.Contains("• Text on a card is", vm.Surface.ContrastNote);
        Assert.StartsWith("Text on the window background and 3 more pairs may be hard to read.", vm.ContrastLine);
        Assert.EndsWith("Hover a marked color for details.", vm.ContrastLine);

        vm.Text.ResetCommand.Execute(null);

        Assert.All(vm.Rows, r => Assert.False(r.HasContrastIssue));
        Assert.True(vm.ContrastOk);
        Assert.False(vm.HasContrastLine);
    });

    [Fact]
    public void AnAccentTooLightForItsButtonText_MarksTheAccent() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");

        vm.Accent.Color = Color.FromRgb(0xFF, 0xFF, 0xFF);

        Assert.True(vm.Accent.HasContrastIssue);
        Assert.Contains("Button text", vm.Accent.ContrastNote);
        Assert.False(vm.Text.HasContrastIssue);
    });

    [Fact]
    public void AColorCanBePutBack() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");
        vm.Accent.Color = Green;

        vm.Accent.ResetCommand.Execute(null);

        Assert.Equal(ThemeCatalog.DarkPalette()["Accent"], vm.Accent.Color);
        Assert.False(vm.HasUnsavedChanges);
    });

    // ---- Saving a new theme ---------------------------------------------------------------------------------------------

    [Fact]
    public void SavingANewTheme_WritesAFileThatReadsBackAsWhatWasEdited() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");
        vm.Name = "Forest";
        vm.Accent.Color = Green;

        Assert.True(Save(vm));

        string path = Path.Combine(Themes, "Forest.theme.json");
        Assert.Equal(path, vm.SavedPath);
        var theme = Read(path);
        Assert.Equal("Forest", theme.Name);
        Assert.True(theme.IsDark);
        Assert.Equal(Green, theme.Colors["Accent"]);
        Assert.DoesNotContain("AccentHover", theme.Colors.Keys);
        var resolved = ThemeCatalog.Resolve(theme);
        foreach (var (name, color) in vm.Palette) Assert.Equal(color, resolved[name]);
    });

    [Fact]
    public void ANewTheme_KeepsTheBuiltInPicture_CopiedAndNumbered() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");
        vm.Name = "Forest";

        Assert.True(Save(vm));

        Assert.True(File.Exists(Path.Combine(Themes, "Forest-1.jpg")));
        var theme = Read(Path.Combine(Themes, "Forest.theme.json"));
        Assert.Equal("Forest-1.jpg", theme.Background!.Image);
        Assert.Equal(1.0, theme.Background.Strength);
    });

    [Fact]
    public void ANewThemeWithAPicture_CopiesItUnchanged_AndNeverOverwritesAFile() => WpfTestHost.Run(() =>
    {
        Directory.CreateDirectory(Themes);
        File.WriteAllText(Path.Combine(Themes, "Forest-1.png"), "the user's own file");
        string source = Picture("mine.png");
        var vm = Open("Dark");
        vm.Name = "Forest";
        vm.PickPictureFile = () => source;
        vm.ChoosePictureCommand.Execute(null);
        vm.Strength = 0.4;

        Assert.True(Save(vm));

        Assert.Equal("the user's own file", File.ReadAllText(Path.Combine(Themes, "Forest-1.png")));
        Assert.True(File.Exists(Path.Combine(Themes, "Forest-2.png")));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(Themes, "Forest-2.png")));
        var theme = Read(Path.Combine(Themes, "Forest.theme.json"));
        Assert.Equal("Forest-2.png", theme.Background!.Image);
        Assert.Equal(0.4, theme.Background.Strength);
        Assert.True(File.Exists(source));
    });

    [Fact]
    public void ThePosition_IsSaved_ForANewTheme_AndForOneEditedInPlace() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");
        vm.Name = "Shore";
        Assert.Equal("right", vm.Position);  // the built-in themes keep their right edge in view
        vm.Position = "left";
        Assert.True(vm.HasUnsavedChanges);
        Assert.True(Save(vm));

        string path = Path.Combine(Themes, "Shore.theme.json");
        Assert.Equal("left", Read(path).Background!.Position);

        var again = new ThemeEditorViewModel(Read(path), ThemeService.Instance);
        Assert.Equal("left", again.Position);
        again.Position = "top";
        Assert.True(Save(again));
        Assert.Equal("top", Read(path).Background!.Position);
    });

    [Fact]
    public void SavingATheme_ReplacesTheFileInOneStep_AndLeavesNoTemporaryFile() => WpfTestHost.Run(() =>
    {
        var vm = OpenFile("Forest", ThemeFileTemplate.Write("Forest", ThemeCatalog.DarkPalette(), isDark: true), out string path);
        vm.Accent.Color = Green;

        Assert.True(Save(vm));

        Assert.Equal(Green, Read(path).Colors["Accent"]);
        Assert.Empty(Directory.GetFiles(Themes, "*.tmp"));
    });

    [Fact]
    public void TheThumbnailIsDimmed_ByWhatTheStrengthLeaves_AsTheSliderMoves() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        vm.Strength = 0.25;

        Assert.Equal(0.75, vm.ThumbnailTint, 3);
        Assert.Contains("ThumbnailTint", changed);
    });

    [Fact]
    public void AThemeThatTookItsBasesPicture_KeepsTheShownPosition_WhenItGetsAPictureOfItsOwn() => WpfTestHost.Run(() =>
    {
        // No "background" in the file: the picture and its position (Right) come from Dark, and the window shows Right.
        var vm = OpenFile("Plain", "{ \"name\": \"Plain\" }", out string path);
        Assert.Equal("right", vm.Position);
        string source = Picture("mine.png");
        vm.PickPictureFile = () => source;
        vm.ChoosePictureCommand.Execute(null);

        Assert.True(Save(vm));

        // A picture of its own with no "position" line would be centered: what was shown has to be what is saved.
        Assert.Equal("right", ThemeCatalog.ResolveBackground(Read(path))!.Position);
    });

    [Fact]
    public void AThemeThatTookItsBasesPicture_KeepsTheShownStrength_WhenItGetsAPictureOfItsOwn() => WpfTestHost.Run(() =>
    {
        // Dark's picture shows at 100%, which is what the slider says; a picture of its own with no "strength" line would be 60%.
        var vm = OpenFile("Plain", "{ \"name\": \"Plain\" }", out string path);
        Assert.Equal(1.0, vm.Strength);
        string source = Picture("mine.png");
        vm.PickPictureFile = () => source;
        vm.ChoosePictureCommand.Execute(null);

        Assert.True(Save(vm));

        Assert.Equal(1.0, ThemeCatalog.ResolveBackground(Read(path))!.Strength);
    });

    [Fact]
    public void RemovingThePicture_SavesAThemeWithNone() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");
        vm.Name = "Plain";
        vm.RemovePictureCommand.Execute(null);

        Assert.False(vm.HasPicture);
        Assert.True(Save(vm));

        Assert.Equal(ThemeBackground.None, Read(Path.Combine(Themes, "Plain.theme.json")).Background!.Image);
        Assert.Empty(Directory.GetFiles(Themes, "Plain-*"));
    });

    [Fact]
    public void APictureThatCannotBeUsed_IsReported_AndChangesNothing() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");
        string? title = null, message = null;
        vm.ShowProblem = (t, m) => { title = t; message = m; };
        vm.PickPictureFile = () => Picture("tiny.png", 640, 360);

        vm.ChoosePictureCommand.Execute(null);

        Assert.NotNull(title);
        Assert.Contains("1280", message);
        Assert.False(vm.HasUnsavedChanges);
    });

    [Fact]
    public void AnEditorThatPicksNothing_ChangesNothing() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");
        vm.PickPictureFile = () => null;

        vm.ChoosePictureCommand.Execute(null);

        Assert.False(vm.HasUnsavedChanges);
    });

    [Fact]
    public void ANameAlreadyTaken_IsReported_AndNothingIsWritten() => WpfTestHost.Run(() =>
    {
        Directory.CreateDirectory(Themes);
        File.WriteAllText(Path.Combine(Themes, "Forest.theme.json"), "{ }");
        var vm = Open("Dark");
        vm.Name = "Forest";

        Assert.False(Save(vm));

        Assert.Contains("already a theme", vm.ValidationMessage);
        Assert.Equal("{ }", File.ReadAllText(Path.Combine(Themes, "Forest.theme.json")));
        Assert.Empty(Directory.GetFiles(Themes, "Forest-*"));
    });

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NUL")]
    public void ANameThatCannotBeUsed_IsRefused(string name) => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");
        vm.Name = name;

        Assert.False(Save(vm));

        Assert.True(vm.HasValidationMessage);
        Assert.False(Directory.Exists(Themes));
    });

    [Fact]
    public void AFailedSave_TakesBackTheFilesItMade() => WpfTestHost.Run(() =>
    {
        // A folder where the second picture would go: the copy fails after the first file was made.
        Directory.CreateDirectory(Path.Combine(Themes, "Forest-1.png"));
        var vm = Open("Dark");
        vm.Name = "Forest";
        vm.PickPictureFile = () => Picture("mine.png");
        vm.ChoosePictureCommand.Execute(null);

        Assert.False(Save(vm));

        Assert.True(vm.HasValidationMessage);
        Assert.Empty(Directory.GetFiles(Themes, "*.theme.json"));
    });

    [Fact]
    public void CancelClosesWithoutSaving() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");
        vm.Accent.Color = Green;
        bool? closedWith = null;
        vm.RequestClose += ok => closedWith = ok;

        vm.CancelCommand.Execute(null);

        Assert.False(closedWith);
        Assert.False(Directory.Exists(Themes));
    });

    // ---- Editing a theme of the user's ---------------------------------------------------------------------------------

    [Fact]
    public void EditingATheme_ChangesOnlyTheLinesEdited_AndKeepsEveryComment() => WpfTestHost.Run(() =>
    {
        string original = ThemeFileTemplate.Write("Forest", ThemeCatalog.DarkPalette(), isDark: true);
        var vm = OpenFile("Forest", original, out string path);
        vm.Accent.Color = Green;

        Assert.True(Save(vm));

        Assert.Equal(original.Replace("\"Accent\": \"#007ACC\"", "\"Accent\": \"#238636\""), File.ReadAllText(path));
    });

    [Fact]
    public void ARenamedTheme_KeepsItsFile_AndOnlyChangesWhatThePickerShows() => WpfTestHost.Run(() =>
    {
        var vm = OpenFile("Forest", ThemeFileTemplate.Write("Forest", ThemeCatalog.DarkPalette(), isDark: true), out string path);
        vm.Name = "Deep Forest";

        Assert.True(Save(vm));

        Assert.Equal("Deep Forest", Read(path).Name);
        Assert.Equal("user:Forest", Read(path).Id);
    });

    [Fact]
    public void ACardColorTheFileSetsItself_StaysPut_WhenTheBackgroundMoves() => WpfTestHost.Run(() =>
    {
        var palette = new ThemePalette(ThemeCatalog.DarkPalette());
        var vm = OpenFile("Cards", "{ \"colors\": { \"Surface\": \"#222222\" } }", out string path);
        var card = vm.Surface.Color;

        vm.Background.Color = Color.FromRgb(0x10, 0x20, 0x30);

        Assert.Equal(card, vm.Surface.Color);
        Assert.True(Save(vm));
        Assert.Equal(Color.FromRgb(0x22, 0x22, 0x22), ThemeCatalog.Resolve(Read(path))["Surface"]);
    });

    [Fact]
    public void AColorSetByHand_KeepsItsValue_AndTheWindowSaysSo() => WpfTestHost.Run(() =>
    {
        var vm = OpenFile("Hand", "{ \"colors\": { \"Accent\": \"#007ACC\", \"AccentHover\": \"#FF00FF\" } }", out string path);
        Assert.True(vm.HasHandSetNote);
        Assert.Contains("AccentHover", vm.HandSetNote);

        vm.Accent.Color = Green;
        Assert.Equal(Color.FromRgb(0xFF, 0x00, 0xFF), vm.Palette["AccentHover"]);
        Assert.True(Save(vm));

        var resolved = ThemeCatalog.Resolve(Read(path));
        Assert.Equal(Color.FromRgb(0xFF, 0x00, 0xFF), resolved["AccentHover"]);
        Assert.Equal(Green, resolved["Accent"]);
    });

    [Fact]
    public void AFileFromAnOlderVersion_ThatListsEveryColor_StillSaves() => WpfTestHost.Run(() =>
    {
        // The first starter files wrote every color as a line, which pins each one.
        var all = string.Join(",\n", ThemeCatalog.DarkPalette().Select(p => $"    \"{p.Key}\": \"{ThemeColor.Format(p.Value)}\""));
        var vm = OpenFile("Old", "{\n  \"name\": \"Old\",\n  \"colors\": {\n" + all + "\n  }\n}\n", out string path);
        Assert.True(vm.HasHandSetNote);

        vm.Accent.Color = Green;

        Assert.True(Save(vm));
        var resolved = ThemeCatalog.Resolve(Read(path));
        Assert.Equal(Green, resolved["Accent"]);
        foreach (var (name, color) in vm.Palette) Assert.Equal(color, resolved[name]);
    });

    [Fact]
    public void AnotherPicture_IsNumberedAfterTheFirst_AndTheFirstStays() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");
        vm.Name = "Forest";
        vm.PickPictureFile = () => Picture("a.png");
        vm.ChoosePictureCommand.Execute(null);
        Assert.True(Save(vm));
        string path = Path.Combine(Themes, "Forest.theme.json");

        var again = new ThemeEditorViewModel(Read(path), ThemeService.Instance);
        again.PickPictureFile = () => Picture("b.png", 1600, 900);
        again.ChoosePictureCommand.Execute(null);
        Assert.True(Save(again));

        Assert.True(File.Exists(Path.Combine(Themes, "Forest-1.png")));
        Assert.True(File.Exists(Path.Combine(Themes, "Forest-2.png")));
        Assert.Equal("Forest-2.png", Read(path).Background!.Image);
    });

    [Fact]
    public void GoingBackToAnEarlierPicture_UsesTheFileWhereItIs() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");
        vm.Name = "Forest";
        vm.PickPictureFile = () => Picture("a.png");
        vm.ChoosePictureCommand.Execute(null);
        Assert.True(Save(vm));
        string path = Path.Combine(Themes, "Forest.theme.json");
        var second = new ThemeEditorViewModel(Read(path), ThemeService.Instance);
        second.PickPictureFile = () => Picture("b.png");
        second.ChoosePictureCommand.Execute(null);
        Assert.True(Save(second));

        var third = new ThemeEditorViewModel(Read(path), ThemeService.Instance);
        third.PickPictureFile = () => Path.Combine(Themes, "Forest-1.png");
        third.ChoosePictureCommand.Execute(null);
        Assert.True(Save(third));

        Assert.Equal("Forest-1.png", Read(path).Background!.Image);
        Assert.Equal(2, Directory.GetFiles(Themes, "Forest-*").Length);
    });

    [Fact]
    public void AnEditThatCouldNotBeReadBack_LeavesTheFileAsItWas() => WpfTestHost.Run(() =>
    {
        string original = ThemeFileTemplate.Write("Forest", ThemeCatalog.DarkPalette(), isDark: true);
        OpenFile("Forest", original, out string path);
        var source = Read(path);
        var palette = new ThemePalette(ThemeCatalog.Resolve(source));
        palette["Accent"] = Green;   // the editor says Accent changed, but no change is handed over to write

        var outcome = ThemeService.Instance.SaveTheme(new ThemeEditRequest(source, source.Name, true, palette, new Dictionary<string, Color>(), PictureChoice.Keep, null, 0.6));

        Assert.False(outcome.Ok);
        Assert.Equal(original, File.ReadAllText(path));
    });
}
