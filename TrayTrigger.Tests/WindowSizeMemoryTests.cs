using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>Dialogs opening at the size they were last left at, and the game cards of the theme preview.</summary>
[Collection("ThemeService")]
public class WindowSizeMemoryTests : IDisposable
{
    private readonly AppSettings _settings = new();
    private int _saves;

    public WindowSizeMemoryTests() => WindowSizeMemory.Configure(() => _settings, _ => _saves++);

    public void Dispose() => WindowSizeMemory.Configure(() => new AppSettings(), _ => { });

    private static Window Dialog() => new() { Width = 800, Height = 600, MinWidth = 600, MinHeight = 400 };

    [Fact]
    public void ASavedSize_IsAppliedToTheWindow() => WpfTestHost.Run(() =>
    {
        _settings.WindowSizes["Scan"] = new WindowSize { Width = 900, Height = 650 };
        var window = Dialog();

        WindowSizeMemory.Attach(window, "Scan");

        Assert.Equal(900, window.Width);
        Assert.Equal(650, window.Height);
    });

    [Fact]
    public void ASizeBelowTheMinimum_OrBiggerThanTheScreen_IsIgnored() => WpfTestHost.Run(() =>
    {
        var window = Dialog();

        Assert.False(WindowSizeMemory.Apply(window, new WindowSize { Width = 300, Height = 650 }));
        Assert.False(WindowSizeMemory.Apply(window, new WindowSize { Width = 900, Height = 300 }));
        Assert.False(WindowSizeMemory.Apply(window, new WindowSize { Width = 99999, Height = 650 }));
        Assert.False(WindowSizeMemory.Apply(window, null));
        Assert.Equal(800, window.Width);
    });

    [Fact]
    public void AWindowWithNothingSaved_KeepsItsOwnSize() => WpfTestHost.Run(() =>
    {
        var window = Dialog();

        WindowSizeMemory.Attach(window, "Never");

        Assert.Equal(800, window.Width);
        Assert.Equal(600, window.Height);
        Assert.Empty(_settings.WindowSizes);
    });

    [Fact]
    public void AWindowWithNoFixedSize_IsLeftAlone() => WpfTestHost.Run(() =>
    {
        var window = new Window { MinWidth = 100 };

        WindowSizeMemory.Attach(window, "Auto");

        Assert.True(double.IsNaN(window.Width));
        Assert.Empty(_settings.WindowSizes);
    });

    [Fact]
    public void ASizeAtTheDefault_IsNotKept_AndOneBeyondItIsKeptLessAPanel()
    {
        Assert.Null(WindowSizeMemory.SizeToKeep(940, 792, 0, 940, 792));
        // A window with a 680 px panel out is that much wider than the editor itself.
        Assert.Null(WindowSizeMemory.SizeToKeep(1620, 792, 680, 940, 792));

        var kept = WindowSizeMemory.SizeToKeep(1700.4, 800.2, 680, 940, 792)!;
        Assert.Equal(1020, kept.Width);
        Assert.Equal(800, kept.Height);
    }

    [Fact]
    public void ASizeIsSavedWhenItChanges_AndForgottenWhenItIsTheDefaultAgain()
    {
        WindowSizeMemory.Remember("Scan", new WindowSize { Width = 900, Height = 650 });
        Assert.Equal(900, _settings.WindowSizes["Scan"].Width);
        Assert.Equal(1, _saves);

        // The same size again changes nothing, so nothing is written.
        WindowSizeMemory.Remember("Scan", new WindowSize { Width = 900, Height = 650 });
        Assert.Equal(1, _saves);

        WindowSizeMemory.Remember("Scan", null);
        Assert.False(_settings.WindowSizes.ContainsKey("Scan"));
        Assert.Equal(2, _saves);

        // Forgetting what was never kept writes nothing either.
        WindowSizeMemory.Remember("Scan", null);
        Assert.Equal(2, _saves);
    }

    [Fact]
    public void ThePreviewCards_AreTheMostPlayedGamesWithACover_AndMadeUpOnesWhenThereAreNone() => WpfTestHost.Run(() =>
    {
        var none = PosterSample.Build(null);
        Assert.Equal(3, none.Count);
        Assert.All(none, s => Assert.False(s.HasCover));
        Assert.Equal(new[] { "Normal", "Mouse over", "Selected" }, none.Select(s => s.Caption));
        Assert.True(none[1].IsHover);
        Assert.True(none[2].IsSelected);

        string folder = Path.Combine(Path.GetTempPath(), "tt-cards-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string cover = Path.Combine(folder, "cover.png");
            var source = BitmapSource.Create(4, 6, 96, 96, PixelFormats.Gray8, null, new byte[24], 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using (var file = File.Create(cover)) encoder.Save(file);

            var library = new[]
            {
                new GameEntry { Name = "Played A Lot", CoverImagePath = cover, CumulativePlaytimeMinutes = 500 },
                new GameEntry { Name = "No Cover", CumulativePlaytimeMinutes = 9000 },
            };
            var samples = PosterSample.Build(library);

            Assert.Equal(3, samples.Count);
            Assert.Equal("Played A Lot", samples[0].Name);
            Assert.True(samples[0].HasCover);
            Assert.False(samples[1].HasCover);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });
}
