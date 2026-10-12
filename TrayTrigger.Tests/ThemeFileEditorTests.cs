using System.Windows.Media;
using TrayTrigger.Services.Theming;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>Single-value edits of a theme file as text, and the color helpers the editor and its picker use.</summary>
public class ThemeFileEditorTests
{
    private static ThemeDefinition Read(string text) => ThemeFileReader.Parse(text, "t", ThemeOrigin.User).Theme!;

    // ---- Colors ----------------------------------------------------------------------------------------------

    [Fact]
    public void ALineThatSetsAColor_HasItsValueChanged_AndNothingElse()
    {
        const string text = "{\n  \"colors\": {\n    \"Accent\": \"#007ACC\",  // buttons\n    \"Danger\": \"#E06C75\",\n  }\n}\n";

        string result = ThemeFileEditor.SetColor(text, "Accent", Color.FromRgb(0x23, 0x86, 0x36))!;

        Assert.Equal(text.Replace("#007ACC", "#238636"), result);
    }

    [Fact]
    public void AColorWrittenWithItsBrushName_IsTheLineThatChanges()
    {
        const string text = "{\n  \"colors\": {\n    \"BrushAccent\": \"#007ACC\",\n  }\n}\n";

        string result = ThemeFileEditor.SetColor(text, "Accent", Color.FromRgb(0x23, 0x86, 0x36))!;

        Assert.Equal(text.Replace("#007ACC", "#238636"), result);
        Assert.Equal(Color.FromRgb(0x23, 0x86, 0x36), Read(result).Colors["Accent"]);
    }

    [Fact]
    public void ACommentedColor_IsTurnedOn_KeepingItsCommentAndComma()
    {
        const string text = "{\n  \"colors\": {\n    \"Accent\": \"#007ACC\",\n    // \"Surface\": \"#1C1C23\",  // cards\n  }\n}\n";

        string result = ThemeFileEditor.SetColor(text, "Surface", Color.FromRgb(0x10, 0x20, 0x30))!;

        Assert.Contains("    \"Surface\": \"#102030\",  // cards", result);
        Assert.Equal(Color.FromRgb(0x10, 0x20, 0x30), Read(result).Colors["Surface"]);
    }

    [Fact]
    public void AColorTheFileDoesNotMention_GetsALineInsideColors()
    {
        const string text = "{\n  \"name\": \"A\",\n  \"colors\": {\n    \"Accent\": \"#007ACC\"\n  }\n}\n";

        string result = ThemeFileEditor.SetColor(text, "Danger", Color.FromRgb(1, 2, 3))!;

        var theme = Read(result);
        Assert.Equal(Color.FromRgb(1, 2, 3), theme.Colors["Danger"]);
        Assert.Equal(Color.FromRgb(0x00, 0x7A, 0xCC), theme.Colors["Accent"]);
    }

    [Fact]
    public void AFileWithNoColorsObject_GetsOne()
    {
        string result = ThemeFileEditor.SetColor("{ \"name\": \"A\" }", "Accent", Color.FromRgb(9, 9, 9))!;

        var theme = Read(result);
        Assert.Equal(Color.FromRgb(9, 9, 9), theme.Colors["Accent"]);
        Assert.Equal("A", theme.Name);
    }

    [Fact]
    public void AColorNamedLikeAnother_IsNotMistakenForIt()
    {
        const string text = "{ \"colors\": { \"AccentHover\": \"#111111\", \"Accent\": \"#222222\" } }";

        var theme = Read(ThemeFileEditor.SetColor(text, "Accent", Color.FromRgb(0xAA, 0xAA, 0xAA))!);

        Assert.Equal(Color.FromRgb(0x11, 0x11, 0x11), theme.Colors["AccentHover"]);
        Assert.Equal(Color.FromRgb(0xAA, 0xAA, 0xAA), theme.Colors["Accent"]);
    }

    [Fact]
    public void ACommentedColorInsideABlockComment_IsNotTurnedOn()
    {
        const string text = "{ /*\n// \"Accent\": \"#111111\",\n*/ \"colors\": { } }";

        string result = ThemeFileEditor.SetColor(text, "Accent", Color.FromRgb(0xAA, 0xAA, 0xAA))!;

        Assert.Contains("// \"Accent\": \"#111111\"", result);
        Assert.Equal(Color.FromRgb(0xAA, 0xAA, 0xAA), Read(result).Colors["Accent"]);
    }

    // ---- Name, base, strength -----------------------------------------------------------------------------------

