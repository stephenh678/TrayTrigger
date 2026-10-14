using System.IO;
using System.Linq;
using System.Windows;
using TrayTrigger.Services.Theming;
using TrayTrigger.ViewModels;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>The Corners card of the Edit Theme window: what a shape and a roundness work out to, what a file's numbers show as, and what saving writes.</summary>
[Collection("ThemeService")]
public class ThemeCornersTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tt-corners-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _themesBefore = ThemeCatalog.UserThemesDirectory;
    private string Themes => ThemeCatalog.UserThemesDirectory;

    public ThemeCornersTests()
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

    private static CornerShape Shape(string id) => ThemeCorners.Shapes.First(s => s.Id == id);
    private static ThemeEditorViewModel Open(string id) => new(ThemeCatalog.BuiltIn(id)!, ThemeService.Instance);

    private ThemeEditorViewModel OpenFile(string name, string text, out string path)
    {
        Directory.CreateDirectory(Themes);
        path = Path.Combine(Themes, name + ".theme.json");
        File.WriteAllText(path, text);
        return new ThemeEditorViewModel(ThemeFileReader.ReadFile(path, ThemeOrigin.User).Theme!, ThemeService.Instance);
    }

    private static bool Save(ThemeEditorViewModel vm)
    {
        bool? closedWith = null;
        vm.RequestClose += ok => closedWith = ok;
        vm.SaveCommand.Execute(null);
        return closedWith == true;
    }

    private static ThemeDefinition Read(string path) => ThemeFileReader.ReadFile(path, ThemeOrigin.User).Theme!;

    // ---- Shapes -----------------------------------------------------------------------------------------------

    [Fact]
    public void RoundedAtTheMiddleOfTheSlider_IsTheAppsOwnLook()
    {
        Assert.Equal(new CornerRadius(4), Shape("rounded").At(4 * ThemeCorners.Targets[0].PxPerStep, ThemeCorners.Targets[0].Max));
        Assert.Equal(new CornerRadius(6), Shape("rounded").At(4 * ThemeCorners.Targets[1].PxPerStep, ThemeCorners.Targets[1].Max));
        Assert.Equal(new CornerRadius(8), Shape("rounded").At(4 * ThemeCorners.Targets[2].PxPerStep, ThemeCorners.Targets[2].Max));
    }

    [Fact]
    public void EveryShapeAtEverySize_IsRecognisedAgainAsNumbersThatDrawTheSame()
    {
        foreach (var target in ThemeCorners.Targets)
            foreach (var shape in ThemeCorners.Shapes)
                for (double step = 0; step <= ThemeCorners.MaxStep; step++)
                {
                    var numbers = shape.At(target.Px(step), target.Max, target.FullPx);
                    var found = ThemeCorners.Recognise(target, numbers);
                    Assert.True(found != null, $"{shape.Name} at {step} for {target.Label} was not recognised");
                    Assert.True(ThemeCorners.Same(found!.Value.Shape.At(target.Px(found.Value.Step), target.Max, target.FullPx), numbers));
                }
    }

    [Fact]
    public void NumbersNoShapeDraws_AreNotRecognised() =>
        Assert.Null(ThemeCorners.Recognise(ThemeCorners.Targets[2], new CornerRadius(3, 11, 5, 2)));

    [Fact]
    public void APill_IsAsRoundAsTheKindOfCornerAllows()
    {
        var control = ThemeCorners.Targets[1];
        var card = ThemeCorners.Targets[2];
        Assert.Equal(new CornerRadius(16), Shape("pill").At(0, control.Max, control.FullPx));
        Assert.Equal(new CornerRadius(64), Shape("pill").At(0, card.Max, card.FullPx));
        Assert.Equal(new CornerRadius(16, 0, 0, 16), Shape("left-cap").At(0, control.Max, control.FullPx));
        Assert.Equal(new CornerRadius(999), Shape("pill").At(0, 999));
    }

    // ---- The card ----------------------------------------------------------------------------------------------

    [Fact]
    public void ATheme_WithNoCorners_OpensAtTheAppsOwn_AndHasNothingToSave() => WpfTestHost.Run(() =>
    {
        var corners = Open("Dark").Corners;

        Assert.Equal("rounded", corners.Shape.Id);
        Assert.Equal(4, corners.Roundness);
        Assert.False(corners.DiffersFromDefault);
        Assert.False(corners.HasOverrides);
        Assert.True(corners.ShowCustomize);
        var (set, clear) = corners.ForSave();
        Assert.Empty(set);
        Assert.Empty(clear);
        Assert.Equal(new CornerRadius(7), corners.Values["RadiusPoster"]);
    });

    [Fact]
    public void ChoosingAShapeAndSize_SetsEveryKindOfCorner_AndAPosterFollowsTheCardLessOne() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");
        int repaints = 0;
        vm.ShapeChanged += (_, _) => repaints++;

        vm.Corners.Shape = Shape("leaf");
        vm.Corners.Roundness = 6;

        var values = vm.Corners.Values;
        Assert.Equal(new CornerRadius(0, 6, 0, 6), values["RadiusSmall"]);
        Assert.Equal(new CornerRadius(0, 9, 0, 9), values["RadiusControl"]);
        Assert.Equal(new CornerRadius(0, 12, 0, 12), values["RadiusCard"]);
        Assert.Equal(new CornerRadius(0, 11, 0, 11), values["RadiusPoster"]);
        Assert.True(vm.Corners.DiffersFromDefault);
        Assert.True(vm.HasUnsavedChanges);
        Assert.Equal(2, repaints);

        var (set, clear) = vm.Corners.ForSave();
        Assert.Equal(new[] { "RadiusCard", "RadiusControl", "RadiusSmall" }, set.Keys.OrderBy(k => k));
        Assert.Empty(clear);
    });

    [Fact]
    public void ARowMovedByHand_KeepsItsSize_WhenTheRoundnessMoves() => WpfTestHost.Run(() =>
    {
        var corners = Open("Dark").Corners;

        corners.Controls.Step = 8;
        corners.Roundness = 2;

        Assert.Equal(new CornerRadius(12), corners.Values["RadiusControl"]);
        Assert.Equal(new CornerRadius(2), corners.Values["RadiusSmall"]);
        Assert.True(corners.HasOverrides);
    });

    [Fact]
    public void ARowWithItsOwnShape_KeepsIt_WhenTheShapeAboveChanges() => WpfTestHost.Run(() =>
    {
        var corners = Open("Dark").Corners;

        corners.Tags.SelectedShape = corners.Tags.Choices.First(c => c.Id == "pill");
        corners.Shape = Shape("leaf");

        Assert.Equal(new CornerRadius(16), corners.Values["RadiusSmall"]);
        Assert.Equal(new CornerRadius(0, 8, 0, 8), corners.Values["RadiusCard"]);
    });

    [Fact]
    public void APosterSetByHand_IsKept_AndWritten() => WpfTestHost.Run(() =>
    {
        var corners = Open("Dark").Corners;
        corners.Shape = Shape("leaf");

        corners.Posters.SelectedShape = corners.Posters.Choices.First(c => c.Id == "rounded");
        corners.Posters.Step = 0;

        Assert.Equal(new CornerRadius(0), corners.Values["RadiusPoster"]);
        Assert.Equal(new CornerRadius(0), corners.ForSave().Set["RadiusPoster"]);

        // Changing the card does not move it.
        corners.Roundness = 8;
        Assert.Equal(new CornerRadius(0), corners.Values["RadiusPoster"]);
    });

    [Fact]
    public void APill_TakesTheSizeSliderAway() => WpfTestHost.Run(() =>
    {
        var corners = Open("Dark").Corners;

        corners.Shape = Shape("pill");

        Assert.False(corners.SizeEnabled);
        Assert.Equal(new CornerRadius(16), corners.Values["RadiusControl"]);
        Assert.Equal(new CornerRadius(64), corners.Values["RadiusCard"]);
        Assert.False(corners.Controls.SizeEnabled);
    });

    [Fact]
    public void Reset_PutsEverythingBackToTheApps() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");
        vm.Corners.Shape = Shape("ramp");
        vm.Corners.Roundness = 7;
        vm.Corners.Posters.Step = 1;

        vm.Corners.ResetCommand.Execute(null);

        Assert.False(vm.Corners.DiffersFromDefault);
        Assert.False(vm.Corners.HasOverrides);
        Assert.Equal("rounded", vm.Corners.Shape.Id);
        Assert.Equal(4, vm.Corners.Roundness);
        Assert.False(vm.HasUnsavedChanges);
    });

    // ---- Opening a file ----------------------------------------------------------------------------------------

    [Fact]
    public void ACardTheFileSetsWithAShape_OpensAsThatShape_AndTheRestShowAsTheirOwn() => WpfTestHost.Run(() =>
    {
        var vm = OpenFile("Leafy", "{ \"shape\": { \"RadiusCard\": [0, 16, 0, 16] } }", out _);

        Assert.Equal("leaf", vm.Corners.Shape.Id);
        Assert.Equal(8, vm.Corners.Roundness);
        // Tags and controls are the app's own, which is not a leaf at 8, so they show as their own choice.
        Assert.True(vm.Corners.HasOverrides);
        Assert.True(vm.Corners.ShowCustomize);
        Assert.Equal(new CornerRadius(6), vm.Corners.Values["RadiusControl"]);
        // Nothing was changed, so nothing is written.
        var (set, clear) = vm.Corners.ForSave();
        Assert.Empty(set);
        Assert.Empty(clear);
        Assert.False(vm.HasUnsavedChanges);
    });

    [Fact]
    public void NumbersNoShapeDraws_AreKeptExactly_UntilThatRowIsChanged() => WpfTestHost.Run(() =>
    {
        var vm = OpenFile("Odd", "{ \"shape\": { \"RadiusCard\": [3, 11, 5, 2] } }", out _);

        Assert.True(vm.Corners.Cards.SelectedShape.IsCustom);
        Assert.Equal(new CornerRadius(3, 11, 5, 2), vm.Corners.Values["RadiusCard"]);
        Assert.False(vm.Corners.Cards.SizeEnabled);
        Assert.Empty(vm.Corners.ForSave().Set);

        vm.Corners.Cards.SelectedShape = vm.Corners.Cards.Choices.First(c => c.Id == "pill");
        Assert.Equal(new CornerRadius(64), vm.Corners.Values["RadiusCard"]);
    });

    [Fact]
    public void APosterTheFileSets_ThatIsNotTheCardLessOne_OpensAsSetByHand() => WpfTestHost.Run(() =>
    {
        var vm = OpenFile("Flat", "{ \"shape\": { \"RadiusPoster\": 0 } }", out _);

        Assert.True(vm.Corners.Posters.IsOverridden);
        Assert.Equal(new CornerRadius(0), vm.Corners.Values["RadiusPoster"]);
        Assert.Empty(vm.Corners.ForSave().Set);
    });

    // ---- Saving ------------------------------------------------------------------------------------------------

    [Fact]
    public void ANewTheme_IsSavedWithOnlyTheNumbersThatDifferFromTheApps() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");
        vm.Corners.Shape = Shape("leaf");
        vm.Corners.Roundness = 6;

        Assert.True(Save(vm));

        string text = File.ReadAllText(vm.SavedPath!);
        Assert.Contains("\"shape\"", text);
        Assert.Contains("\"RadiusCard\": [0, 12, 0, 12]", text);
        Assert.DoesNotContain("RadiusPoster\":", text);
        var read = Read(vm.SavedPath!);
        Assert.Equal(new CornerRadius(0, 12, 0, 12), read.Shape!["RadiusCard"]);
        Assert.Equal(new CornerRadius(0, 9, 0, 9), read.Shape["RadiusControl"]);
    });

    [Fact]
    public void ANewTheme_WithNoCornersChanged_HasNoShapeBlock() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");
        vm.Name = "Plain";

        Assert.True(Save(vm));

        Assert.DoesNotContain("\"shape\"", File.ReadAllText(vm.SavedPath!));
    });

    [Fact]
    public void AFileWithNoShape_GetsABlock_AndKeepsEverythingElse() => WpfTestHost.Run(() =>
    {
        const string text = "{\n  // my theme\n  \"name\": \"Mine\",\n  \"base\": \"dark\",\n  \"colors\": {\n    \"Accent\": \"#007ACC\",\n  }\n}\n";
        var vm = OpenFile("Mine", text, out string path);

        vm.Corners.Shape = Shape("folder-tab");
        Assert.True(Save(vm));

        string saved = File.ReadAllText(path);
        Assert.Contains("// my theme", saved);
        Assert.Contains("\"Accent\": \"#007ACC\"", saved);
        Assert.Equal(new CornerRadius(8, 8, 0, 0), Read(path).Shape!["RadiusCard"]);
    });

    [Fact]
    public void PuttingACardBack_TakesItsLineOut_AndLeavesTheOtherLines() => WpfTestHost.Run(() =>
    {
        const string text = "{\n  \"shape\": {\n    // the corners\n    \"RadiusCard\": [0, 16, 0, 16],\n    \"RadiusControl\": 12,\n  }\n}\n";
        var vm = OpenFile("Back", text, out string path);

        vm.Corners.Cards.SelectedShape = vm.Corners.Cards.Choices.First(c => c.Id == "rounded");
        vm.Corners.Cards.Step = 4;
        // The card is now the app's own: the shape is Rounded, at the middle.
        Assert.Equal(new CornerRadius(8), vm.Corners.Values["RadiusCard"]);
        Assert.True(Save(vm));

        string saved = File.ReadAllText(path);
        Assert.DoesNotContain("RadiusCard", saved);
        Assert.Contains("// the corners", saved);
        var read = Read(path);
        Assert.False(read.Shape!.ContainsKey("RadiusCard"));
        Assert.Equal(new CornerRadius(12), read.Shape["RadiusControl"]);
    });

    [Fact]
    public void SavingAFileWithoutChangingItsCorners_DoesNotTouchThem() => WpfTestHost.Run(() =>
    {
        const string text = "{\n  \"shape\": { \"RadiusCard\": [3, 11, 5, 2], \"RadiusPoster\": 1 }\n}\n";
        var vm = OpenFile("Hand", text, out string path);
        vm.Name = "Hand Two";

        Assert.True(Save(vm));

        string saved = File.ReadAllText(path);
        Assert.Contains("\"shape\": { \"RadiusCard\": [3, 11, 5, 2], \"RadiusPoster\": 1 }", saved);
    });

    // ---- The states link ---------------------------------------------------------------------------------------

    [Fact]
    public void ThePreviewPanel_IsClosedUntilTheWindowOpensIt_AndTheTabTogglesIt() => WpfTestHost.Run(() =>
    {
        var vm = Open("Dark");

        Assert.False(vm.IsPreviewOpen);
        Assert.Equal("Live preview  >", vm.PreviewTabText);

        vm.TogglePreviewCommand.Execute(null);

        Assert.True(vm.IsPreviewOpen);
        Assert.Equal("Live preview  <", vm.PreviewTabText);
        Assert.False(vm.HasUnsavedChanges);
    });

    // ---- The steeper end of the slider -------------------------------------------------------------------------

    [Fact]
    public void TheSlider_IsEvenUpToEight_AndSteeperAfter()
    {
        var card = ThemeCorners.Targets[2];
        var poster = ThemeCorners.Targets[3];

        Assert.Equal(8, card.Px(4));
        Assert.Equal(16, card.Px(8));
        Assert.Equal(64, card.Px(16));
        Assert.Equal(96, poster.Px(16));
        Assert.Equal(36, ThemeCorners.Targets[1].Px(16));
        Assert.Equal(16, ThemeCorners.Targets[0].Px(16));
    }

    [Fact]
    public void PastEight_TheRoundnessIsExtreme_AndAPosterCanBeVeryRound() => WpfTestHost.Run(() =>
    {
        var corners = Open("Dark").Corners;
        Assert.False(corners.IsExtreme);

        corners.Roundness = 16;

        Assert.True(corners.IsExtreme);
        Assert.Equal(new CornerRadius(64), corners.Values["RadiusCard"]);
        Assert.Equal(new CornerRadius(63), corners.Values["RadiusPoster"]);

        corners.Roundness = 8;
        corners.Posters.Step = 16;
        Assert.True(corners.Posters.IsExtreme);
        Assert.Equal(new CornerRadius(96), corners.Values["RadiusPoster"]);
        Assert.Equal(new CornerRadius(96), corners.ForSave().Set["RadiusPoster"]);
    });

    [Fact]
    public void ARowPutBack_FollowsTheChoiceAboveAgain() => WpfTestHost.Run(() =>
    {
        var corners = Open("Dark").Corners;
        corners.Controls.Step = 8;
        Assert.True(corners.Controls.HasOwnSetting);

        corners.Controls.ResetCommand.Execute(null);

        Assert.False(corners.Controls.HasOwnSetting);
        Assert.Equal(new CornerRadius(6), corners.Values["RadiusControl"]);
    });
}

