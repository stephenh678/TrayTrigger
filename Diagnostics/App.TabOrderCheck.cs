#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;
using TrayTrigger.Views;

namespace TrayTrigger;

/// <summary>
/// --test-tab-order &lt;out.txt&gt;: presses Tab (MoveFocus Next) from the top of the Library in each view and of
/// Game Details (Overview and Settings) until focus comes round again, and lists every stop: its type, name, what a
/// screen reader calls it, the game it belongs to, and whether it can be seen when it has focus. A game's stops are
/// listed for the first two games and counted for the rest. Settings are put back as they were.
/// </summary>
public partial class App
{
    private void RunTabOrderCheck(string outPath)
    {
        _skipSettingsSaveOnExit = true;
        var report = new StringBuilder();
        string settingsFile = Path.Combine(_storageService.BaseDirectory, "settings.json");
        var settingsBefore = new[] { settingsFile, settingsFile + ".bak" }.ToDictionary(f => f, f => File.Exists(f) ? File.ReadAllBytes(f) : null);
        var window = _mainWindow!;
        var main = _mainViewModel!;
        string modeBefore = main.SettingsVM.LibraryViewMode;
        var games = new List<GameCardViewModel>();
        var madeFavorite = new List<GameCardViewModel>();

        // A walk listed in short: the count, any stop that can't be seen, then the stops (up to 30).
        void Compact(string label, Window w)
        {
            var walk = new StringBuilder();
            Walk(label, w, games, walk);
            var lines = walk.ToString().Split(Environment.NewLine).Where(l => l.Length > 0).ToList();
            report.AppendLine(lines[0]);
            foreach (var l in lines.Skip(1).Where(l => l.Contains("NOT VISIBLE"))) report.AppendLine("  !! " + l.Trim());
            foreach (var l in lines.Skip(1).Take(30)) report.AppendLine(l);
            if (lines.Count > 31) report.AppendLine($"       ... {lines.Count - 31} more");
        }

        try
        {
            window.WindowState = WindowState.Normal;
            window.Show();
            window.Activate();
            window.Width = 1400;
            window.Height = 860;
            main.CurrentSection = NavSection.Library;
            games.AddRange(main.FilteredGames.Cast<GameCardViewModel>());
            report.AppendLine($"{games.Count} games; Show details on hover: {main.SettingsVM.PosterDetailsOnHover}");

            // The first two games as favorites, in memory only (put back in finally), so their stars show and can be tried.
            madeFavorite.AddRange(games.Take(2).Where(g => !g.Game.IsFavorite));
            foreach (var g in madeFavorite) { g.Game.IsFavorite = true; g.RefreshProperties(); }
            foreach (var mode in new[] { SettingsViewModel.ViewModePosterGrid, SettingsViewModel.ViewModeCompactIcons, SettingsViewModel.ViewModeDetailsList })
            {
                main.SettingsVM.LibraryViewMode = mode;
                Settle(window);
                Walk($"Library, {mode}", window, games, report);
                // Down and Right from the view buttons, the way into the games without Tab.
                var page = (LibraryView)window.FindName("LibraryPage");
                var viewButton = Descendants(page).OfType<Button>().First(b => AutomationProperties.GetName(b) == "Poster grid view");
                foreach (var key in new[] { Key.Down, Key.Right, Key.Right, Key.Left })
                {
                    if (key == Key.Down) { Keyboard.Focus(viewButton); Settle(window); }
                    SendTestKey(key);
                    Settle(window);
                    var f = Keyboard.FocusedElement as UIElement;
                    report.AppendLine($"  {key} -> {(f != null ? Describe(f) : "nothing")}");
                }
            }
            // The poster view with details on hover the other way round, since it changes what a card shows.
            main.SettingsVM.LibraryViewMode = SettingsViewModel.ViewModePosterGrid;
            main.SettingsVM.PosterDetailsOnHover = !main.SettingsVM.PosterDetailsOnHover;
            Settle(window);
            Walk($"Library, Poster Grid, Show details on hover {main.SettingsVM.PosterDetailsOnHover}", window, games, report);
            main.SettingsVM.PosterDetailsOnHover = !main.SettingsVM.PosterDetailsOnHover;
            // States that show more on a card or above the games: posters off (the icon card inside the poster
            // view), a selection (the selection bar), the filter flyout open.
            bool postersBefore = main.SettingsVM.UseVerticalPosterArt;
            main.SettingsVM.UseVerticalPosterArt = !postersBefore;
            Settle(window);
            Compact($"Library, Poster Grid, poster art {(!postersBefore ? "on" : "off")}", window);
            main.SettingsVM.UseVerticalPosterArt = postersBefore;
            main.SelectAllVisible();
            Settle(window);
            Compact("Library, Poster Grid, every game selected", window);
            main.ClearSelection();
            var library = (LibraryView)window.FindName("LibraryPage");
            (System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(library.LibraryFilterButton)
                .GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke) as System.Windows.Automation.Provider.IInvokeProvider)?.Invoke();
            PumpDispatcher(TimeSpan.FromSeconds(1));
            if (library.IsFilterFlyoutOpen)
            {
                // The flyout is a popup: its own Tab order, walked from its first control.
                var popupRoot = Keyboard.FocusedElement as UIElement;
                report.AppendLine($"## Library filter flyout open; focus in it: {(popupRoot != null ? Describe(popupRoot) : "nothing")}");
                var seen = new HashSet<object>();
                for (int n = 0; n < 60 && Keyboard.FocusedElement is UIElement f && seen.Add(f); n++)
                {
                    report.AppendLine($"  {n + 1,3}. {Describe(f)}");
                    f.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                    Settle(window);
                }
                SendTestKey(Key.Escape);
                Settle(window);
            }
            else report.AppendLine("## Library filter flyout: did not open");

            // The other pages, each tab on its own: how many stops, any that can't be seen, and the list.
            void Page(NavSection section, Action select, string tab)
            {
                main.CurrentSection = section;
                select();
                PumpDispatcher(TimeSpan.FromSeconds(1));
                Compact($"{section} > {tab}", window);
            }
            Page(NavSection.Tools, () => { }, "Tools");
            Page(NavSection.Activity, () => { }, "Activity");
            foreach (SystemSubSection tab in Enum.GetValues<SystemSubSection>())
                Page(NavSection.System, () => main.SystemVM.CurrentSubSection = tab, tab.ToString());
            foreach (SettingsCategoryTab tab in Enum.GetValues<SettingsCategoryTab>())
                Page(NavSection.Settings, () => main.SettingsVM.SelectedTab = tab, tab.ToString());
            foreach (AboutSubSection tab in Enum.GetValues<AboutSubSection>())
                Page(NavSection.About, () => main.CurrentAboutSection = tab, tab.ToString());
            main.CurrentSection = NavSection.Library;

            var game = games.FirstOrDefault(g => !string.IsNullOrEmpty(g.Game.SteamAppId)) ?? games.First();
            var vm = new GameDetailsViewModel(game.Game, new SteamMetadataService(), new SteamSearchService(),
                editHost: DiagnosticEditHost(), heroArt: new HeroArtService());
            var dialog = new GameDetailsDialog(vm);
            dialog.Show();
            PumpDispatcher(TimeSpan.FromSeconds(40), () => vm.HasDetails && !vm.IsLoading);
            PumpDispatcher(TimeSpan.FromSeconds(3));
            Walk($"Game Details, Overview ({game.Name})", dialog, games, report);
            dialog.ShowSettings();
            PumpDispatcher(TimeSpan.FromSeconds(2));
            Walk($"Game Details, Settings ({game.Name})", dialog, games, report);
            dialog.Close();

            // The other dialogs, each shown on its own (not modally) and walked from the top.
            void Dialog(string label, Window w)
            {
                try
                {
                    w.Owner = null;
                    w.Show();
                    PumpDispatcher(TimeSpan.FromSeconds(1));
                    Compact($"Dialog: {label}", w);
                }
                catch (Exception ex)
                {
                    report.AppendLine($"## Dialog: {label}: FAILED {ex.Message}");
                }
                finally
                {
                    w.Close();
                }
            }
            var candidates = new List<GameCandidate>
            {
                new("DOOM Eternal", @"C:\Games\DOOM Eternal\DOOMEternalx64tk.exe", @"C:\Games\DOOM Eternal", 88400000, 140, "DOOMEternalx64tk.exe"),
                new("DOOM Config Tool", @"C:\Games\DOOM Eternal\tools\DOOMConfig.exe", @"C:\Games\DOOM Eternal\tools", 4500000, 10, @"tools\DOOMConfig.exe"),
            };
            Dialog("Confirm (ModernDialog)", new ModernDialog("Remove from Library", "Remove it?", "Detail", "Remove", "Cancel", DialogIconType.Delete));
            Dialog("Change Category", new QuickInputDialog("Change Category", "Change Category", "Select or enter a category:", "Action", new[] { "Action", "RPG" }, showAdd: true));
            Dialog("Collections", new CollectionsDialog("Collections", "Pick a collection, or type a new one, and press Add.", new[] { new CollectionChoice("Co-op", true), new CollectionChoice("Kids", null) }, _ => null, "No collections yet.", "Not in any collection."));
            Dialog("Change Match, Steam (no attribution)", new GameMatchPickerDialog("Change Steam Match", "Pick the right listing.", "", null,
                (_, _) => Task.FromResult(new MatchPickerResult(new List<MatchPickerHit>(), "Type a name to search."))));
            Dialog("Change Match, RAWG (attribution)", new GameMatchPickerDialog("Change RAWG Match", "Pick the right listing.", "", null,
                (_, _) => Task.FromResult(new MatchPickerResult(new List<MatchPickerHit>(), "Type a name to search.")), "Data from RAWG", "https://rawg.io"));
            Dialog("Edit Tool", new ToolEditDialog(new ToolEntry { Name = "Audit Tool", TargetPath = @"C:\Windows\notepad.exe" }, new[] { "Mods" }, _iconExtractorService));
            Dialog("Edit Theme", new ThemeEditDialog(new ThemeEditorViewModel(TrayTrigger.Services.Theming.ThemeService.Instance.Active, TrayTrigger.Services.Theming.ThemeService.Instance)));
            Dialog("Scan for Games", new ScanForGamesDialog(main, new(), new(), new(), new(), new(), new(), new(), new(), new(), new(), new()));
            Dialog("Welcome", new WelcomeDialog());
            Dialog("Pick the game's exe", new GameCandidatePickerDialog(@"C:\Games\DOOM Eternal", candidates));
            Dialog("Add Folder", new FolderBatchImportDialog(@"C:\Games", candidates, _ => false, isAlreadyScanLocation: false, onIgnoreCandidate: _ => { }));
            Dialog("Update available", new UpdateDialog(new GitHubReleaseInfo { TagName = "v9.9.9", Name = "TrayTrigger v9.9.9", Body = "### Highlights\n- One\n- Two", HtmlUrl = "https://example.invalid",
                Assets = new List<GitHubReleaseAsset> { new() { Name = "TrayTrigger-v9.9.9-Setup.exe", Size = 26588200 } } }));
            if (WhatsNewDialog.CreateForCapture(afterUpdate: true) is { } whatsNew) Dialog("What's new", whatsNew);
            HelpDialog.ShowTopic(null, "general/overview");
            if (Current.Windows.OfType<HelpDialog>().FirstOrDefault() is { } help) Dialog("Help", help);

            File.WriteAllText(outPath, report.ToString());
        }
        catch (Exception ex)
        {
            File.WriteAllText(outPath, report + Environment.NewLine + "FAILED: " + ex);
        }
        finally
        {
            foreach (var g in madeFavorite) { g.Game.IsFavorite = false; g.RefreshProperties(); }
            main.SettingsVM.LibraryViewMode = modeBefore;
            foreach (var (file, bytes) in settingsBefore)
            {
                try
                {
                    if (bytes != null) File.WriteAllBytes(file, bytes);
                    else File.Delete(file);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Could not put {file} back as it was: {ex.Message}");
                }
            }
        }
        ExitApplication();
    }

    private void Walk(string title, Window window, List<GameCardViewModel> games, StringBuilder report)
    {
        window.Activate();
        // From nothing focused, as after a click on the page: focus left in the sidebar would stay where it was.
        Keyboard.ClearFocus();
        Keyboard.Focus(window);
        window.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        Settle(window);
        var seen = new HashSet<DependencyObject>();
        var lines = new List<string>();
        var perGame = new Dictionary<int, int>();
        int total = 0;
        string ended = "came round to the start";
        while (Keyboard.FocusedElement is DependencyObject focused && seen.Add(focused) && total < 600)
        {
            total++;
            object? context = (focused as FrameworkElement)?.DataContext ?? (focused as FrameworkContentElement)?.DataContext;
            int gameIndex = context is GameCardViewModel g ? games.IndexOf(g) : -1;
            if (gameIndex >= 0) perGame[gameIndex] = perGame.GetValueOrDefault(gameIndex) + 1;
            if (gameIndex < 0 || gameIndex < 2)
                lines.Add($"  {total,3}. {(focused is UIElement u ? Describe(u) : DescribeContent(focused))}{(gameIndex >= 0 ? $"  [game {gameIndex}]" : "")}");
            else if (perGame[gameIndex] == 1 && gameIndex == 2)
                lines.Add("       ... (the other games repeat the same stops)");
            var next = new TraversalRequest(FocusNavigationDirection.Next);
            // MoveFocus says false when a views:FocusGroup sends focus on to its selected item, so where focus is now decides.
            if (focused is UIElement ue) ue.MoveFocus(next);
            else if (focused is ContentElement ce) ce.MoveFocus(next);
            if (ReferenceEquals(Keyboard.FocusedElement, focused)) { ended = "Tab went nowhere"; break; }
            Settle(window);
        }
        if (Keyboard.FocusedElement is DependencyObject last && seen.Contains(last) && total > 0)
            ended = $"came back to: {(last is UIElement lu ? Describe(lu) : DescribeContent(last))}";
        int gameStops = perGame.Values.Sum();
        report.AppendLine($"## {title}: {total} stops, {gameStops} on games ({(perGame.Count > 0 ? $"{perGame.Values.Max()} per game" : "none")}), {total - gameStops} others; {ended}");
        foreach (var line in lines) report.AppendLine(line);
    }

    /// <summary>A link inside text (a Hyperlink is not a UIElement): its text and the control it sits in.</summary>
    private static string DescribeContent(DependencyObject element)
    {
        string text = element is System.Windows.Documents.Hyperlink h
            ? AutomationProperties.GetName(h) is { Length: > 0 } n ? n : new System.Windows.Documents.TextRange(h.ContentStart, h.ContentEnd).Text
            : "";
        DependencyObject? up = element;
        while (up is FrameworkContentElement fce) up = fce.Parent;
        string host = up is UIElement host1 ? Describe(host1) : "?";
        return $"{element.GetType().Name} \"{Trim(text)}\" in [{host}]";
    }

    private static string Describe(UIElement element)
    {
        var fe = element as FrameworkElement;
        string label = AutomationProperties.GetName(element);
        if (string.IsNullOrEmpty(label) && element is ContentControl { Content: string s }) label = s;
        if (string.IsNullOrEmpty(label) && element is ContentControl { Content: TextBlock tb }) label = tb.Text;
        if (string.IsNullOrEmpty(label) && fe?.ToolTip is string tip) label = "tip: " + tip;
        if (string.IsNullOrEmpty(label) && element is TextBox box) label = "text: " + box.Text;
        string region = "";
        for (var d = VisualTreeHelper.GetParent(element); d != null; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { Name.Length: > 0 } named && !named.Name.StartsWith("PART_")) { region = named.Name; break; }
        bool hidden = false;
        for (DependencyObject? d = element; d is UIElement u; d = VisualTreeHelper.GetParent(d))
            if (u.Opacity < 0.05 || u.Visibility != Visibility.Visible) { hidden = true; break; }
        return $"{element.GetType().Name} '{fe?.Name}' \"{Trim(label)}\" in {region}{(hidden ? "  (NOT VISIBLE)" : "")}";
    }

    private static string Trim(string? s) => s == null ? "" : s.Length > 60 ? s[..60] + "..." : s.Replace('\n', ' ');
}
#endif