    [Fact]
    public void TheNameAndBase_AreChangedInPlace_OrAddedWhenMissing()
    {
        const string text = "{\n  \"name\": \"Old\",\n  \"base\": \"dark\"  // start\n}\n";

        var changed = Read(ThemeFileEditor.SetString(ThemeFileEditor.SetString(text, "name", "New's")!, "base", "light")!);
        Assert.Equal("New's", changed.Name);
        Assert.False(changed.IsDark);

        var added = Read(ThemeFileEditor.SetString("{ }", "name", "Added")!);
        Assert.Equal("Added", added.Name);
    }

    [Fact]
    public void TheStrength_IsChangedInPlace_AddedToABlock_OrGivenABlock()
    {
        Assert.Equal(0.3, Read(ThemeFileEditor.SetStrength("{ \"background\": { \"image\": \"a.jpg\", \"strength\": 0.6 } }", 0.3)!).Background!.Strength);
        Assert.Equal(0.8, Read(ThemeFileEditor.SetStrength("{ \"background\": { \"image\": \"a.jpg\" } }", 0.8)!).Background!.Strength);

        var made = Read(ThemeFileEditor.SetStrength("{ \"name\": \"A\" }", 0.45)!);
        Assert.Equal(0.45, made.Background!.Strength);
        Assert.Equal("A", made.Name);
    }

    [Fact]
    public void ThePosition_IsChangedInPlace_AddedToABlock_OrGivenABlock()
    {
        Assert.Equal("left", Read(ThemeFileEditor.SetPosition("{ \"background\": { \"image\": \"a.jpg\", \"position\": \"right\" } }", "left")!).Background!.Position);
        Assert.Equal("top", Read(ThemeFileEditor.SetPosition("{ \"background\": { \"image\": \"a.jpg\" } }", "top")!).Background!.Position);
        Assert.Equal("bottom", Read(ThemeFileEditor.SetPosition("{ \"name\": \"A\" }", "bottom")!).Background!.Position);
    }

    [Fact]
    public void ImageAndStrength_CanBothBeSetOnAFileWithNoBackground()
    {
        string text = ThemeFileEditor.SetImage("{ \"name\": \"A\" }", "A-1.png")!;
        text = ThemeFileEditor.SetStrength(text, 0.5)!;

        var theme = Read(text);
        Assert.Equal("A-1.png", theme.Background!.Image);
        Assert.Equal(0.5, theme.Background.Strength);
    }

    [Fact]
    public void ATextWithNoBrace_CannotBeEdited() =>
        Assert.Null(ThemeFileEditor.SetString("not json", "name", "x"));

    // ---- Color helpers --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("#238636", 0x23, 0x86, 0x36)]
    [InlineData("238636", 0x23, 0x86, 0x36)]
    [InlineData("  #abc ", 0xAA, 0xBB, 0xCC)]
    [InlineData("F0f", 0xFF, 0x00, 0xFF)]
    public void ATypedColor_IsReadWithOrWithoutTheHash_AndInShortForm(string typed, int r, int g, int b)
    {
        Assert.True(ThemeColor.TryParseTyped(typed, out var color));
        Assert.Equal(Color.FromRgb((byte)r, (byte)g, (byte)b), color);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#12")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("green")]
    [InlineData("#GG0000")]
    [InlineData(null)]
    public void ATypedColor_ThatIsNotSixOrThreeDigits_IsRefused(string? typed) =>
        Assert.False(ThemeColor.TryParseTyped(typed, out _));

    [Theory]
    [InlineData(0x00, 0x7A, 0xCC)]
    [InlineData(0x23, 0x86, 0x36)]
    [InlineData(0xFF, 0xFF, 0xFF)]
    [InlineData(0x00, 0x00, 0x00)]
    [InlineData(0x80, 0x80, 0x80)]
    [InlineData(0xE0, 0x6C, 0x75)]
    public void HsvRoundTrips_ToTheSameColor(int r, int g, int b)
    {
        var color = Color.FromRgb((byte)r, (byte)g, (byte)b);

        var (h, s, v) = ThemeColor.ToHsv(color);

        Assert.Equal(color, ThemeColor.FromHsv(h, s, v));
    }

    [Fact]
    public void HsvPrimaries_AreWhereTheyShouldBe()
    {
        Assert.Equal(Color.FromRgb(255, 0, 0), ThemeColor.FromHsv(0, 1, 1));
        Assert.Equal(Color.FromRgb(0, 255, 0), ThemeColor.FromHsv(120, 1, 1));
        Assert.Equal(Color.FromRgb(0, 0, 255), ThemeColor.FromHsv(240, 1, 1));
        Assert.Equal(Color.FromRgb(255, 255, 255), ThemeColor.FromHsv(77, 0, 1));
        Assert.Equal(Color.FromRgb(0, 0, 0), ThemeColor.FromHsv(300, 1, 0));
        Assert.Equal(Color.FromRgb(255, 0, 0), ThemeColor.FromHsv(360, 1, 1));
    }
}