/// <summary>Setting and taking out a radius in a theme file as text.</summary>
public class ThemeFileShapeEditTests
{
    private static ThemeDefinition Read(string text) => ThemeFileReader.Parse(text, "t", ThemeOrigin.User).Theme!;

    [Fact]
    public void ARadius_IsWrittenAsOneNumberWhenTheCornersAreAlike_AndAsFourWhenNot()
    {
        string one = ThemeFileEditor.SetShape("{\n}\n", "RadiusCard", new CornerRadius(20))!;
        string four = ThemeFileEditor.SetShape("{\n}\n", "RadiusCard", new CornerRadius(0, 20, 0, 20.5))!;

        Assert.Contains("\"RadiusCard\": 20,", one);
        Assert.Contains("\"RadiusCard\": [0, 20, 0, 20.5],", four);
        Assert.Equal(new CornerRadius(20), Read(one).Shape!["RadiusCard"]);
        Assert.Equal(new CornerRadius(0, 20, 0, 20.5), Read(four).Shape!["RadiusCard"]);
    }

    [Fact]
    public void ALineThatSetsARadius_HasItsValueChanged_AndNothingElse()
    {
        const string text = "{\n  \"shape\": {\n    \"RadiusCard\": 20,  // cards\n    \"RadiusControl\": [0, 12, 0, 12],\n  }\n}\n";

        string result = ThemeFileEditor.SetShape(text, "RadiusCard", new CornerRadius(4))!;

        Assert.Equal(text.Replace("20,  // cards", "4,  // cards"), result);
    }

