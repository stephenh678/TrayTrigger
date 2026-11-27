using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>
/// Keeps the Tab order short (2.0.0-beta.4): a row of tabs is one Tab stop (views:FocusGroup), and a game's or
/// tool's favorite star is never one. Reads the XAML, so a new strip or star can't slip back in unnoticed.
/// </summary>
public class TabStopGuardTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "TrayTrigger.csproj")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("TrayTrigger.csproj not found above " + AppContext.BaseDirectory);
        }
    }

    private static IEnumerable<string> XamlFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot, "Views"), "*.xaml").Append(Path.Combine(RepoRoot, "MainWindow.xaml"));

    // A tab you can move to: a preview drawn with tab buttons that take no focus (Focusable="False") is not one.
    private static bool IsTabButton(XElement e) =>
        e.Name == Presentation + "Button" && (string?)e.Attribute("Style") is { } style
        && (style.Contains("SegmentedTabButton") || style.Contains("UnderlineTabButton"))
        && (string?)e.Attribute("Focusable") != "False";

    private static bool IsGroup(XElement e) => e.Attributes().Any(a => a.Name.LocalName == "FocusGroup.IsGroup" && a.Value == "True");

    // The windows a controller works in (GamepadNavigator): Game Details and its Settings tab, and what the Library opens.
    private static readonly string[] ControllerWindows =
        { "GameDetailsDialog.xaml", "GameEditPanel.xaml", "LibraryView.xaml", "QuickInputDialog.xaml", "CollectionsDialog.xaml", "GameMatchPickerDialog.xaml" };

    [Fact]
    public void ControllerSkipsEveryButtonThatOpensAWindowsPicker()
    {
        // Browse..., Choose image and New script open Windows' own file pickers, which a controller can't work or close.
        var opensPicker = new Regex(@"\{Binding (\w+\.)?(Browse\w*|Choose\w*|New\w*Script)Command\}");
        var missing = ControllerWindows
            .Select(name => Path.Combine(RepoRoot, "Views", name))
            .SelectMany(file => XDocument.Load(file).Descendants()
                .Where(e => (string?)e.Attribute("Command") is { } command && opensPicker.IsMatch(command))
                .Where(e => !e.Attributes().Any(a => a.Name.LocalName == "GamepadNavigator.Skip" && a.Value == "True"))
                .Select(e => $"{Path.GetFileName(file)}: {(string?)e.Attribute("Command")}"))
            .ToList();
        Assert.True(missing.Count == 0, "Add views:GamepadNavigator.Skip=\"True\" to: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryRowOfTabs_IsOneTabStop()
    {
        var missing = new List<string>();
        foreach (var file in XamlFiles())
        {
            var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
            // Two or more tab buttons side by side in one panel.
            foreach (var panel in doc.Descendants().Where(p => p.Elements().Count(IsTabButton) >= 2 && !IsGroup(p)))
                missing.Add($"{Path.GetFileName(file)}:{((System.Xml.IXmlLineInfo)panel).LineNumber} {panel.Name.LocalName}");
            // A list of tabs: an ItemsControl whose item is a tab button.
            foreach (var list in doc.Descendants(Presentation + "ItemsControl").Where(l => l.Descendants().Any(IsTabButton) && !IsGroup(l)))
                missing.Add($"{Path.GetFileName(file)}:{((System.Xml.IXmlLineInfo)list).LineNumber} ItemsControl");
        }
        Assert.True(missing.Count == 0, "Rows of tabs that aren't one Tab stop (add views:FocusGroup.IsGroup=\"True\"):\n" + string.Join("\n", missing));
    }

    [Fact]
    public void FavoriteStars_AreNotTabStops()
    {
        var stops = new List<string>();
        var button = new Regex(@"<Button\b[^>]*ToggleFavoriteCommand[^>]*>", RegexOptions.Singleline);
        foreach (var file in XamlFiles())
        {
            string xaml = File.ReadAllText(file);
            foreach (Match m in button.Matches(xaml))
            {
                // Focusable, not just IsTabStop: the arrow keys and a click still put focus on a button that is only out of the Tab order.
                bool fine = m.Value.Contains("Focusable=\"False\"") || m.Value.Contains("FavoriteToggleButton");
                if (!fine) stops.Add($"{Path.GetFileName(file)}:{xaml[..m.Index].Count(c => c == '\n') + 1}");
            }
        }
        Assert.True(stops.Count == 0, "Favorite stars that can take focus (the game's menu has Favorite):\n" + string.Join("\n", stops));
        string app = File.ReadAllText(Path.Combine(RepoRoot, "App.xaml"));
        // Within the style itself: the match may not run past its </Style> onto another style's setter.
        Assert.Matches(new Regex("x:Key=\"FavoriteToggleButton\"(?:(?!</Style>).)*?<Setter Property=\"Focusable\" Value=\"False\"/>", RegexOptions.Singleline), app);
    }
}
