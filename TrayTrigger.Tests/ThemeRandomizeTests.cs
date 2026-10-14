using System.IO;
using System.Linq;
using System.Windows;
using TrayTrigger.Services.Theming;
using TrayTrigger.ViewModels;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>The theme editor's Randomize button: a starting point that stays inside the editor's gates, and Undo.</summary>
[Collection("ThemeService")]
public class ThemeRandomizeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tt-generate-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _themesBefore = ThemeCatalog.UserThemesDirectory;

    public ThemeRandomizeTests()
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

    private static ThemeEditorViewModel Open(string id, int seed)
    {
        var vm = new ThemeEditorViewModel(ThemeCatalog.BuiltIn(id)!, ThemeService.Instance);
        vm.Random = new Random(seed);
        return vm;
    }

    [Fact]
    public void EveryRoll_ReadsWell_AndKeepsTheCornersOnTheSliders() => WpfTestHost.Run(() =>
    {
        foreach (string id in new[] { "Dark", "Light" })
            for (int seed = 0; seed < 60; seed++)
            {
                var vm = Open(id, seed);

                vm.RandomizeCommand.Execute(null);

                Assert.True(vm.ContrastOk, $"{id} seed {seed}: {vm.ContrastLine}");
                Assert.False(vm.HasValidationMessage, $"{id} seed {seed} found nothing");
                Assert.True(vm.Corners.Roundness is >= 0 and <= ThemeCorners.MaxStep);
                Assert.All(vm.Corners.Rows, row => Assert.True(row.Step is >= 0 and <= ThemeCorners.MaxStep, $"{id} seed {seed}: {row.Label} at {row.Step}"));
                // Whatever comes up is a size the theme file allows.
                foreach (var (key, radius) in vm.Corners.Values)
                    Assert.True(radius.TopLeft <= ThemeShape.Settable[key] && radius.TopRight <= ThemeShape.Settable[key]
                        && radius.BottomRight <= ThemeShape.Settable[key] && radius.BottomLeft <= ThemeShape.Settable[key], $"{id} seed {seed}: {key}");
            }
    });

    [Fact]
    public void ARoll_ChangesTheColorsAndCorners_ButNotTheNameOrTheStart() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark", 7);
        string name = vm.Name;
        var before = vm.Rows.Select(r => r.Color).ToArray();
        Assert.False(vm.HasUnsavedChanges);
        Assert.False(vm.CanUndoRandomize);

        vm.RandomizeCommand.Execute(null);

        Assert.NotEqual(before, vm.Rows.Select(r => r.Color).ToArray());
        Assert.True(vm.HasUnsavedChanges);
        Assert.True(vm.CanUndoRandomize);
        Assert.Equal(name, vm.Name);
        Assert.True(vm.IsDark);
        Assert.Equal(vm.Palette["Surface"], vm.Surface.Color);
    });

    [Fact]
    public void TheKindsOfCorner_OftenDiffer_AndTheWholeRangeComesUp() => WpfTestHost.Run(() =>
    {
        var rolls = Enumerable.Range(0, 80).Select(seed =>
        {
            var vm = Open("Dark", seed);
            vm.RandomizeCommand.Execute(null);
            return vm.Corners;
        }).ToList();

        // Most rolls give at least one kind of corner a shape or size of its own.
        Assert.True(rolls.Count(c => c.HasOverrides) >= 60);
        // Different kinds of corner end up with different radii in many rolls.
        Assert.True(rolls.Count(c => c.Values.Values.Distinct().Count() >= 3) >= 40);
        // Extreme sizes, a pill and a square corner all come up somewhere.
        Assert.Contains(rolls, c => c.IsExtreme || c.Rows.Any(r => r.IsExtreme));
        Assert.Contains(rolls, c => c.Shape.Id == "pill" || c.Rows.Any(r => r.SelectedShape.Id == "pill"));
        Assert.Contains(rolls, c => c.Values.Values.Any(v => v.TopLeft == 0));
    });

    [Fact]
    public void DifferentSeeds_GiveDifferentAccents() => WpfTestHost.Run(() =>
    {
        var accents = Enumerable.Range(0, 12).Select(seed =>
        {
            var vm = Open("Dark", seed);
            vm.RandomizeCommand.Execute(null);
            return vm.Accent.Color;
        }).Distinct().Count();

        Assert.True(accents >= 10);
    });

    [Fact]
    public void ARollCanBeChangedByHandAfterwards() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark", 3);
        vm.RandomizeCommand.Execute(null);

        vm.Accent.Hex = "#238636";
        vm.Accent.CommitHex();
        vm.Corners.Shape = ThemeCorners.Shapes.First(s => s.Id == "leaf");
        vm.Corners.Roundness = 6;

        Assert.Equal(System.Windows.Media.Color.FromRgb(0x23, 0x86, 0x36), vm.Accent.Color);
        Assert.Equal("leaf", vm.Corners.Shape.Id);
        Assert.Equal(6, vm.Corners.Roundness);
    });

    [Fact]
    public void Undo_PutsBackTheColorsAndCornersFromBeforeTheLastRoll() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark", 11);
        vm.Corners.Shape = ThemeCorners.Shapes.First(s => s.Id == "folder-tab");
        vm.Corners.Roundness = 5;
        vm.Accent.Hex = "#112233";
        vm.Accent.CommitHex();
        var colors = vm.Rows.Select(r => r.Color).ToArray();
        var corners = vm.Corners.Values.OrderBy(v => v.Key).Select(v => v.Value).ToArray();

        vm.RandomizeCommand.Execute(null);
        vm.UndoRandomizeCommand.Execute(null);

        Assert.Equal(colors, vm.Rows.Select(r => r.Color).ToArray());
        Assert.Equal(corners, vm.Corners.Values.OrderBy(v => v.Key).Select(v => v.Value).ToArray());
        Assert.Equal("folder-tab", vm.Corners.Shape.Id);
        Assert.Equal(5, vm.Corners.Roundness);
        Assert.False(vm.CanUndoRandomize);
    });

    [Fact]
    public void RollingTwice_UndoesOnlyTheLastRoll() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark", 21);
        vm.RandomizeCommand.Execute(null);
        var first = vm.Rows.Select(r => r.Color).ToArray();
        var firstShape = vm.Corners.Shape.Id;

        vm.RandomizeCommand.Execute(null);
        vm.UndoRandomizeCommand.Execute(null);

        Assert.Equal(first, vm.Rows.Select(r => r.Color).ToArray());
        Assert.Equal(firstShape, vm.Corners.Shape.Id);
    });

    [Fact]
    public void WhenNoColorsPass_NothingChanges_AndTheEditorSaysSo() => WpfTestHost.Run(() =>
    {
        // A fine-tuning color the file sets by hand (muted text almost as dark as every dark background) fails on every roll.
        Directory.CreateDirectory(ThemeCatalog.UserThemesDirectory);
        string path = Path.Combine(ThemeCatalog.UserThemesDirectory, "Pinned.theme.json");
        File.WriteAllText(path, "{ \"base\": \"dark\", \"colors\": { \"TextMuted\": \"#080808\" } }");
        var vm = new ThemeEditorViewModel(ThemeFileReader.ReadFile(path, ThemeOrigin.User).Theme!, ThemeService.Instance) { Random = new Random(1) };
        var before = vm.Rows.Select(r => r.Color).ToArray();
        var shape = vm.Corners.Shape.Id;

        vm.RandomizeCommand.Execute(null);

        Assert.True(vm.HasValidationMessage);
        Assert.Equal(before, vm.Rows.Select(r => r.Color).ToArray());
        Assert.Equal(shape, vm.Corners.Shape.Id);
        Assert.False(vm.CanUndoRandomize);
        Assert.False(vm.HasUnsavedChanges);
    });

    [Fact]
    public void TheGenerator_GivesUpAfterItsTries_WhenNothingPasses()
    {
        int tried = 0;

        var found = ThemeRandomizer.Roll(new Random(5), true, _ => { tried++; return false; });

        Assert.Null(found);
        Assert.Equal(ThemeRandomizer.MaxTries, tried);
    }
}
