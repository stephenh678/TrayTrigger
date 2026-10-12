using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TrayTrigger.Services.Theming;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>The starter theme file, the rules for a chosen picture, and what choosing one does to a theme file.</summary>
[Collection("ThemeService")]
public class ThemePictureTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tt-picture-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _themesBefore = ThemeCatalog.UserThemesDirectory;

    public ThemePictureTests()
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

    private string Picture(string name, int width, int height, bool jpeg = false, int orientation = 0)
    {
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, new byte[width * height], width);
        BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder() : new PngBitmapEncoder();
        BitmapMetadata? metadata = null;
        if (orientation != 0)
        {
            metadata = new BitmapMetadata("jpg");
            metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)orientation);
        }
        encoder.Frames.Add(BitmapFrame.Create(source, null, metadata, null));
        string path = Path.Combine(_folder, name);
        using (var file = File.Create(path)) encoder.Save(file);
        return path;
    }

    // ---- The starter file ---------------------------------------------------------------------------------

    [Fact]
    public void TheStarterFile_HasALineAndAComment_ForEveryColorTheAppHas()
    {
        var names = ThemeFileTemplate.Names.ToList();

        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Equal(ThemeCatalog.DarkPalette().Keys.OrderBy(n => n), names.OrderBy(n => n));
    }

    [Fact]
    public void TheStarterFile_PutsTheMainColorsFirst_AndReadsBackAsTheSameTheme()
    {
        var palette = ThemeCatalog.DarkPalette();
        string json = ThemeFileTemplate.Write("Copy", palette, isDark: true, ("Copy.jpg", 0.6));

        Assert.True(json.IndexOf("\"Background\"", StringComparison.Ordinal) < json.IndexOf("\"AccentHover\"", StringComparison.Ordinal));
        Assert.True(json.IndexOf("\"Accent\"", StringComparison.Ordinal) < json.IndexOf("\"SurfaceHover\"", StringComparison.Ordinal));
        Assert.Contains("// ", json);

        var theme = ThemeFileReader.Parse(json, "Copy", ThemeOrigin.User).Theme!;
        Assert.Empty(theme.Warnings!);
        Assert.Equal("Copy.jpg", theme.Background!.Image);
        foreach (var (name, color) in palette) Assert.Equal(color, ThemeCatalog.Resolve(theme)[name]);
    }

    // ---- What a picture has to be ----------------------------------------------------------------------------

    [Fact]
    public void APictureOfAGoodSize_IsAccepted()
    {
        var check = ThemePictureCheck.Inspect(Picture("good.png", 1920, 1080));

        Assert.True(check.Ok);
        Assert.Null(check.Note);
        Assert.StartsWith("1920×1080", check.Message);
    }

    [Theory]
    [InlineData(1000, 600)]
    [InlineData(1280, 500)]
    public void APictureThatIsTooSmall_IsRefused_WithTheSizeToUse(int width, int height)
    {
        var check = ThemePictureCheck.Inspect(Picture("small.png", width, height));

        Assert.False(check.Ok);
        Assert.Contains("1280×720", check.Message);
    }

    [Fact]
    public void AnUltrawidePicture_IsAcceptedWithANoteAboutCropping()
    {
        var check = ThemePictureCheck.Inspect(Picture("wide.png", 2560, 1080));

        Assert.True(check.Ok);
        Assert.Contains("cropped", check.Note);
    }

    [Fact]
    public void APictureTakenOnItsSide_IsJudgedByItsUprightSize()
    {
        var check = ThemePictureCheck.Inspect(Picture("phone.jpg", 1600, 900, jpeg: true, orientation: 6));

        Assert.True(check.Ok, check.Message);
        Assert.Equal(900, check.Width);
        Assert.Equal(1600, check.Height);
        Assert.Contains("cropped", check.Note);
    }

    [Fact]
    public void APictureThatCannotBeDecoded_IsRefusedWithAReason_AndNeverThrows()
    {
        string damaged = Path.Combine(_folder, "damaged.png");
        File.WriteAllBytes(damaged, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, 1, 2, 3]);

        var check = ThemePictureCheck.Inspect(damaged);

        Assert.False(check.Ok);
        Assert.Contains("could not be read", check.Message);
    }

    [Fact]
    public void APictureWiderThanWhatIsKept_SaysATrimmedCopyIsUsed()
    {
        var check = ThemePictureCheck.Inspect(Picture("big.png", 3840, 2160));

        Assert.True(check.Ok);
        Assert.Contains("scaled copy", check.Note);
        Assert.DoesNotContain("cropped", check.Note);
    }

    [Fact]
    public void ABmpOrAFileThatIsNotAPicture_IsRefused()
    {
        string text = Path.Combine(_folder, "fake.jpg");
        File.WriteAllText(text, "not a picture");

        Assert.False(ThemePictureCheck.Inspect(text).Ok);
        Assert.False(ThemePictureCheck.Inspect(Path.Combine(_folder, "x.bmp")).Ok);
    }

    // ---- Changing the image line as text -------------------------------------------------------------------------

    [Fact]
    public void SettingTheImage_ChangesOnlyThatValue_AndKeepsTheComments()
    {
        const string text = "{\n  // my theme\n  \"background\": {\n    \"image\": \"none\",\n    \"strength\": 0.6  // how strong\n  },\n  \"colors\": { \"Accent\": \"#7C3AED\" }\n}\n";

        string result = ThemeFileEditor.SetImage(text, "Forest.jpg")!;

        Assert.Equal(text.Replace("\"none\"", "\"Forest.jpg\""), result);
    }

    [Fact]
    public void SettingTheImage_WhenThereIsNoBackgroundBlock_AddsOne()
    {
        string result = ThemeFileEditor.SetImage("{ \"name\": \"A\" }", "A.png")!;

        var theme = ThemeFileReader.Parse(result, "A", ThemeOrigin.User).Theme!;
        Assert.Equal("A.png", theme.Background!.Image);
        Assert.Equal("A", theme.Name);
    }

    [Fact]
    public void SettingTheImage_WhenTheBlockHasNoImage_AddsTheImageToIt()
    {
        string result = ThemeFileEditor.SetImage("{ \"background\": { \"strength\": 0.3 } }", "A.png")!;

        var theme = ThemeFileReader.Parse(result, "A", ThemeOrigin.User).Theme!;
        Assert.Equal("A.png", theme.Background!.Image);
        Assert.Equal(0.3, theme.Background.Strength);
    }

    [Fact]
    public void AnImageInsideAComment_IsNotTheSetting()
    {
        const string text = "{\n  // \"image\": \"old.jpg\"\n  \"background\": { \"image\": \"real.jpg\" }\n}\n";

        string result = ThemeFileEditor.SetImage(text, "new.jpg")!;

        Assert.Contains("// \"image\": \"old.jpg\"", result);
        Assert.Contains("\"image\": \"new.jpg\"", result);
        Assert.DoesNotContain("real.jpg", result);
    }

    [Fact]
    public void SettingTheImage_WhenAnEarlierPartOfTheLineHoldsTwoSlashes_StillChangesTheRealSetting()
    {
        const string text = "{ \"author\": \"https://example.com\", \"background\": { \"image\": \"a.jpg\" } }";

        string result = ThemeFileEditor.SetImage(text, "new.jpg")!;

        Assert.Equal("new.jpg", ThemeFileReader.Parse(result, "t", ThemeOrigin.User).Theme!.Background!.Image);
        Assert.DoesNotContain("a.jpg", result);
    }

    [Fact]
    public void AnImageInsideABlockComment_IsNotTheSetting()
    {
        const string text = "{ /* \"image\": \"old.jpg\" */ \"background\": { \"image\": \"real.jpg\" } }";

        string result = ThemeFileEditor.SetImage(text, "new.jpg")!;

        Assert.Contains("/* \"image\": \"old.jpg\" */", result);
        Assert.Equal("new.jpg", ThemeFileReader.Parse(result, "t", ThemeOrigin.User).Theme!.Background!.Image);
    }

    [Fact]
    public void AGeneratedFile_KeepsApostrophesAndAccentsAsTyped()
    {
        string json = ThemeFileTemplate.Write("Stephen's Zoë & Co", ThemeCatalog.DarkPalette(), isDark: true);

        Assert.Contains("\"name\": \"Stephen's Zoë & Co\"", json);
        Assert.DoesNotContain("\\u", json);
        Assert.Equal("Stephen's Zoë & Co", ThemeFileReader.Parse(json, "x", ThemeOrigin.User).Theme!.Name);
    }

    [Fact]
    public void ThePicker_NamesDarkAsTheDefault_AndYourThemesAsYours() => WpfTestHost.Run(() =>
    {
        MakeThemeFiles("Forest.theme.json");

        var choices = new TrayTrigger.ViewModels.ThemeSettingsViewModel(new TrayTrigger.Models.AppSettings(), () => { }).Themes;

        Assert.Contains(choices, c => c.Id == "Dark" && c.DisplayName == "Dark (Default)");
        Assert.Contains(choices, c => c.Id == "Light" && c.DisplayName == "Light");
        Assert.Contains(choices, c => c.Id == "user:Forest" && c.DisplayName == "Forest (yours)");
    });

    // ---- Deleting a theme ----------------------------------------------------------------------------------------

    private void MakeThemeFiles(params string[] names)
    {
        Directory.CreateDirectory(ThemeCatalog.UserThemesDirectory);
        foreach (string name in names) File.WriteAllText(Path.Combine(ThemeCatalog.UserThemesDirectory, name), name.EndsWith(".json") ? "{ }" : "x");
    }

    private static T WithDiscard<T>(List<string> discarded, Func<T> body)
    {
        var before = ThemeService.Discard;
        ThemeService.Discard = path => { discarded.Add(Path.GetFileName(path)); File.Delete(path); };
        try { return body(); }
        finally { ThemeService.Discard = before; }
    }

    [Fact]
    public void DeletingATheme_RemovesItsFileAndItsOwnPictures_AndNothingElse() => WpfTestHost.Run(() =>
    {
        MakeThemeFiles("Forest.theme.json", "Forest-1.png", "Forest-2.jpg", "Forest.jpg", "Forest-background.jpg", "Forest-notes.txt", "Forestry-1.png", "Other.theme.json", "Other-1.png");
        var discarded = new List<string>();

        var outcome = WithDiscard(discarded, () => ThemeService.Instance.DeleteTheme("user:Forest"));

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal(5, outcome.Files);
        Assert.Equal(["Forest-1.png", "Forest-2.jpg", "Forest-background.jpg", "Forest.jpg", "Forest.theme.json"], discarded.OrderBy(n => n, StringComparer.Ordinal));
        Assert.True(File.Exists(Path.Combine(ThemeCatalog.UserThemesDirectory, "Forest-notes.txt")));
        Assert.True(File.Exists(Path.Combine(ThemeCatalog.UserThemesDirectory, "Forestry-1.png")));
        Assert.True(File.Exists(Path.Combine(ThemeCatalog.UserThemesDirectory, "Other.theme.json")));
        Assert.True(File.Exists(Path.Combine(ThemeCatalog.UserThemesDirectory, "Other-1.png")));
    });

    [Fact]
    public void WhereTheRecycleBinIsOff_TheOutcomeSaysDeleted_NotMovedToTheBin() => WpfTestHost.Run(() =>
    {
        MakeThemeFiles("Forest.theme.json", "Forest-1.png");
        var before = ThemeService.RecycleBinIsOn;
        ThemeService.RecycleBinIsOn = _ => false;
        try
        {
            var outcome = WithDiscard(new List<string>(), () => ThemeService.Instance.DeleteTheme("user:Forest"));

            Assert.True(outcome.Ok, outcome.Message);
            Assert.Equal("Forest was deleted.", outcome.Message);
        }
        finally { ThemeService.RecycleBinIsOn = before; }
    });

    [Theory]
    [InlineData(1, true, "Its theme file goes to the Recycle Bin.")]
    [InlineData(2, true, "Its theme file and 1 picture go to the Recycle Bin.")]
    [InlineData(4, true, "Its theme file and 3 pictures go to the Recycle Bin.")]
    [InlineData(1, false, "Its theme file is deleted for good, because the Recycle Bin is turned off for this drive.")]
    [InlineData(3, false, "Its theme file and 2 pictures are deleted for good, because the Recycle Bin is turned off for this drive.")]
    public void TheDeletePrompt_SaysWhereTheFilesGo(int files, bool binOn, string expected) =>
        Assert.Equal(expected, TrayTrigger.ViewModels.ThemeSettingsViewModel.DeleteDetail(files, binOn));

    [Fact]
    public void ReadingTheRecycleBinSetting_NeverThrows_AndAnUnknownDriveCountsAsOn()
    {
        _ = RecycleBinState.IsOnFor(Path.GetTempPath());
        Assert.True(RecycleBinState.IsOnFor(@"\\no-such-server\share\file"));
    }

    [Fact]
    public void ADeletedThemesPicture_StaysWhenAnotherThemeUsesIt() => WpfTestHost.Run(() =>
    {
        MakeThemeFiles("Forest.theme.json", "Forest-1.png");
        File.WriteAllText(Path.Combine(ThemeCatalog.UserThemesDirectory, "Cousin.theme.json"), "{ \"background\": { \"image\": \"Forest-1.png\" } }");
        var discarded = new List<string>();

        WithDiscard(discarded, () => ThemeService.Instance.DeleteTheme("user:Forest"));

        Assert.Equal(["Forest.theme.json"], discarded);
    });

    [Fact]
    public void DeletingATheme_NamedLikeAnotherThemesPicture_LeavesThatPicture() => WpfTestHost.Run(() =>
    {
        // Forest-1.jpg is the Forest theme's first picture, though it also reads as "the Forest-1 theme's picture".
        MakeThemeFiles("Forest.theme.json", "Forest-1.jpg", "Forest-1.theme.json", "Forest-1-1.jpg");
        var discarded = new List<string>();

        WithDiscard(discarded, () => ThemeService.Instance.DeleteTheme("user:Forest-1"));

        Assert.Equal(["Forest-1.theme.json", "Forest-1-1.jpg"], discarded);
    });

    [Fact]
    public void ATheme_WithATypoInItsFile_CanStillBeDeleted() => WpfTestHost.Run(() =>
    {
        MakeThemeFiles("Broken-1.png");
        File.WriteAllText(Path.Combine(ThemeCatalog.UserThemesDirectory, "Broken.theme.json"), "{ \"name\": ");
        var discarded = new List<string>();

        var outcome = WithDiscard(discarded, () => ThemeService.Instance.DeleteTheme("user:Broken"));

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal(["Broken-1.png", "Broken.theme.json"], discarded.OrderBy(n => n, StringComparer.Ordinal));
    });

    [Fact]
    public void ThemeFile_IsFoundByIdOnly_WhenItIsThere()
    {
        MakeThemeFiles("Forest.theme.json");

        Assert.EndsWith("Forest.theme.json", ThemeService.UserThemeFile("user:Forest"));
        Assert.Null(ThemeService.UserThemeFile("user:Gone"));
        Assert.Null(ThemeService.UserThemeFile("Dark"));
        Assert.Null(ThemeService.UserThemeFile(null));
    }

    [Fact]
    public void ABuiltInTheme_CannotBeDeleted() => WpfTestHost.Run(() =>
    {
        var discarded = new List<string>();

        var outcome = WithDiscard(discarded, () => ThemeService.Instance.DeleteTheme("Dark"));

        Assert.False(outcome.Ok);
        Assert.Empty(discarded);
    });
}
