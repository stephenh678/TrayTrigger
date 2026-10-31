using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.ExceptionServices;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>Edit Game's Artwork tab (Banner, Poster, Icon: Automatic or Custom), the Online match card, and the banner the player chose.</summary>
/// <remarks>A Save writes to Activity & History through the static <see cref="ActivityService.Current"/>, so these run apart from the tests that set it.</remarks>
[Collection(ActivityCurrentCollection.Name)]
public class ArtworkTabTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void Sta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private string Picture(string name, int width, int height)
    {
        Directory.CreateDirectory(Path.Combine(_root, "files"));
        string path = Path.Combine(_root, "files", name);
        using var bmp = new Bitmap(width, height);
        var rng = new Random(3);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.DarkSlateBlue);
            for (int i = 0; i < 300; i++)
                g.FillRectangle(new SolidBrush(Color.FromArgb(rng.Next(256), rng.Next(256), rng.Next(256))), rng.Next(width), rng.Next(height), 30, 30);
        }
        bmp.Save(path, path.EndsWith(".png") ? ImageFormat.Png : ImageFormat.Jpeg);
        return path;
    }

    private GameEditViewModel Create(GameEntry? game = null) => new(
        game ?? new GameEntry { Name = "Hades", Category = "Action" }, new[] { "Action" },
        new IconExtractorService(new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"))))
    {
        CoversDirectory = Path.Combine(_root, "Covers"),
        HeroArt = new HeroArtService(Path.Combine(_root, "Heroes")),
    };

    [Fact]
    public void TheArtworkTab_SitsAfterIdentity_AndItsCardShowsOnlyThere() => Sta(() =>
    {
        var vm = Create();
        var tabs = vm.AvailableSections.ToList();
        Assert.Equal(tabs.IndexOf(GameEditSection.Identity) + 1, tabs.IndexOf(GameEditSection.Artwork));

        vm.SelectedSection = GameEditSection.Artwork;
        Assert.True(vm.ShowArtworkCard);
        Assert.False(vm.ShowIdentityCard || vm.ShowLaunchCard || vm.ShowPerformanceCard);
        vm.SelectedSection = GameEditSection.Launch;
        Assert.False(vm.ShowArtworkCard);
        vm.SelectedSection = GameEditSection.All;
        Assert.True(vm.ShowArtworkCard);
    });

    [Fact]
    public void ANewGame_StartsAutomatic_AndAChosenPosterIsCustomUntilUseAutomatic() => Sta(() =>
    {
        var vm = Create();
        Assert.False(vm.PosterIsCustom);
        Assert.True(vm.PosterIsAutomatic);
        Assert.StartsWith("Automatic", vm.PosterChip);
        Assert.False(vm.UseAutomaticPosterCommand.CanExecute(null));

        string file = Picture("cover.png", 600, 900);
        Assert.Null(vm.TryUseImage(ArtSlot.Poster, file));

        Assert.True(vm.PosterIsCustom);
        Assert.Equal("Custom · cover.png", vm.PosterChip);
        Assert.True(vm.UseAutomaticPosterCommand.CanExecute(null));
        Assert.True(vm.HasUnsavedChanges);

        vm.UseAutomaticPosterCommand.Execute(null);
        Assert.False(vm.PosterIsCustom);
        Assert.StartsWith("Automatic", vm.PosterChip);
    });

    [Fact]
    public void AFileThatIsNotAPicture_IsRefused_WithAReason_AndNothingChanges() => Sta(() =>
    {
        var vm = Create();
        string notes = Path.Combine(_root, "notes.txt");
        Directory.CreateDirectory(_root);
        File.WriteAllText(notes, "hello");
        string broken = Path.Combine(_root, "broken.png");
        File.WriteAllText(broken, "not really a png");

        Assert.NotNull(vm.TryUseImage(ArtSlot.Poster, notes));
        Assert.NotNull(vm.TryUseImage(ArtSlot.Banner, broken));
        Assert.NotNull(vm.TryUseImage(ArtSlot.Icon, Path.Combine(_root, "missing.png")));
        Assert.True(vm.StatusIsProblem);
        Assert.False(vm.PosterIsCustom || vm.HeroIsCustom || vm.IconIsCustom);
        Assert.False(vm.HasUnsavedChanges);
    });

    [Fact]
    public void TheIconCanBeAPictureOrAnExe_ButThePosterCannotBeAnExe() => Sta(() =>
    {
        var vm = Create();
        Assert.NotNull(vm.TryUseImage(ArtSlot.Poster, Path.Combine(Environment.SystemDirectory, "notepad.exe")));
        Assert.Null(vm.TryUseImage(ArtSlot.Icon, Path.Combine(Environment.SystemDirectory, "notepad.exe")));
        Assert.True(vm.IconIsCustom);
        Assert.Equal("Custom · notepad.exe", vm.IconChip);

        vm.UseAutomaticIconCommand.Execute(null);
        Assert.False(vm.IconIsCustom);
        Assert.Equal("Automatic · the game's exe", vm.IconChip);
    });

    [Fact]
    public void AChosenBanner_IsCopiedIntoCovers_OnSave_AndTheGameRemembersIt() => Sta(() =>
    {
        var game = new GameEntry { Name = "Hades", Category = "Action" };
        var vm = Create(game);
        string file = Picture("wide.jpg", 1920, 620);

        Assert.Null(vm.TryUseImage(ArtSlot.Banner, file));
        Assert.True(vm.HeroIsCustom);
        Assert.True(vm.HasHeroPreview);
        Assert.Equal("Custom · wide.jpg", vm.HeroChip);
        Assert.True(vm.HasUnsavedChanges);

        vm.SaveCommand.Execute(null);

        Assert.NotNull(game.HeroImagePath);
        Assert.True(File.Exists(game.HeroImagePath));
        Assert.Equal(Path.Combine(_root, "Covers"), Path.GetDirectoryName(game.HeroImagePath));
        // Named the way Covers' clean-up knows a game's own files: {Id}_{ticks}.
        Assert.Equal(game.Id, GameDataCleanup.KeyOf(game.HeroImagePath!));
        Assert.True(File.Exists(file));
    });

    [Fact]
    public void PuttingTheBannerBackToAutomatic_LetsGoOfTheChosenFile_OnSave() => Sta(() =>
    {
        var game = new GameEntry { Name = "Hades", Category = "Action" };
        var first = Create(game);
        first.TryUseImage(ArtSlot.Banner, Picture("wide.jpg", 1920, 620));
        first.SaveCommand.Execute(null);
        string kept = game.HeroImagePath!;

        var second = Create(game);
        Assert.True(second.HeroIsCustom);
        second.UseAutomaticHeroCommand.Execute(null);
        Assert.False(second.HeroIsCustom);
        second.SaveCommand.Execute(null);

        Assert.Null(game.HeroImagePath);
        Assert.False(File.Exists(kept));
    });

    [Fact]
    public void ACardThatIsNotWide_IsKept_ButTheStatusLineWarnsItWillBeCropped() => Sta(() =>
    {
        var vm = Create();
        Assert.Null(vm.TryUseImage(ArtSlot.Banner, Picture("square.png", 800, 800)));
        Assert.True(vm.HeroIsCustom);
        Assert.Contains("isn't wide", vm.StatusMessage);
    });

    [Fact]
    public void CancelOrSavingNothing_LeavesTheArtworkFlagsOnTheGameAsTheyWere() => Sta(() =>
    {
        var game = new GameEntry { Name = "Hades", Category = "Action", HasCustomPoster = true, HasCustomIcon = true };
        var vm = Create(game);
        Assert.True(vm.PosterIsCustom);
        Assert.True(vm.IconIsCustom);
        Assert.False(vm.HasUnsavedChanges);

        vm.SaveCommand.Execute(null);
        Assert.True(game.HasCustomPoster);
        Assert.True(game.HasCustomIcon);
    });

    // --- Online match ---

    [Fact]
    public void RemovingTheMatch_ClearsTheAppId_AndUndoPutsItBack() => Sta(() =>
    {
        var vm = Create(new GameEntry { Name = "Hades", Category = "Action", SteamAppId = "1145360" });
        Assert.True(vm.HasMatch);
        Assert.Contains("1145360", vm.MatchSummary);

        vm.RemoveMatchCommand.Execute(null);
        Assert.False(vm.HasMatch);
        Assert.Null(vm.SteamAppId);
        Assert.Contains("Not matched", vm.MatchSummary);
        Assert.True(vm.CanUndoLookUp);

        vm.UndoLookUpCommand.Execute(null);
        Assert.Equal("1145360", vm.SteamAppId);
        Assert.True(vm.HasMatch);
    });

    [Fact]
    public void ARefusedAppId_OpensTheBoxItIsTypedIn() => Sta(() =>
    {
        var vm = Create();
        Assert.False(vm.ShowAppIdEntry);
        vm.ShowAppIdEntry = false;
        vm.SteamAppId = "abc";
        vm.SaveCommand.Execute(null);
        Assert.True(vm.ShowAppIdEntry);
    });

    [Fact]
    public void ANewMatchMayReplaceTheTitleGenreAndPoster_AllTickedToBegin_AndEachCanBeLeftAlone() => Sta(() =>
    {
        var vm = Create();
        Assert.True(vm.UpdateTitleFromMatch && vm.UpdateGenreFromMatch && vm.UpdatePosterFromMatch);
    });

    // --- The banner service: a chosen banner wins, and Find again looks past it ---

    [Fact]
    public async Task AChosenBanner_WinsOverTheAutomaticOne_WithoutAskingAnyone()
    {
        string file = Picture("mine.jpg", 1920, 620);
        int asked = 0;
        var service = new HeroArtService(Path.Combine(_root, "Heroes")) { DownloadBytes = (_, _) => { asked++; return Task.FromResult<byte[]?>(null); } };
        var game = new GameEntry { Name = "Hades", SteamAppId = "1145360", HeroImagePath = file };

        Assert.Equal(file, await service.GetHeroPathAsync(game, game.SteamAppId, null, 0.5));
        Assert.Equal(0, asked);

        // Find again forgets what was kept and looks, whatever the player chose.
        await service.RefetchAsync(game, game.SteamAppId, null, 0.5);
        Assert.True(asked > 0);
    }

    [Fact]
    public void RemovingAGame_TakesItsChosenBanner_FromCovers()
    {
        string covers = Path.Combine(_root, "Covers");
        string icons = Path.Combine(_root, "Icons");
        Directory.CreateDirectory(covers);
        Directory.CreateDirectory(icons);
        var gone = new GameEntry { Name = "Gone" };
        gone.HeroImagePath = Path.Combine(covers, $"{gone.Id}_{DateTime.UtcNow.Ticks}.jpg");
        File.WriteAllText(gone.HeroImagePath, "x");

        GameDataCleanup.DeleteRemovedGameData([gone], [], icons, covers);

        Assert.False(File.Exists(gone.HeroImagePath));
    }

    [Theory]
    [InlineData(300, 118)]     // narrow: the floor
    [InlineData(600, 194)]     // about as the window opens: the picture keeps the banner's shape
    [InlineData(1500, 260)]    // wide: the ceiling
    [InlineData(double.NaN, 118)]
    public void TheBannerPreview_KeepsItsShapeAsItsBoxGrows(double width, double expected)
    {
        Assert.Equal(expected, Math.Round(TrayTrigger.Converters.RatioHeightConverter.For(width, "0.3229,118,260")));
    }

    // --- Cards that come and go with the PC, so a tab never shows an empty one ---

    [Fact]
    public void TheScreensCard_NeedsMoreThanOneMonitor() => Sta(() =>
    {
        var one = new GameEditViewModel(new GameEntry { Name = "Hades" }, new[] { "Action" },
            new IconExtractorService(new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"))))
        { MonitorCount = () => 1, CoversDirectory = Path.Combine(_root, "Covers") };
        one.SelectedSection = GameEditSection.Display;
        Assert.True(one.ShowDisplayCard);
        Assert.False(one.ShowScreensCard);

        var two = new GameEditViewModel(new GameEntry { Name = "Hades" }, new[] { "Action" },
            new IconExtractorService(new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"))))
        { MonitorCount = () => 2, CoversDirectory = Path.Combine(_root, "Covers") };
        two.SelectedSection = GameEditSection.Display;
        Assert.True(two.ShowScreensCard);
        two.SelectedSection = GameEditSection.Launch;
        Assert.False(two.ShowScreensCard);
    });

    [Fact]
    public void TheCoresAndHdrCard_IsLeftOut_WhenThisPcHasNoneOfThem() => Sta(() =>
    {
        var vm = new GameEditViewModel(new GameEntry { Name = "Hades" }, new[] { "Action" },
            new IconExtractorService(new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"))))
        {
            HdrDisplayPresent = () => false,
            CoversDirectory = Path.Combine(_root, "Covers"),
        };
        vm.SelectedSection = GameEditSection.Performance;
        Assert.True(vm.ShowPerformanceCard);
        Assert.Equal(vm.ShowCpuCores || vm.ShowHdr || vm.ShowAutoHdr, vm.ShowCoresAndHdrCard);

        var hdr = new GameEditViewModel(new GameEntry { Name = "Hades", Hdr = HdrMode.On }, new[] { "Action" },
            new IconExtractorService(new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"))))
        { HdrDisplayPresent = () => false, CoversDirectory = Path.Combine(_root, "Covers") };
        hdr.SelectedSection = GameEditSection.Performance;
        Assert.True(hdr.ShowCoresAndHdrCard);
    });

    [Fact]
    public void HowTheScriptsRun_StartsShut_ForAGameWithNoScript_AndOpensForOneWithAScriptOrARefusedValue() => Sta(() =>
    {
        Assert.False(Create().ShowScriptRunOptions);

        var withScript = Create(new GameEntry { Name = "Hades", PreLaunchScriptPath = Path.Combine(_root, "pre.ps1") });
        Assert.True(withScript.ShowScriptRunOptions);

        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "real.ps1"), "# a script");
        var refused = Create();
        refused.PreLaunchScriptPath = Path.Combine(_root, "real.ps1");
        refused.PreLaunchScriptTimeoutSeconds = "0";
        refused.SaveCommand.Execute(null);
        Assert.True(refused.ShowScriptRunOptions);
    });

    [Fact]
    public void UndoingAMatchChange_PutsBackWhetherThePosterWasYours() => Sta(() =>
    {
        var vm = Create(new GameEntry { Name = "Hades", Category = "Action", SteamAppId = "1145360" });
        Assert.Null(vm.TryUseImage(ArtSlot.Poster, Picture("mine.png", 600, 900)));
        Assert.True(vm.PosterIsCustom);

        // A match changes the App ID (with Undo offered) and, with Poster ticked, replaces the poster with Steam's.
        vm.RemoveMatchCommand.Execute(null);
        vm.ApplyFetchedCover(Picture("steams.jpg", 600, 900));
        Assert.False(vm.PosterIsCustom);

        vm.UndoLookUpCommand.Execute(null);

        Assert.True(vm.PosterIsCustom);
        Assert.StartsWith("Custom", vm.PosterChip);
        Assert.Equal("1145360", vm.SteamAppId);
    });

    [Fact]
    public void RemovingTheMatch_LetsTheBannerCardFollowTheGame() => Sta(() =>
    {
        var vm = Create(new GameEntry { Name = "Hades", Category = "Action", SteamAppId = "1145360" });
        var seen = new List<string?>();
        vm.PropertyChanged += (_, e) => seen.Add(e.PropertyName);
        vm.RemoveMatchCommand.Execute(null);
        Assert.Contains(nameof(GameEditViewModel.HeroPreview), seen);
    });

    [Fact]
    public void SavingAChosenHeroBanner_LeavesOneChangeLineInActivity() => Sta(() =>
    {
        Directory.CreateDirectory(_root);
        var log = new ActivityService(Path.Combine(_root, "activity"), readOnly: true);
        var previous = ActivityService.Current;
        ActivityService.Current = log;
        try
        {
            var vm = Create();
            Assert.Null(vm.TryUseImage(ArtSlot.Banner, Picture("wide.jpg", 1920, 620)));
            vm.SaveCommand.Execute(null);

            var line = Assert.Single(log.Entries, e => e.Subject == "Hades");
            Assert.Equal(ActivityLevel.Change, line.Level);
            Assert.Equal("Hades: hero banner changed", line.Text);
            Assert.Equal("Hero banner: automatic -> a chosen one", line.Detail);
        }
        finally
        {
            ActivityService.Current = previous;
        }
    });

    [Fact]
    public void ChoosingABanner_AndThenUsingAutomatic_IsNotAnEditToAskAboutLeaving() => Sta(() =>
    {
        var vm = Create();
        Assert.False(vm.HasUnsavedChanges);

        Assert.Null(vm.TryUseImage(ArtSlot.Banner, Picture("wide.jpg", 1920, 620)));
        Assert.True(vm.HasUnsavedChanges);

        vm.UseAutomaticHeroCommand.Execute(null);
        Assert.False(vm.HasUnsavedChanges);
    });

    [Fact]
    public void AGameWithABannerOfItsOwn_LetsItGoOnSave_WhenUseAutomaticWasPressed() => Sta(() =>
    {
        string covers = Path.Combine(_root, "Covers");
        Directory.CreateDirectory(covers);
        string mine = Path.Combine(covers, "mine_1.jpg");
        File.Copy(Picture("wide.jpg", 1920, 620), mine);
        var game = new GameEntry { Name = "Hades", Category = "Action", HeroImagePath = mine };

        var vm = Create(game);
        vm.UseAutomaticHeroCommand.Execute(null);
        Assert.True(vm.HasUnsavedChanges);
        vm.SaveCommand.Execute(null);

        Assert.Null(game.HeroImagePath);
        Assert.False(File.Exists(mine));
    });
}
