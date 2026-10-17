using System.IO;
using TrayTrigger.Services;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>The font the app's text is set in: what can be chosen, and what an unknown or missing font becomes.</summary>
public class AppFontsTests
{
    [Fact]
    public void TheDefaultFont_CanAlwaysBeChosen() =>
        Assert.Contains(AppFonts.DefaultName, AppFonts.Installed);

    [Fact]
    public void TheList_HasNoSymbolFontsOrSidewaysCopies() =>
        Assert.All(AppFonts.Installed, name =>
        {
            Assert.DoesNotContain("Wingdings", name);
            Assert.False(name.StartsWith('@'), name);
        });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("No Such Font Exists Anywhere")]
    public void ANameThatIsNotInstalled_IsTheDefault(string? name)
    {
        Assert.Null(AppFonts.Find(name));
        Assert.Equal(AppFonts.DefaultName, AppFonts.Build(name).Source);
    }

    [Fact]
    public void AFontIsFollowedByTheDefault_SoWhatItLacksFallsBack()
    {
        string other = AppFonts.Installed.First(n => n != AppFonts.DefaultName);
        Assert.Equal(other + ", " + AppFonts.DefaultName, AppFonts.Build(other).Source);
    }

    [Fact]
    public void TheFontAndTheTextSize_AreRememberedFromOneRunToTheNext()
    {
        string root = Path.Combine(Path.GetTempPath(), "tt-fontsettings-" + Guid.NewGuid().ToString("N"));
        try
        {
            string font = AppFonts.Installed.First(n => n != AppFonts.DefaultName);
            new TrayTrigger.Services.StorageService(Path.Combine(root, "data"), Path.Combine(root, "local"))
                .SaveSettings(new TrayTrigger.Models.AppSettings { FontName = font, TextSizePercent = 125 });

            var loaded = new TrayTrigger.Services.StorageService(Path.Combine(root, "data"), Path.Combine(root, "local")).LoadSettings();

            Assert.Equal(font, loaded.FontName);
            Assert.Equal(125, loaded.TextSizePercent);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* a test folder left in %TEMP% is not worth failing a test over */ }
        }
    }

    [Fact]
    public void TheNameIsMatchedWithoutRegardToCase()
    {
        string other = AppFonts.Installed.First(n => n != AppFonts.DefaultName);
        Assert.Equal(other, AppFonts.Find(" " + other.ToUpperInvariant() + " "));
    }
}