    [Fact]
    public void ARadiusWrittenWithoutTheWordRadius_IsTheLineThatChanges()
    {
        const string text = "{\n  \"shape\": {\n    \"Card\": 20,\n  }\n}\n";

        string result = ThemeFileEditor.SetShape(text, "RadiusCard", new CornerRadius(6))!;

        Assert.Equal(text.Replace("20", "6"), result);
    }

    [Fact]
    public void ACommentedRadius_IsLeftAlone_AndALineIsAddedInTheBlock()
    {
        const string text = "{\n  \"shape\": {\n    // \"RadiusCard\": 20,\n  }\n}\n";

        string result = ThemeFileEditor.SetShape(text, "RadiusCard", new CornerRadius(6))!;

        Assert.Contains("// \"RadiusCard\": 20,", result);
        Assert.Equal(new CornerRadius(6), Read(result).Shape!["RadiusCard"]);
    }

    [Fact]
    public void ARadiusNamedInTheColorsBlock_IsNotMistakenForOneInTheShapeBlock()
    {
        const string text = "{\n  \"colors\": {\n    \"Accent\": \"#007ACC\",\n  },\n  \"shape\": {\n    \"RadiusCard\": 20,\n  }\n}\n";

        string result = ThemeFileEditor.SetShape(text, "RadiusSmall", new CornerRadius(2))!;

        Assert.Equal(new CornerRadius(2), Read(result).Shape!["RadiusSmall"]);
        Assert.Equal(new CornerRadius(20), Read(result).Shape!["RadiusCard"]);
    }

