using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TrayTrigger.Services.Theming;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>Importing a theme made elsewhere into the Themes folder.</summary>
[Collection("ThemeService")]
public class ThemeImportTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tt-import-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _themesBefore = ThemeCatalog.UserThemesDirectory;
    private string Themes => ThemeCatalog.UserThemesDirectory;

    public ThemeImportTests()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "Download"));
        ThemeCatalog.UserThemesDirectory = Path.Combine(_folder, "Themes");
    }

    public void Dispose()
    {
        ThemeCatalog.UserThemesDirectory = _themesBefore;
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { /* a test folder left in %TEMP% is not worth failing a test over */ }
    }

    private string Download(string name, string text)
    {
        string path = Path.Combine(_folder, "Download", name);
        File.WriteAllText(path, text);
        return path;
    }

    private string Picture(string name)
    {
        var source = BitmapSource.Create(1920, 1080, 96, 96, PixelFormats.Gray8, null, new byte[1920 * 1080], 1920);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        string path = Path.Combine(_folder, "Download", name);
        using (var file = File.Create(path)) encoder.Save(file);
        return path;
    }

    private const string Plain = "{ \"name\": \"Ocean\", \"base\": \"dark\", \"colors\": { \"Accent\": \"#1F70C9\" } }";

    [Fact]
    public void A_plain_json_is_saved_as_a_theme_file()
    {
        var outcome = ThemeService.Instance.ImportThemeFiles(new[] { Download("Ocean.json", Plain) });
        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal(Path.Combine(Themes, "Ocean.theme.json"), outcome.ThemePath);
        Assert.NotNull(ThemeFileReader.ReadFile(outcome.ThemePath!, ThemeOrigin.User).Theme);
    }

    [Fact]
    public void A_name_in_use_is_never_overwritten()
    {
        var first = ThemeService.Instance.ImportThemeFiles(new[] { Download("Ocean.theme.json", Plain) });
        var second = ThemeService.Instance.ImportThemeFiles(new[] { Download("Ocean.theme.json", Plain) });
        Assert.True(second.Ok, second.Message);
        Assert.Equal(Path.Combine(Themes, "Ocean (2).theme.json"), second.ThemePath);
        Assert.True(File.Exists(first.ThemePath));
    }

    [Fact]
    public void A_chosen_picture_is_copied_and_set_in_the_file()
    {
        var outcome = ThemeService.Instance.ImportThemeFiles(new[] { Download("Ocean.json", Plain), Picture("sea.png") });
        Assert.True(outcome.Ok, outcome.Message);
        Assert.True(File.Exists(Path.Combine(Themes, "Ocean-1.png")));
        Assert.Equal("Ocean-1.png", ThemeFileReader.ReadFile(outcome.ThemePath!, ThemeOrigin.User).Theme!.Background!.Image);
    }

    [Fact]
    public void The_picture_the_theme_names_is_found_beside_the_file()
    {
        Picture("sea.png");
        string text = "{ \"name\": \"Ocean\", \"background\": { \"image\": \"sea.png\", \"strength\": 0.4 }, \"colors\": { \"Accent\": \"#1F70C9\" } }";
        var outcome = ThemeService.Instance.ImportThemeFiles(new[] { Download("Ocean.theme.json", text) });
        Assert.True(outcome.Ok, outcome.Message);
        var theme = ThemeFileReader.ReadFile(outcome.ThemePath!, ThemeOrigin.User).Theme!;
        Assert.Equal("Ocean-1.png", theme.Background!.Image);
        Assert.Equal(0.4, theme.Background.Strength);
    }

    [Fact]
    public void A_missing_picture_is_said_and_the_theme_still_imports()
    {
        string text = "{ \"name\": \"Ocean\", \"background\": { \"image\": \"gone.png\" }, \"colors\": { \"Accent\": \"#1F70C9\" } }";
        var outcome = ThemeService.Instance.ImportThemeFiles(new[] { Download("Ocean.theme.json", text) });
        Assert.True(outcome.Ok);
        Assert.Contains("gone.png", outcome.Message);
    }

    [Theory]
    [InlineData("{ }")]
    [InlineData("[1, 2]")]
    [InlineData("not json")]
    public void A_file_that_is_not_a_theme_is_refused_and_leaves_nothing(string text)
    {
        var outcome = ThemeService.Instance.ImportThemeFiles(new[] { Download("x.json", text) });
        Assert.False(outcome.Ok);
        Assert.False(Directory.Exists(Themes) && Directory.GetFiles(Themes).Length > 0);
    }

    [Fact]
    public void Pictures_alone_are_refused()
    {
        Assert.False(ThemeService.Instance.ImportThemeFiles(new[] { Picture("sea.png") }).Ok);
    }

    [Fact]
    public void A_theme_that_only_sets_a_text_size_is_a_theme()
    {
        var outcome = ThemeService.Instance.ImportThemeFiles(new[] { Download("Big.json", "{ \"name\": \"Big text\", \"textSize\": 130 }") });
        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal(130, ThemeFileReader.ReadFile(outcome.ThemePath!, ThemeOrigin.User).Theme!.TextSize);
    }

    [Fact]
    public void A_theme_that_only_sets_a_font_is_a_theme()
    {
        string font = TrayTrigger.Services.AppFonts.Installed.First(n => n != TrayTrigger.Services.AppFonts.DefaultName);
        var outcome = ThemeService.Instance.ImportThemeFiles(new[] { Download("Fancy.json", "{ \"name\": \"Fancy\", \"font\": \"" + font + "\" }") });
        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal(font, ThemeFileReader.ReadFile(outcome.ThemePath!, ThemeOrigin.User).Theme!.Font);
    }
}
