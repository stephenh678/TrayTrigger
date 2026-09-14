using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using TrayTrigger.Converters;
using Xunit;

namespace TrayTrigger.Tests;

public class TabStateConvertersTests
{
    private static readonly string[] ThemeKeys = { "BrushAccent", "BrushAccentHover", "BrushSurfaceHover", "BrushTextSecondary" };

    /// <summary>The converters look brushes up through Application.Current, so the theme keys are
    /// registered on the shared test Application (a distinct brush per key) and the body runs on
    /// its thread.</summary>
    private static void WithThemeBrushes(Action<ResourceDictionary> body) => WpfTestHost.Run(() =>
    {
        var resources = Application.Current!.Resources;
        foreach (string key in ThemeKeys)
        {
            if (!resources.Contains(key)) resources[key] = new SolidColorBrush(Colors.Red);
        }
        body(resources);
    });

    private static object Convert(IMultiValueConverter converter, params object[] values) =>
        converter.Convert(values, typeof(Brush), null, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(true, true, "BrushAccentHover")]
    [InlineData(true, false, "BrushAccent")]
    [InlineData(false, true, "BrushSurfaceHover")]
    public void Background_PicksTheThemeBrushForTheState(bool active, bool hovered, string expectedKey) => WithThemeBrushes(resources =>
        Assert.Same(resources[expectedKey], Convert(new TabBackgroundConverter(), active, hovered)));

    [Fact]
    public void Background_IdleTabIsTransparent() => WithThemeBrushes(_ =>
        Assert.Same(Brushes.Transparent, Convert(new TabBackgroundConverter(), false, false)));

    [Fact]
    public void Foreground_ActiveOrHoveredIsWhite_IdleIsSecondaryText() => WithThemeBrushes(resources =>
    {
        var converter = new TabForegroundConverter();
        Assert.Same(Brushes.White, Convert(converter, true, false));
        Assert.Same(Brushes.White, Convert(converter, false, true));
        Assert.Same(Brushes.White, Convert(converter, true, true));
        Assert.Same(resources["BrushTextSecondary"], Convert(converter, false, false));
    });

    // The style's default setter gives Tag the string "False"; a string never counts as active, even "True".
    [Theory]
    [InlineData("False")]
    [InlineData("True")]
    public void StringTag_CountsAsInactive(string tag) => WithThemeBrushes(resources =>
    {
        Assert.Same(Brushes.Transparent, Convert(new TabBackgroundConverter(), tag, false));
        Assert.Same(resources["BrushSurfaceHover"], Convert(new TabBackgroundConverter(), tag, true));
    });

    [Fact]
    public void UnsetOrMissingValues_FallBackWithoutThrowing() => WithThemeBrushes(resources =>
    {
        Assert.Same(Brushes.Transparent, Convert(new TabBackgroundConverter(), DependencyProperty.UnsetValue, DependencyProperty.UnsetValue));
        Assert.Same(Brushes.Transparent, Convert(new TabBackgroundConverter()));
        Assert.Same(resources["BrushTextSecondary"], Convert(new TabForegroundConverter()));
    });

    [Fact]
    public void ConvertBack_IsNotSupported()
    {
        Assert.Throws<NotSupportedException>(() => new TabBackgroundConverter().ConvertBack(Brushes.White, new[] { typeof(bool) }, null, CultureInfo.InvariantCulture));
        Assert.Throws<NotSupportedException>(() => new TabForegroundConverter().ConvertBack(Brushes.White, new[] { typeof(bool) }, null, CultureInfo.InvariantCulture));
    }
}
