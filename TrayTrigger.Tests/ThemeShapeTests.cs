using System.Windows;
using TrayTrigger.Converters;
using TrayTrigger.Services.Theming;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>A theme's corner radii: what the file may say, and how the radii that follow a card are worked out from it.</summary>
public class ThemeShapeTests
{
    // Reading Shapes.xaml needs the WPF Application object to exist, as in the running app.
    public ThemeShapeTests() => WpfTestHost.Run(() => { });

    private static ThemeDefinition Parse(string json) => ThemeFileReader.Parse(json, "t", ThemeOrigin.User).Theme!;

    [Fact]
    public void AThemeWithNoShapeBlock_SetsNothing() => Assert.Null(Parse("{}").Shape);

    [Fact]
    public void ANumberSetsAllFourCorners_AndFourNumbersSetEachCorner()
    {
        var shape = Parse("""{ "shape": { "RadiusCard": 20, "RadiusControl": [0, 12, 0, 12] } }""").Shape!;
        Assert.Equal(new CornerRadius(20), shape["RadiusCard"]);
        Assert.Equal(new CornerRadius(0, 12, 0, 12), shape["RadiusControl"]);
    }

    [Fact]
    public void TheWordRadius_IsOptionalInAName() =>
        Assert.Equal(new CornerRadius(3), Parse("""{ "shape": { "Small": 3 } }""").Shape!["RadiusSmall"]);

    [Fact]
    public void ValuesAreKeptInRange_AndWhatIsNotARadiusIsSkippedWithAWarning()
    {
        var theme = Parse("""{ "shape": { "RadiusCard": 500, "RadiusSmall": -4, "RadiusLarge": 3, "RadiusPoster": "round", "RadiusControl": [1, 2] } }""");
        Assert.Equal(new CornerRadius(64), theme.Shape!["RadiusCard"]);
        Assert.Equal(new CornerRadius(0), theme.Shape["RadiusSmall"]);
        Assert.False(theme.Shape.ContainsKey("RadiusLarge"));
        Assert.False(theme.Shape.ContainsKey("RadiusPoster"));
        Assert.False(theme.Shape.ContainsKey("RadiusControl"));
        Assert.Equal(3, theme.Warnings!.Count);
    }

    [Fact]
    public void WithNoShape_EveryRadiusIsTheAppsOwn() => WpfTestHost.Run(() =>
    {
        var shapes = ThemeShape.Build(null);
        Assert.Equal(new CornerRadius(4), shapes["RadiusSmall"]);
        Assert.Equal(new CornerRadius(6), shapes["RadiusControl"]);
        Assert.Equal(new CornerRadius(8), shapes["RadiusCard"]);
        Assert.Equal(new CornerRadius(7), shapes["RadiusPoster"]);
        Assert.Equal(new CornerRadius(7, 7, 0, 0), shapes["RadiusInnerTop"]);
        Assert.Equal(new CornerRadius(0, 0, 7, 7), shapes["RadiusInnerBottom"]);
        Assert.Equal(new CornerRadius(999), shapes["RadiusLarge"]);
    });

    [Fact]
    public void ThePosterAndInnerEdgesFollowACardTheThemeSets() => WpfTestHost.Run(() =>
    {
        // A leaf card: square top-left and bottom-right.
        var shapes = ThemeShape.Build(Parse("""{ "shape": { "RadiusCard": [0, 20, 0, 20] } }""").Shape);
        Assert.Equal(new CornerRadius(0, 19, 0, 19), shapes["RadiusPoster"]);
        Assert.Equal(new CornerRadius(0, 19, 0, 0), shapes["RadiusInnerTop"]);
        Assert.Equal(new CornerRadius(0, 0, 0, 19), shapes["RadiusInnerBottom"]);
    });

    [Fact]
    public void ASquareCard_NeverGivesANegativeRadius() => WpfTestHost.Run(() =>
    {
        var shapes = ThemeShape.Build(Parse("""{ "shape": { "RadiusCard": 0 } }""").Shape);
        Assert.Equal(new CornerRadius(0), shapes["RadiusPoster"]);
        Assert.Equal(new CornerRadius(0), shapes["RadiusInnerTop"]);
    });

    [Fact]
    public void APosterTheThemeSets_IsNotReplacedByTheCards() => WpfTestHost.Run(() =>
    {
        var shapes = ThemeShape.Build(Parse("""{ "shape": { "RadiusCard": 20, "RadiusPoster": 2 } }""").Shape);
        Assert.Equal(new CornerRadius(2), shapes["RadiusPoster"]);
    });


    [Fact]
    public void TheClipOfABorderWithALine_IsItsInsideEdge() => WpfTestHost.Run(() =>
    {
        // A 170 x 255 card with a 1 px line and an 8 px radius clips to 168 x 253 with a 7 px radius.
        var clip = RoundedClipConverter.Build(170, 255, new CornerRadius(8), 1);
        Assert.Equal(new Rect(0, 0, 168, 253), clip.Bounds);
    });

    [Fact]
    public void ARadiusLargerThanTheShape_IsScaledDownNotRejected() => WpfTestHost.Run(() =>
    {
        var clip = RoundedClipConverter.Build(40, 20, new CornerRadius(999), 0);
        Assert.Equal(new Rect(0, 0, 40, 20), clip.Bounds);
    });
}

public class RoundedClipShapeTests
{
    public RoundedClipShapeTests() => WpfTestHost.Run(() => { });

