#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
        try
        {
            window.WindowState = WindowState.Normal;
            window.Show();
            window.Activate();
            window.Width = 1400;
            window.Height = 860;
            main.CurrentSection = NavSection.Library;
            var games = main.FilteredGames.Cast<GameCardViewModel>().ToList();
            report.AppendLine($"{games.Count} games; Show details on hover: {main.SettingsVM.PosterDetailsOnHover}");

            foreach (var mode in new[] { SettingsViewModel.ViewModePosterGrid, SettingsViewModel.ViewModeCompactIcons, SettingsViewModel.ViewModeDetailsList })
            {
                main.SettingsVM.LibraryViewMode = mode;
                Settle(window);
                Walk($"Library, {mode}", window, games, report);
            }
            // The poster view with details on hover the other way round, since it changes what a card shows.
            main.SettingsVM.LibraryViewMode = SettingsViewModel.ViewModePosterGrid;
            main.SettingsVM.PosterDetailsOnHover = !main.SettingsVM.PosterDetailsOnHover;
            Settle(window);
            Walk($"Library, Poster Grid, Show details on hover {main.SettingsVM.PosterDetailsOnHover}", window, games, report);
            main.SettingsVM.PosterDetailsOnHover = !main.SettingsVM.PosterDetailsOnHover;

            // The other pages, counted only: how many stops, which can't be seen, and how many are help links.
            foreach (var section in new[] { NavSection.Tools, NavSection.Activity, NavSection.System, NavSection.Settings, NavSection.About })
            {
                main.CurrentSection = section;
                PumpDispatcher(TimeSpan.FromSeconds(2));
                var page = new StringBuilder();
                Walk($"{section} page", window, games, page);
                var pageLines = page.ToString().Split(Environment.NewLine);
                report.AppendLine(pageLines[0]);
                var stops = pageLines.Skip(1).Where(l => l.Length > 0).ToList();
                int help = stops.Count(l => l.Contains("\"Learn more") || l.Contains("\"How ") || l.Contains("Hyperlink"));
                report.AppendLine($"  help links: {help}");
                foreach (var l in stops.Where(l => l.Contains("NOT VISIBLE"))) report.AppendLine(l);
                foreach (var l in stops.Take(14)) report.AppendLine(l);
            }
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

            File.WriteAllText(outPath, report.ToString());
        }
        catch (Exception ex)
        {
            File.WriteAllText(outPath, report + Environment.NewLine + "FAILED: " + ex);
        }
        finally
        {
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
