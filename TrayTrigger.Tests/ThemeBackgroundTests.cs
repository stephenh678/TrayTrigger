using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TrayTrigger.Services.Theming;
using Xunit;

namespace TrayTrigger.Tests;

// Shares the picture state and the theme singleton with ThemeServiceApplyTests, so the two never run at once.
[Collection("ThemeService")]
public class ThemeBackgroundTests : IDisposable
{
    private readonly List<string> _folders = new();
    // Reading Colors.xaml needs the WPF Application object to exist, as in the running app.
    public ThemeBackgroundTests() => WpfTestHost.Run(() => { });

    public void Dispose()
    {
        foreach (string folder in _folders)
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { /* a test folder left in %TEMP% is not worth failing a test over */ }
        }
    }

    private string TempPicture(string name, int width = 8, int height = 4, bool jpeg = false, int orientation = 0)
    {
        string dir = Path.Combine(Path.GetTempPath(), "tt-theme-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _folders.Add(dir);
        var pixels = new byte[width * height];
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder() : new PngBitmapEncoder();
        BitmapMetadata? metadata = null;
        if (orientation != 0)
        {
            // The camera's "turn me this way" tag, as a phone writes it.
            metadata = new BitmapMetadata("jpg");
            metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)orientation);
        }
        encoder.Frames.Add(BitmapFrame.Create(source, null, metadata, null));
        using (var file = File.Create(Path.Combine(dir, name))) encoder.Save(file);
        return dir;
    }

    // ---- Reading a background block ------------------------------------------------------------------

    [Fact]
    public void ABackgroundBlock_IsRead_AndStrengthIsKeptBetweenZeroAndOne()
    {
        var theme = ThemeFileReader.Parse("""{ "background": { "image": "sky.jpg", "strength": 7 } }""", "t", ThemeOrigin.User).Theme!;
        Assert.Equal("sky.jpg", theme.Background!.Image);
        Assert.Equal(1.0, theme.Background.Strength);
    }

    [Fact]
    public void AnEmptyImageName_MeansNoPicture() =>
        Assert.Equal(ThemeBackground.None, ThemeFileReader.Parse("""{ "background": { "image": "" } }""", "t", ThemeOrigin.User).Theme!.Background!.Image);

    [Fact]
    public void ABackgroundStrengthThatIsNotANumber_IsSkippedWithAWarning()
    {
        var result = ThemeFileReader.Parse("""{ "background": { "image": "a.jpg", "strength": "lots" } }""", "t", ThemeOrigin.User);
        Assert.Null(result.Theme!.Background!.Strength);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void ThePosition_IsReadInAnyCase_AndDefaultsToCenter()
    {
        var left = ThemeFileReader.Parse("""{ "background": { "image": "a.jpg", "position": "LEFT" } }""", "t", ThemeOrigin.User).Theme!;
        Assert.Equal("left", ThemeCatalog.ResolveBackground(left)!.Position);

        var none = ThemeFileReader.Parse("""{ "background": { "image": "a.jpg" } }""", "t", ThemeOrigin.User).Theme!;
        Assert.Equal("center", ThemeCatalog.ResolveBackground(none)!.Position);
    }

    [Fact]
    public void APositionThatIsNotOneOfTheFive_IsSkippedWithAWarning()
    {
        var result = ThemeFileReader.Parse("""{ "background": { "image": "a.jpg", "position": "middle" } }""", "t", ThemeOrigin.User);
        Assert.Null(result.Theme!.Background!.Position);
        Assert.Single(result.Warnings);
    }

    [Theory]
    [InlineData("left", System.Windows.HorizontalAlignment.Left, System.Windows.VerticalAlignment.Center)]
    [InlineData("right", System.Windows.HorizontalAlignment.Right, System.Windows.VerticalAlignment.Center)]
    [InlineData("top", System.Windows.HorizontalAlignment.Center, System.Windows.VerticalAlignment.Top)]
    [InlineData("bottom", System.Windows.HorizontalAlignment.Center, System.Windows.VerticalAlignment.Bottom)]
    [InlineData("center", System.Windows.HorizontalAlignment.Center, System.Windows.VerticalAlignment.Center)]
    public void EachPosition_AnchorsThePicture_AndCenterIsExplicit(string position, System.Windows.HorizontalAlignment h, System.Windows.VerticalAlignment v)
    {
        // A picture scaled up to fill is bigger than its slot, and WPF draws that from the top left unless it is told otherwise.
        Assert.Equal(h, BackgroundPosition.Horizontal(position));
        Assert.Equal(v, BackgroundPosition.Vertical(position));
    }

    [Theory]
    [InlineData("center", -155.56, 0)]  // a 16:9 picture filling a 400 x 400 slot is 711 wide: 311 hang over, half on each side
    [InlineData("left", 0, 0)]
    [InlineData("right", -311, 0)]
    public void AFilledPicture_IsDrawnWhereThePositionSays_NotFromTheTopLeft(string position, double expectedX, double expectedY) => WpfTestHost.Run(() =>
    {
        var source = BitmapSource.Create(16, 9, 96, 96, PixelFormats.Gray8, null, new byte[16 * 9], 16);
        var slot = new System.Windows.Controls.Grid { Width = 400, Height = 400, ClipToBounds = true };
        var image = new System.Windows.Controls.Image
        {
            Source = source,
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = BackgroundPosition.Horizontal(position),
            VerticalAlignment = BackgroundPosition.Vertical(position),
        };
        slot.Children.Add(image);
        slot.Measure(new System.Windows.Size(400, 400));
        slot.Arrange(new System.Windows.Rect(0, 0, 400, 400));

        var at = image.TranslatePoint(new System.Windows.Point(0, 0), slot);
        Assert.Equal(expectedX, at.X, 0.6);
        Assert.Equal(expectedY, at.Y, 0.6);
    });

    [Fact]
    public void EveryBuiltInTheme_HasItsPicture_PinnedRight_SoTheTTMarkAlwaysShows()
    {
        // Dark, Light, Green, Amber and Purple: the TT mark is in the picture's lower right corner.
        Assert.Equal(5, ThemeCatalog.BuiltIns.Count(t => t.Name is "Dark" or "Light" or "Green" or "Amber" or "Purple"));
        foreach (var theme in ThemeCatalog.BuiltIns.Where(t => t.Origin == ThemeOrigin.BuiltIn && t.Name is "Dark" or "Light" or "Green" or "Amber" or "Purple"))
        {
            var resolved = ThemeCatalog.ResolveBackground(theme);
            Assert.True(resolved != null, theme.Name + " has no picture");
            Assert.Equal("right", resolved!.Position);
            Assert.NotNull(ThemeBackgroundLoader.Load(resolved, out string? problem));
            Assert.Null(problem);
        }
    }

    [Fact]
    public void ATheme_WithNoPictureOfItsOwn_TakesThePositionOfItsBase()
    {
        var plain = ThemeFileReader.Parse("""{ "base": "light" }""", "t", ThemeOrigin.User).Theme!;
        Assert.Equal("right", ThemeCatalog.ResolveBackground(plain)!.Position);

        var own = ThemeFileReader.Parse("""{ "base": "light", "background": { "image": "mine.jpg" } }""", "t", ThemeOrigin.User).Theme!;
        Assert.Equal("center", ThemeCatalog.ResolveBackground(own)!.Position);
    }

    [Theory]
    [InlineData("""{ "schema": "one" }""")]
    [InlineData("""{ "name": 5, "base": 7, "author": [], "colors": 3, "background": 4 }""")]
    [InlineData("""{ "colors": { "Accent": 12 } }""")]
    public void WrongTypesInAThemeFile_NeverThrow(string json) =>
        Assert.NotNull(ThemeFileReader.Parse(json, "t", ThemeOrigin.User));

    [Fact]
    public void ATheme_WithNoBackgroundBlock_SaysNothing() =>
        Assert.Null(ThemeFileReader.Parse("{}", "t", ThemeOrigin.User).Theme!.Background);

    // ---- Which picture a theme shows -------------------------------------------------------------------

    [Theory]
    [InlineData("Dark", "dark.jpg")]
    [InlineData("Light", "light.jpg")]
    [InlineData("Green", "green.jpg")]
    [InlineData("Amber", "amber.jpg")]
    public void EveryBuiltInTheme_HasItsOwnPicture(string id, string image)
    {
        var resolved = ThemeCatalog.ResolveBackground(ThemeCatalog.BuiltIn(id)!)!;
        Assert.Equal(image, resolved.Image);
        Assert.True(resolved.Embedded);
    }

    [Fact]
    public void HighContrast_HasNoPicture() =>
        Assert.Null(ThemeCatalog.ResolveBackground(ThemeCatalog.HighContrast()));

    [Theory]
    [InlineData("""{ "base": "dark" }""", "dark.jpg")]
    [InlineData("""{ "base": "light" }""", "light.jpg")]
    public void AUserTheme_WithNoBackground_TakesItsBasesPicture(string json, string image)
    {
        var resolved = ThemeCatalog.ResolveBackground(ThemeFileReader.Parse(json, "t", ThemeOrigin.User).Theme!)!;
        Assert.Equal(image, resolved.Image);
        Assert.True(resolved.Embedded);
    }

    [Fact]
    public void AUserTheme_CanTurnThePictureOff() =>
        Assert.Null(ThemeCatalog.ResolveBackground(ThemeFileReader.Parse("""{ "background": { "image": "none" } }""", "t", ThemeOrigin.User).Theme!));

    [Fact]
    public void AUserPicture_IsLookedForBesideTheThemeFile_AtSixtyPercentUnlessToldOtherwise()
    {
        var theme = ThemeFileReader.Parse("""{ "background": { "image": "sky.png" } }""", "t", ThemeOrigin.User, @"C:\Users\x\Themes\t.theme.json").Theme!;
        var resolved = ThemeCatalog.ResolveBackground(theme)!;
        Assert.False(resolved.Embedded);
        Assert.Equal(@"C:\Users\x\Themes", resolved.Directory);
        Assert.Equal(0.6, resolved.Strength);
    }

    // ---- Loading a picture --------------------------------------------------------------------------------

    [Fact]
    public void EveryBuiltInThemesPicture_IsInTheApp_AndDecodesToAFrozenImageWithinTheBudget()
    {
        var withPictures = ThemeCatalog.BuiltIns.Select(ThemeCatalog.ResolveBackground).Where(b => b != null).Select(b => b!).ToList();
        Assert.True(withPictures.Count >= 5);   // Dark, Light, Green, Amber, Purple

        foreach (var background in withPictures)
        {
            var picture = ThemeBackgroundLoader.Load(background, out string? problem);

            Assert.Null(problem);
            Assert.NotNull(picture);
            Assert.True(picture!.IsFrozen);
            Assert.InRange((long)picture.PixelWidth * picture.PixelHeight, 1, ThemeBackgroundLoader.MaxDecodePixels);
        }
    }

    [Theory]
    [InlineData(4000, 3000)]   // 4:3 landscape
    [InlineData(3000, 4000)]   // portrait: a phone photo
    [InlineData(3600, 3600)]   // square
    public void ABigPicture_IsKeptWithinThePixelBudget_WhateverItsShape_AndKeepsItsShape(int width, int height)
    {
        string dir = TempPicture("big.jpg", width, height, jpeg: true);

        var picture = ThemeBackgroundLoader.Load(new ResolvedBackground("big.jpg", false, dir, 0.5), out string? problem)!;

        Assert.Null(problem);
        Assert.InRange((long)picture.PixelWidth * picture.PixelHeight, 1, ThemeBackgroundLoader.MaxDecodePixels);
        Assert.Equal((double)width / height, (double)picture.PixelWidth / picture.PixelHeight, 2);
    }

    [Fact]
    public void ASmallPicture_IsNotEnlarged()
    {
        string dir = TempPicture("small.png", 1600, 900);

        var picture = ThemeBackgroundLoader.Load(new ResolvedBackground("small.png", false, dir, 0.5), out _)!;

        Assert.Equal(1600, picture.PixelWidth);
        Assert.Equal(900, picture.PixelHeight);
    }

    [Fact]
    public void APictureTakenOnItsSide_IsShownUpright()
    {
        string dir = TempPicture("phone.jpg", 1600, 900, jpeg: true, orientation: 6);

        var picture = ThemeBackgroundLoader.Load(new ResolvedBackground("phone.jpg", false, dir, 0.5), out string? problem)!;

        Assert.Null(problem);
        Assert.True(picture.IsFrozen);
        Assert.Equal(900, picture.PixelWidth);
        Assert.Equal(1600, picture.PixelHeight);
    }

    [Fact]
    public void APictureBesideTheThemeFile_Loads()
    {
        string dir = TempPicture("sky.png");
        var picture = ThemeBackgroundLoader.Load(new ResolvedBackground("sky.png", false, dir, 0.5), out string? problem);
        Assert.Null(problem);
        Assert.Equal(8, picture!.PixelWidth);
    }

    [Fact]
    public void AMissingPicture_GivesAReason_NotAnException()
    {
        string dir = TempPicture("sky.png");
        var picture = ThemeBackgroundLoader.Load(new ResolvedBackground("gone.png", false, dir, 0.5), out string? problem);
        Assert.Null(picture);
        Assert.Contains("not in the Themes folder", problem);
    }

    [Theory]
    [InlineData(@"..\escape.png")]
    [InlineData(@"sub\deeper.png")]
    public void APictureOutsideTheThemesFolder_IsRefused(string name)
    {
        string dir = TempPicture("sky.png");
        var picture = ThemeBackgroundLoader.Load(new ResolvedBackground(name, false, dir, 0.5), out string? problem);
        Assert.Null(picture);
        Assert.Contains("same folder", problem);
    }

    [Fact]
    public void AFileThatIsNotAPictureType_IsRefused()
    {
        var picture = ThemeBackgroundLoader.Load(new ResolvedBackground("notes.txt", false, Path.GetTempPath(), 0.5), out string? problem);
        Assert.Null(picture);
        Assert.Contains("JPG, PNG or BMP", problem);
    }

    [Fact]
    public void ACorruptPicture_GivesAReason()
    {
        string dir = TempPicture("sky.png");
        File.WriteAllText(Path.Combine(dir, "bad.png"), "this is not an image");
        var picture = ThemeBackgroundLoader.Load(new ResolvedBackground("bad.png", false, dir, 0.5), out string? problem);
        Assert.Null(picture);
        Assert.Contains("could not be read", problem);
    }

    [Fact]
    public void APictureWithTooManyPixels_IsRefused_BeforeItIsDecoded()
    {
        // 1 bit a pixel keeps the file and the test small: 10001 x 10001 is far over the 8K limit.
        string dir = Path.Combine(Path.GetTempPath(), "tt-theme-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _folders.Add(dir);
        const int side = 10001;
        var source = BitmapSource.Create(side, side, 96, 96, PixelFormats.BlackWhite, null, new byte[(side + 7) / 8 * side], (side + 7) / 8);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using (var file = File.Create(Path.Combine(dir, "huge.png"))) encoder.Save(file);

        var picture = ThemeBackgroundLoader.Load(new ResolvedBackground("huge.png", false, dir, 0.5), out string? problem);

        Assert.Null(picture);
        Assert.Contains("megapixels", problem);
    }

    // ---- Names, typos and exports -------------------------------------------------------------------------

    [Fact]
    public void AUserTheme_IsKnownByItsFileName_NotByTheNameInsideIt()
    {
        var theme = ThemeFileReader.Parse("""{ "name": "Pretty Name" }""", "plain-file", ThemeOrigin.User).Theme!;
        Assert.Equal("user:plain-file", theme.Id);
        Assert.Equal("Pretty Name", theme.Name);
    }

    [Theory]
    [InlineData(@"C:\T\Forest.theme.json", "Forest")]
    [InlineData(@"C:\T\a.themed.theme.json", "a.themed")]
    [InlineData(@"C:\T\UPPER.THEME.JSON", "UPPER")]
    public void TheFileNameWithoutTheThemeSuffix_IsWhatIdentifiesATheme(string path, string expected)
    {
        Assert.Equal(expected, ThemeFileReader.FileBaseName(path));
        Assert.Equal("user:" + expected, ThemeCatalog.UserThemeId(path));
    }

    [Fact]
    public void ACopyOfAThemeFile_IsAThemeOfItsOwn()
    {
        const string json = """{ "name": "Forest" }""";
        var original = ThemeFileReader.Parse(json, "Forest", ThemeOrigin.User).Theme!;
        var copy = ThemeFileReader.Parse(json, "Forest - Copy", ThemeOrigin.User).Theme!;
        Assert.NotEqual(original.Id, copy.Id);
    }

    [Fact]
    public void AColorNameThePaletteDoesNotHave_IsLeftOut_AndReported()
    {
        var theme = ThemeFileReader.Parse("""{ "colors": { "Acent": "#238636", "Accent": "#238636" } }""", "t", ThemeOrigin.User).Theme!;

        var resolved = ThemeCatalog.Resolve(theme);

        Assert.DoesNotContain("Acent", resolved.Keys);
        Assert.Equal(ThemeCatalog.DarkPalette().Count, resolved.Count);
        Assert.Equal(["Acent"], ThemeCatalog.UnknownColorNames(theme));
    }

    [Fact]
    public void WhatTheFileGotWrong_IsKeptWithTheTheme()
    {
        var theme = ThemeFileReader.Parse("""{ "base": "mauve", "colors": { "Accent": "green" } }""", "t", ThemeOrigin.User).Theme!;
        Assert.Equal(2, theme.Warnings!.Count);
    }

    // ---- On screen and in a file ------------------------------------------------------------------------------

    [Fact]
    public void ThePictureOnScreen_IsHeldForTheWindow_AndTintIsWhatIsLeftOfStrength()
    {
        var state = ThemeBackgroundState.Instance;
        state.Set(null, 1);
        Assert.False(state.HasImage);

        var picture = ThemeBackgroundLoader.Load(new ResolvedBackground("dark.jpg", true, null, 0.75), out _);
        state.Set(picture, 0.75);
        Assert.True(state.HasImage);
        Assert.Equal(0.25, state.TintOpacity, 3);
        state.Set(null, 1);
    }

    [Fact]
    public void AnExportedTheme_ReadsBackWithItsPicture()
    {
        string json = ThemeFileTemplate.Write("Copy", ThemeCatalog.DarkPalette(), isDark: true, ("Copy.jpg", 0.6));
        var theme = ThemeFileReader.Parse(json, "Copy", ThemeOrigin.User).Theme!;
        Assert.Equal("Copy.jpg", theme.Background!.Image);
        Assert.Equal(0.6, theme.Background.Strength);

        var none = ThemeFileReader.Parse(ThemeFileTemplate.Write("Copy", ThemeCatalog.DarkPalette(), isDark: true), "Copy", ThemeOrigin.User).Theme!;
        Assert.Equal(ThemeBackground.None, none.Background!.Image);
    }
}
