using System.IO;
using TrayTrigger.Converters;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// "Show details on hover": the poster views show only the artwork until a card is in use. The
/// look itself is XAML; what's tested here is the setting behind it and the two converters the
/// card's bindings rely on.
/// </summary>
public class PosterDetailsOnHoverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private StorageService NewStorage() => new(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"));

    private static SettingsViewModel NewSettings(AppSettings settings, StorageService storage) =>
        new(settings, storage, new StartupManager(), new TrayPromotionService(), new SteamScannerService());

    /// <summary>Off on a fresh install and for everyone upgrading, so nobody's library changes
    /// look until they ask for it.</summary>
    [Fact]
    public void FreshSettings_ShowDetailsAllTheTime()
    {
        Assert.False(new AppSettings().PosterDetailsOnHover);
    }

    [Fact]
    public void ToolbarToggle_FlipsTheSetting_AndItSurvivesARestart() => WpfTestHost.Run(() =>
    {
        var storage = NewStorage();
        var vm = NewSettings(new AppSettings(), storage);

        vm.TogglePosterDetailsOnHoverCommand.Execute(null);
        Assert.True(vm.PosterDetailsOnHover);
        Assert.True(storage.LoadSettings().PosterDetailsOnHover);

        vm.TogglePosterDetailsOnHoverCommand.Execute(null);
        Assert.False(vm.PosterDetailsOnHover);
        Assert.False(storage.LoadSettings().PosterDetailsOnHover);
    });

    /// <summary>The toggle is a plain Button, so its on/off state reaches Narrator through its name.</summary>
    [Fact]
    public void AccessibleName_SaysWhetherItIsOn() => WpfTestHost.Run(() =>
    {
        var vm = NewSettings(new AppSettings(), NewStorage());
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.EndsWith(", off", vm.PosterDetailsOnHoverAccessibleName);
        vm.PosterDetailsOnHover = true;
        Assert.EndsWith(", on", vm.PosterDetailsOnHoverAccessibleName);
        Assert.Contains(nameof(SettingsViewModel.PosterDetailsOnHoverAccessibleName), changed);
    });

    /// <summary>The toggle is only enabled where it does something: the two poster views.</summary>
    [Theory]
    [InlineData(SettingsViewModel.ViewModePosterGrid, true)]
    [InlineData(SettingsViewModel.ViewModeExtraLarge, true)]
    [InlineData(SettingsViewModel.ViewModeCompactIcons, false)]
    [InlineData(SettingsViewModel.ViewModeDetailsList, false)]
    public void IsPosterView_FollowsTheViewMode(string mode, bool expected) => WpfTestHost.Run(() =>
    {
        var vm = NewSettings(new AppSettings(), NewStorage());
        // Start from a view on the other side, so every case is a real change and raises it.
        vm.LibraryViewMode = expected ? SettingsViewModel.ViewModeDetailsList : SettingsViewModel.ViewModePosterGrid;
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.LibraryViewMode = mode;

        Assert.Equal(expected, vm.IsPosterView);
        Assert.Contains(nameof(SettingsViewModel.IsPosterView), changed);
    });

    /// <summary>The card's "in use" state - pointed at, focused, or its menu open - is any of
    /// three; the triggers test the one answer.</summary>
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(true, true, true, true)]
    public void AnyTrue_IsTrueWhenAnyValueIs(bool a, bool b, bool c, bool expected)
    {
        var result = new AnyTrueConverter().Convert([a, b, c], typeof(object), null, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    /// <summary>The details read the animated opacity while Animation effects are on and the
    /// instant one while they're off; anything but a true flag counts as off.</summary>
    [Theory]
    [InlineData(true, 0.4)]
    [InlineData(false, 1.0)]
    public void PickByFlag_ReadsTheAnimatedValueOnlyWhileTheFlagIsOn(bool flag, double expected)
    {
        var result = new PickByFlagConverter().Convert([flag, 0.4, 1.0], typeof(double), null, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void PickByFlag_TreatsAnUnresolvedFlagAsOff()
    {
        var result = new PickByFlagConverter().Convert([System.Windows.DependencyProperty.UnsetValue, 0.4, 1.0], typeof(double), null, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(1.0, result);
    }

    /// <summary>Too few values: nothing to pick from, so the binding falls back to the default.</summary>
    [Fact]
    public void PickByFlag_WithTooFewValues_IsUnset()
    {
        var result = new PickByFlagConverter().Convert([true, 0.4], typeof(double), null, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Same(System.Windows.DependencyProperty.UnsetValue, result);
    }

    /// <summary>A binding that hasn't resolved yet hands the converter UnsetValue, which isn't "in use".</summary>
    [Fact]
    public void AnyTrue_IgnoresValuesThatArentBooleans()
    {
        var result = new AnyTrueConverter().Convert([System.Windows.DependencyProperty.UnsetValue, null!, "True"], typeof(object), null, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(false, result);
    }
}