    [Fact]
    public void TheClip_CutsEveryCornerByItsOwnRadius() => WpfTestHost.Run(() =>
    {
        var clip = TrayTrigger.Converters.RoundedClipConverter.Build(143, 120, new System.Windows.CornerRadius(51), 0);

        Assert.False(clip.FillContains(new System.Windows.Point(2, 2)));
        Assert.False(clip.FillContains(new System.Windows.Point(141, 2)));
        Assert.False(clip.FillContains(new System.Windows.Point(2, 118)));
        Assert.False(clip.FillContains(new System.Windows.Point(141, 118)));
        Assert.True(clip.FillContains(new System.Windows.Point(71, 60)));
        Assert.True(clip.FillContains(new System.Windows.Point(71, 2)));
        Assert.True(clip.FillContains(new System.Windows.Point(2, 60)));
    });

    [Fact]
    public void ASquareCorner_IsKept_WhenTheOthersAreRound() => WpfTestHost.Run(() =>
    {
        var clip = TrayTrigger.Converters.RoundedClipConverter.Build(143, 120, new System.Windows.CornerRadius(0, 51, 0, 51), 0);

        Assert.True(clip.FillContains(new System.Windows.Point(1, 1)));
        Assert.False(clip.FillContains(new System.Windows.Point(141, 2)));
        Assert.True(clip.FillContains(new System.Windows.Point(141, 118)));
        Assert.False(clip.FillContains(new System.Windows.Point(1, 118)));
    });
}

/// <summary>The corners of a game card around its poster, and the shades cut to the poster's shape.</summary>
public class PosterCardShapeTests
{
    public PosterCardShapeTests() => WpfTestHost.Run(() => { });

    [Fact]
    public void TheCardAroundAPoster_FollowsTheArtsScaledCorners_PlusItsOutline()
    {
        // The Poster Grid's card is 184 wide: two 96 px corners do not fit in the 182 px of art, so the art scales them to 91.
        var grid = TrayTrigger.Converters.PosterCardRadiusConverter.For(184, 276, new CornerRadius(96));
        Assert.Equal(92, grid.TopLeft, 3);
        Assert.Equal(92, grid.BottomRight, 3);

        // Extra Large is 240 wide: they fit, so the card is just the poster's 96 plus its line.
        var large = TrayTrigger.Converters.PosterCardRadiusConverter.For(240, 360, new CornerRadius(96));
        Assert.Equal(97, large.TopLeft, 3);
    }

    [Fact]
    public void ATwoPixelOutline_IsGoneRoundByTheCardsCorners()
    {
        // Focused or selected, the card's line is 2 px: the art is 4 px narrower, so its corners scale a little more, and the line goes round them.
        var card = TrayTrigger.Converters.PosterCardRadiusConverter.For(184, 276, new CornerRadius(96), 2);

        Assert.Equal(92, card.TopLeft, 3);
        var values = new object[] { 184.0, 276.0, new CornerRadius(96), new Thickness(2) };
        var viaBinding = (CornerRadius)new TrayTrigger.Converters.PosterCardRadiusConverter().Convert(values, typeof(CornerRadius), null!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(card, viaBinding);
    }

    [Fact]
    public void ASquareCornerOfThePoster_StaysSquareOnTheCard()
    {
        var card = TrayTrigger.Converters.PosterCardRadiusConverter.For(184, 276, new CornerRadius(96, 96, 0, 0));

        Assert.Equal(0, card.BottomLeft);
        Assert.Equal(0, card.BottomRight);
        Assert.True(card.TopLeft > 90);
    }

    [Fact]
    public void ADefaultCard_IsItsOwnRadiusAgain()
    {
        // With the app's own corners (card 8, poster 7) the card around the art is the 8 it always was.
        var card = TrayTrigger.Converters.PosterCardRadiusConverter.For(184, 276, new CornerRadius(7));

        Assert.Equal(8, card.TopLeft, 3);
    }

    [Fact]
    public void TheTopAndBottomShades_AreCutToThePostersShape() => WpfTestHost.Run(() =>
    {
        var converter = new TrayTrigger.Converters.PosterShadeClipConverter();
        object[] Values(CornerRadius radius, double shade) => [182.0, 274.0, radius, shade];

        // An arch: round at the top, square at the bottom. The top shade loses its corners, the bottom one keeps them.
        var arch = new CornerRadius(96, 96, 0, 0);
        var top = (System.Windows.Media.Geometry)converter.Convert(Values(arch, 48), typeof(System.Windows.Media.Geometry), null!, System.Globalization.CultureInfo.InvariantCulture);
        var bottom = (System.Windows.Media.Geometry)converter.Convert(Values(arch, 60), typeof(System.Windows.Media.Geometry), "bottom", System.Globalization.CultureInfo.InvariantCulture);
        Assert.False(top.FillContains(new System.Windows.Point(2, 2)));
        Assert.True(top.FillContains(new System.Windows.Point(91, 2)));
        Assert.True(bottom.FillContains(new System.Windows.Point(2, 58)));

        // All round: the bottom shade loses its corners too (its own top-left is 214 px down the poster).
        var round = new CornerRadius(96);
        var capsule = (System.Windows.Media.Geometry)converter.Convert(Values(round, 60), typeof(System.Windows.Media.Geometry), "bottom", System.Globalization.CultureInfo.InvariantCulture);
        Assert.False(capsule.FillContains(new System.Windows.Point(2, 58)));
        Assert.True(capsule.FillContains(new System.Windows.Point(91, 58)));
    });
}