    [Fact]
    public void TakingARadiusOut_RemovesItsLineAndItsComment_AndKeepsTheRest()
    {
        const string text = "{\n  \"shape\": {\n    // keep this\n    \"RadiusCard\": 20,  // cards\n    \"RadiusControl\": 12,\n  }\n}\n";

        string result = ThemeFileEditor.RemoveShape(text, "RadiusCard")!;

        Assert.Equal("{\n  \"shape\": {\n    // keep this\n    \"RadiusControl\": 12,\n  }\n}\n", result);
    }

    [Fact]
    public void TakingOutARadiusTheFileDoesNotSet_ChangesNothing()
    {
        const string text = "{\n  \"shape\": {\n    \"RadiusControl\": 12,\n  }\n}\n";

        Assert.Equal(text, ThemeFileEditor.RemoveShape(text, "RadiusCard"));
        Assert.Equal("{\n}\n", ThemeFileEditor.RemoveShape("{\n}\n", "RadiusCard"));
    }

    [Fact]
    public void ARadiusOnOneLine_IsTakenOutWithItsComma()
    {
        const string text = "{ \"shape\": { \"RadiusCard\": [0, 16, 0, 16], \"RadiusControl\": 12 } }";

        string result = ThemeFileEditor.RemoveShape(text, "RadiusCard")!;

        var shape = Read(result).Shape!;
        Assert.False(shape.ContainsKey("RadiusCard"));
        Assert.Equal(new CornerRadius(12), shape["RadiusControl"]);
    }

    [Fact]
    public void ANewThemeFile_WritesTheShapeBlockItIsGiven_AndReadsBackAsIt()
    {
        var palette = ThemeCatalog.DarkPalette();
        var shape = new Dictionary<string, CornerRadius>
        {
            ["RadiusCard"] = new CornerRadius(0, 12, 0, 12),
            ["RadiusSmall"] = new CornerRadius(3),
        };

        string text = ThemeFileTemplate.Write("Shaped", palette, true, null, BackgroundPosition.Center, shape);

        var read = Read(text).Shape!;
        Assert.Equal(new CornerRadius(0, 12, 0, 12), read["RadiusCard"]);
        Assert.Equal(new CornerRadius(3), read["RadiusSmall"]);
        Assert.Equal(2, read.Count);
        Assert.DoesNotContain("\"shape\"", ThemeFileTemplate.Write("Plain", palette, true));
    }
}
