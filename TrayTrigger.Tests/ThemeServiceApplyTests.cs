using System.IO;
using TrayTrigger.Services.Theming;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>
/// Puts themes on the shared test Application through <see cref="ThemeService"/>. Everything here shares one
/// singleton (and the picture state the window binds to), so these run one at a time, and each leaves Dark behind.
/// </summary>
[Collection("ThemeService")]
public class ThemeServiceApplyTests : IDisposable
{
    public ThemeServiceApplyTests() => WpfTestHost.Run(() => ThemeService.Instance.WindowShown = true);

    public void Dispose() => WpfTestHost.Run(() => ThemeService.Instance.Apply(ThemeCatalog.DarkId));

    private static void OnUiThread(Action body) => WpfTestHost.Run(body);

    [Fact]
    public void ATheme_ThatIsNotThere_LeavesDarkOnScreen_AndSaysWhy() => OnUiThread(() =>
    {
        bool applied = ThemeService.Instance.Apply("user:no-such-theme");

        Assert.False(applied);
        Assert.Equal("Dark", ThemeService.Instance.ActiveName);
        Assert.Contains("no-such-theme", ThemeService.Instance.FallbackReason);
        // The choice is kept, so the theme returns when its file does.
        Assert.Equal("user:no-such-theme", ThemeService.Instance.SelectedId);
    });

    [Fact]
    public void ATheme_ThatSetsCorners_ChangesTheRadiiOnScreen_AndDarkPutsThemBack()
    {
        string root = Path.Combine(Path.GetTempPath(), "tt-shape-" + Guid.NewGuid().ToString("N"));
        string before = ThemeCatalog.UserThemesDirectory;
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "Leaf.theme.json"), """{ "shape": { "RadiusCard": [0, 20, 0, 20] } }""");
            ThemeCatalog.UserThemesDirectory = root;

            OnUiThread(() =>
            {
                var resources = System.Windows.Application.Current.Resources;
                Assert.True(ThemeService.Instance.Apply("user:Leaf"));
                Assert.Equal(new System.Windows.CornerRadius(0, 20, 0, 20), resources["RadiusCard"]);
                Assert.Equal(new System.Windows.CornerRadius(0, 19, 0, 19), resources["RadiusPoster"]);
                Assert.Equal(new System.Windows.CornerRadius(6), resources["RadiusControl"]);

                ThemeService.Instance.Apply(ThemeCatalog.DarkId);
                Assert.Equal(new System.Windows.CornerRadius(8), resources["RadiusCard"]);
                Assert.Equal(new System.Windows.CornerRadius(7), resources["RadiusPoster"]);
            });
        }
        finally
        {
            ThemeCatalog.UserThemesDirectory = before;
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void TheThemesFolder_DeletedAndMadeAgain_IsWatchedAgain()
    {
        string root = Path.Combine(Path.GetTempPath(), "tt-watch-" + Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(root, "Themes");
        string before = ThemeCatalog.UserThemesDirectory;
        int changes = 0;
        var changed = new System.Threading.AutoResetEvent(false);
        EventHandler onChanged = (_, _) => { System.Threading.Interlocked.Increment(ref changes); changed.Set(); };
        try
        {
            Directory.CreateDirectory(folder);
            ThemeCatalog.UserThemesDirectory = folder;
            WpfTestHost.Run(() => { ThemeService.Instance.ThemesChanged += onChanged; ThemeService.Instance.WatchUserThemes(); });

            // The watching works, then the folder goes and comes back with a theme in it.
            File.WriteAllText(Path.Combine(folder, "A.theme.json"), "{ }");
            Assert.True(changed.WaitOne(TimeSpan.FromSeconds(8)), "the first file was not noticed");

            Directory.Delete(folder, recursive: true);
            System.Threading.Thread.Sleep(1500);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "B.theme.json"), "{ }");
            Assert.True(changed.WaitOne(TimeSpan.FromSeconds(8)), "the folder coming back was not noticed");

            // Let what the new folder set off finish, then a file written later has to be noticed too.
            System.Threading.Thread.Sleep(1500);
            while (changed.WaitOne(0)) { }
            File.WriteAllText(Path.Combine(folder, "C.theme.json"), "{ }");
            Assert.True(changed.WaitOne(TimeSpan.FromSeconds(8)), "the new folder is not being watched");
        }
        finally
        {
            WpfTestHost.Run(() => ThemeService.Instance.ThemesChanged -= onChanged);
            ThemeCatalog.UserThemesDirectory = before;
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ABuiltInTheme_IsPutOnScreen_WithItsOwnColors() => OnUiThread(() =>
    {
        Assert.True(ThemeService.Instance.Apply("Light"));

        Assert.Equal("Light", ThemeService.Instance.ActiveName);
        Assert.False(ThemeService.Instance.IsDark);
        Assert.Null(ThemeService.Instance.FallbackReason);
        Assert.Equal(ThemeCatalog.LightPalette()["Surface"], TrayTrigger.Services.AppPalette.ColorOf("BrushSurface"));
    });

    [Fact]
    public void AutoInAnyCase_IsStoredAsAuto_SoTheSettingFollowsWindows() => OnUiThread(() =>
    {
        ThemeService.Instance.Apply("auto");

        Assert.Equal(ThemeService.AutoId, ThemeService.Instance.SelectedId);
    });

    [Fact]
    public void TurningTheBackgroundOff_RemovesThePicture_AndOnBringsItBack() => OnUiThread(() =>
    {
        ThemeService.Instance.ShowBackground = true;
        ThemeService.Instance.Apply("Dark");
        Assert.True(ThemeBackgroundState.Instance.HasImage);

        ThemeService.Instance.ShowBackground = false;
        Assert.False(ThemeBackgroundState.Instance.HasImage);

        ThemeService.Instance.ShowBackground = true;
        Assert.True(ThemeBackgroundState.Instance.HasImage);
    });

    [Fact]
    public void ThePicture_IsNotLoadedBeforeTheWindowShows_AndIsLetGoWhenItHides() => OnUiThread(() =>
    {
        ThemeService.Instance.ShowBackground = true;
        try
        {
            ThemeService.Instance.WindowShown = false;
            ThemeService.Instance.Apply("Dark");
            Assert.False(ThemeBackgroundState.Instance.HasImage);

            ThemeService.Instance.WindowShown = true;
            Assert.True(ThemeBackgroundState.Instance.HasImage);

            ThemeService.Instance.WindowShown = false;
            Assert.False(ThemeBackgroundState.Instance.HasImage);
        }
        finally { ThemeService.Instance.WindowShown = true; }
    });

    [Fact]
    public void HighContrast_HasNoPicture() => OnUiThread(() =>
    {
        ThemeService.Instance.ShowBackground = true;
        ThemeService.Instance.Apply(ThemeCatalog.HighContrastId);

        Assert.False(ThemeBackgroundState.Instance.HasImage);
    });

    [Fact]
    public void ThemeChanged_IsRaised_WhenAThemeGoesOnScreen() => OnUiThread(() =>
    {
        int raised = 0;
        EventHandler handler = (_, _) => raised++;
        ThemeService.Instance.ThemeChanged += handler;
        try { ThemeService.Instance.Apply("Green"); }
        finally { ThemeService.Instance.ThemeChanged -= handler; }

        Assert.Equal(1, raised);
    });
}
