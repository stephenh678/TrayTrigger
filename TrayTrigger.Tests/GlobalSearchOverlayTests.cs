using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TrayTrigger.Views;

namespace TrayTrigger.Tests;

/// <summary>The window's search box: what it shows, which key does what, and that it leaves the page alone until a result is chosen.</summary>
public class GlobalSearchOverlayTests
{
    private static GlobalHit Hit(GlobalHitKind kind, string title, string detail = "") => new(kind, title, detail, string.Empty, title);

    private static IReadOnlyList<GlobalGroup> Groups(string query) => query switch
    {
        "none" => [],
        _ =>
        [
            new GlobalGroup("Games", [Hit(GlobalHitKind.Game, "Witcher 3", "RPG"), Hit(GlobalHitKind.Game, "Witcher Tales", "Cards")]),
            new GlobalGroup("Settings", [Hit(GlobalHitKind.Setting, "Launch options", "General")]),
        ],
    };

    private static GlobalSearchOverlay NewOverlay(List<(GlobalHit Hit, string Query)> chosen)
    {
        var overlay = new GlobalSearchOverlay { Search = Groups, Chosen = (hit, query) => chosen.Add((hit, query)) };
        var window = new Window { Width = 800, Height = 600, Content = overlay, ShowInTaskbar = false, Left = -10000, Top = -10000 };
        window.Show();
        return overlay;
    }

    private static string[] RowTitles(GlobalSearchOverlay overlay) =>
        FindAll<Button>(overlay).Select(b => System.Windows.Automation.AutomationProperties.GetName(b)).ToArray();

    private static List<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        var found = new List<T>();
        (root as UIElement)?.UpdateLayout();
        void Walk(DependencyObject node)
        {
            if (node is T match) found.Add(match);
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
                Walk(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
        }
        Walk(root);
        return found;
    }

    private static void Press(GlobalSearchOverlay overlay, Key key)
    {
        var box = FindAll<TextBox>(overlay).First();
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(box), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        box.RaiseEvent(args);
    }

    [Fact]
    public void Closed_ByDefault_AndOpenShowsAnEmptyBoxWithAHint()
    {
        StartupSmokeTests.WithAppResources(_ =>
        {
            var overlay = NewOverlay([]);
            Assert.False(overlay.IsOpen);

            overlay.Open();

            Assert.True(overlay.IsOpen);
            Assert.Empty(RowTitles(overlay));
            Assert.Contains("Type to find", FindAll<TextBlock>(overlay).Select(t => t.Text).First(t => t.StartsWith("Type")));
        });
    }

    [Fact]
    public void ATypedQuery_ListsTheGroupsAndTheirRows()
    {
        StartupSmokeTests.WithAppResources(_ =>
        {
            var overlay = NewOverlay([]);
            overlay.Open();

            overlay.SetQuery("witcher");

            Assert.Equal(["Witcher 3, RPG", "Witcher Tales, Cards", "Launch options, General"], RowTitles(overlay));
            var headings = FindAll<TextBlock>(overlay).Select(t => t.Text).ToList();
            Assert.Contains("GAMES", headings);
            Assert.Contains("SETTINGS", headings);
        });
    }

    [Fact]
    public void NoMatches_SaysSo_WithTheQuery()
    {
        StartupSmokeTests.WithAppResources(_ =>
        {
            var overlay = NewOverlay([]);
            overlay.Open();

            overlay.SetQuery("none");

            Assert.Empty(RowTitles(overlay));
            Assert.Contains(FindAll<TextBlock>(overlay), t => t.Text.StartsWith("No matches for") && t.Text.Contains("none"));
        });
    }

    [Fact]
    public void EnterChoosesTheTopRow_DownMovesAndWraps_AndTheBoxClosesOnChoosing()
    {
        StartupSmokeTests.WithAppResources(_ =>
        {
            var chosen = new List<(GlobalHit Hit, string Query)>();
            var overlay = NewOverlay(chosen);
            overlay.Open();
            overlay.SetQuery("witcher");

            Press(overlay, Key.Enter);

            var first = Assert.Single(chosen);
            Assert.Equal("Witcher 3", first.Hit.Title);
            Assert.Equal("witcher", first.Query);
            Assert.False(overlay.IsOpen);

            overlay.Open();
            overlay.SetQuery("witcher");
            Press(overlay, Key.Down);
            Press(overlay, Key.Down);
            Press(overlay, Key.Enter);
            Assert.Equal("Launch options", chosen[1].Hit.Title);

            overlay.Open();
            overlay.SetQuery("witcher");
            Press(overlay, Key.Up);
            Press(overlay, Key.Enter);
            Assert.Equal("Launch options", chosen[2].Hit.Title);
        });
    }

    [Fact]
    public void Escape_Closes_WithoutChoosing()
    {
        StartupSmokeTests.WithAppResources(_ =>
        {
            var chosen = new List<(GlobalHit Hit, string Query)>();
            var overlay = NewOverlay(chosen);
            overlay.Open();
            overlay.SetQuery("witcher");

            Press(overlay, Key.Escape);

            Assert.False(overlay.IsOpen);
            Assert.Empty(chosen);
        });
    }

    [Fact]
    public void OpeningAgain_StartsEmpty()
    {
        StartupSmokeTests.WithAppResources(_ =>
        {
            var overlay = NewOverlay([]);
            overlay.Open();
            overlay.SetQuery("witcher");
            overlay.Close();

            overlay.Open();

            Assert.Empty(RowTitles(overlay));
            Assert.Equal(string.Empty, FindAll<TextBox>(overlay).First().Text);
        });
    }

    [Fact]
    public void Closing_LeavesTheCursorOnTheWindow_SoItStillGetsKeys()
    {
        StartupSmokeTests.WithAppResources(_ =>
        {
            var overlay = NewOverlay([]);
            var window = Window.GetWindow(overlay)!;
            overlay.Open();
            overlay.SetQuery("witcher");

            overlay.Close(restoreFocus: false);

            // A window with no logical focus comes back from a dialog with the cursor nowhere, and then gets no
            // key presses: Ctrl+K stopped working after a game's details were closed.
            Assert.Same(window, FocusManager.GetFocusedElement(window));
        });
    }

    [Fact]
    public void ClickingARow_ChoosesIt()
    {
        StartupSmokeTests.WithAppResources(_ =>
        {
            var chosen = new List<(GlobalHit Hit, string Query)>();
            var overlay = NewOverlay(chosen);
            overlay.Open();
            overlay.SetQuery("witcher");

            var second = FindAll<Button>(overlay)[1];
            second.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            Assert.Equal("Witcher Tales", Assert.Single(chosen).Hit.Title);
        });
    }
}
