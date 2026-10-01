using System.IO;
using System.Reflection;
using System.Text.Json;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;
using TrayTrigger.Views;

namespace TrayTrigger.Tests;

/// <summary>
/// "Show details on hover" › what stays on every card at rest: the settings behind the list in
/// Settings › Library &amp; Art, and the names the card template uses to reach them.
/// </summary>
public class PosterDetailsAtRestTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static PosterDetailsViewModel NewDetails(PosterDetailsAtRest model, Action? save = null) =>
        new(() => model, save ?? (() => { }));

    /// <summary>The defaults are the look the feature shipped with: only PLAYING stays.</summary>
    [Fact]
    public void Defaults_KeepOnlyThePlayingTag()
    {
        var atRest = new AppSettings().PosterDetailsAtRest;
        var kept = typeof(PosterDetailsAtRest).GetProperties()
            .Where(p => p.PropertyType == typeof(bool) && (bool)p.GetValue(atRest)!)
            .Select(p => p.Name);
        Assert.Equal([nameof(PosterDetailsAtRest.PlayingTag)], kept);
    }

    /// <summary>A settings file from before the list existed loads with the defaults, not a null.</summary>
    [Fact]
    public void SettingsWithoutTheList_LoadTheDefaults()
    {
        var settings = JsonSerializer.Deserialize("{\"PosterDetailsOnHover\": true}", AppJsonContext.Default.AppSettings)!;
        Assert.NotNull(settings.PosterDetailsAtRest);
        Assert.True(settings.PosterDetailsAtRest.PlayingTag);
        Assert.False(settings.PosterDetailsAtRest.Title);
    }

    /// <summary>
    /// The card template names each part (views:PosterDetailFade.Part="Title") and binds to the view
    /// model property of that name. A part with no property would silently never stay at rest.
    /// </summary>
    [Fact]
    public void EveryCardPart_HasAViewModelProperty()
    {
        foreach (var part in Enum.GetValues<PosterDetailPart>().Where(p => p != PosterDetailPart.None))
        {
            var property = typeof(PosterDetailsViewModel).GetProperty(part.ToString(), BindingFlags.Public | BindingFlags.Instance);
            Assert.True(property?.PropertyType == typeof(bool), $"PosterDetailsViewModel has no bool property '{part}'.");
        }
    }

    /// <summary>...and every setting the list offers is a part a card can show.</summary>
    [Fact]
    public void EverySetting_IsACardPart()
    {
        var parts = Enum.GetNames<PosterDetailPart>();
        foreach (var setting in typeof(PosterDetailsAtRest).GetProperties())
        {
            Assert.Contains(setting.Name, parts);
        }
    }

    [Theory]
    [InlineData(nameof(PosterDetailsViewModel.LauncherLogo), true, false)]
    [InlineData(nameof(PosterDetailsViewModel.Category), true, false)]
    [InlineData(nameof(PosterDetailsViewModel.Title), false, true)]
    [InlineData(nameof(PosterDetailsViewModel.Playtime), false, true)]
    [InlineData(nameof(PosterDetailsViewModel.LastPlayed), false, true)]
    // The tags only some games carry bring their own backgrounds: no shade for every card.
    [InlineData(nameof(PosterDetailsViewModel.HiddenTag), false, false)]
    [InlineData(nameof(PosterDetailsViewModel.FavoriteStar), false, false)]
    [InlineData(nameof(PosterDetailsViewModel.NotInstalledTag), false, false)]
    public void KeepingAPart_KeepsTheShadeBehindIt(string part, bool topShade, bool bottomShade)
    {
        var details = NewDetails(new PosterDetailsAtRest { PlayingTag = false });
        typeof(PosterDetailsViewModel).GetProperty(part)!.SetValue(details, true);

        Assert.Equal(topShade, details.TopShade);
        Assert.Equal(bottomShade, details.BottomShade);
    }

    [Fact]
    public void ChangingAPart_WritesTheSetting_RaisesItAndTheShades_AndSaves()
    {
        var model = new PosterDetailsAtRest();
        int saves = 0;
        var details = NewDetails(model, () => saves++);
        var changed = new List<string?>();
        details.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        details.Title = true;

        Assert.True(model.Title);
        Assert.Equal(1, saves);
        Assert.Contains(nameof(PosterDetailsViewModel.Title), changed);
        Assert.Contains(nameof(PosterDetailsViewModel.BottomShade), changed);

        // Setting it to what it already is neither raises nor saves.
        changed.Clear();
        details.Title = true;
        Assert.Empty(changed);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void ACheckboxInSettings_SurvivesARestart() => WpfTestHost.Run(() =>
    {
        var storage = new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"));
        var vm = new SettingsViewModel(new AppSettings(), storage, new StartupManager(), new TrayPromotionService(), new SteamScannerService());

        vm.PosterDetails.LauncherLogo = true;
        vm.PosterDetails.PlayingTag = false;

        var reloaded = storage.LoadSettings().PosterDetailsAtRest;
        Assert.True(reloaded.LauncherLogo);
        Assert.False(reloaded.PlayingTag);
    });
}
