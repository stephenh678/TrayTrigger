#if DEBUG
#pragma warning disable CS8602, CS8604
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;
using TrayTrigger.Views;

namespace TrayTrigger;

/// <summary>
/// --test-layout-audit &lt;out.txt&gt;: shows each page and the main dialogs and lists what does not fit, so the app can be checked at
/// every text size and font (--text-scale, --font) without looking at screens. Two kinds of line:
/// <list type="bullet">
/// <item><b>TRIM / CUT</b>: a TextBlock whose text is wider (or, when it wraps, taller) than the room it was given. TRIM when the
/// block trims on purpose (it shows an ellipsis), CUT when the rest is simply not drawn.</item>
/// <item><b>CLIP</b>: an element WPF arranged smaller than it asked to be, so the layout cuts it to its slot.</item>
/// </list>
/// Some of each are by design (a long path that trims). The audit is for comparing: run at 100% in the default font, then at other sizes
/// and fonts, and look at what is new (tools/Audit-Layout.ps1 does this).
/// </summary>
public partial class App
{
    private void RunLayoutAudit(string outPath)
    {
        _skipSettingsSaveOnExit = true;
        var report = new StringBuilder();
        string font = (Application.Current.Resources["AppFont"] as FontFamily)?.Source ?? "?";
        double scale = AppTypography.OverrideScale ?? AppTypography.SystemPercent / 100.0;
        report.AppendLine($"# Layout audit | text size {scale * 100:0}% | font {font} | theme {TrayTrigger.Services.Theming.ThemeService.Instance.Active.Name}");

        // Switching the library view saves settings.json (the setter saves at once, with the window size this run sets), and this
        // run reads the real settings. The files are kept as they were and put back before the app exits.
        string settingsFile = Path.Combine(_storageService.BaseDirectory, "settings.json");
        var settingsBefore = new[] { settingsFile, settingsFile + ".bak" }.ToDictionary(f => f, f => File.Exists(f) ? File.ReadAllBytes(f) : null);
        string libraryViewBefore = _mainViewModel.SettingsVM.LibraryViewMode;

        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Show();
        _mainWindow.Width = 1280;
        _mainWindow.Height = 860;

        void Audit(string label, DependencyObject root)
        {
            var issues = new SortedSet<string>(StringComparer.Ordinal);
            int texts = 0;
            foreach (var element in Descendants(root).OfType<FrameworkElement>())
            {
                if (!element.IsVisible || element.Opacity < 0.01 || element.ActualWidth <= 0 || element.ActualHeight <= 0) continue;

                if (element is TextBlock block && !string.IsNullOrWhiteSpace(block.Text))
                {
                    texts++;
                    if (TextShortOfRoom(block) is { } detail)
                        issues.Add($"  - {detail.Kind} {Describe(element)} \"{Shorten(block.Text)}\" | {detail.Text}");
                }

                // A picture that fills its space and is cropped to it (the page background) is meant to overflow.
                if (LayoutInformation.GetLayoutClip(element) != null && !(element is Image { Stretch: Stretch.UniformToFill }))
                {
                    var slot = LayoutInformation.GetLayoutSlot(element);
                    double wants = element.RenderSize.Width + element.Margin.Left + element.Margin.Right;
                    double tall = element.RenderSize.Height + element.Margin.Top + element.Margin.Bottom;
                    if (wants > slot.Width + 1 || tall > slot.Height + 1)
                        issues.Add($"  - CLIP {Describe(element)} | wants {wants:0} x {tall:0}, slot {slot.Width:0} x {slot.Height:0}");
                }
            }
            report.AppendLine();
            report.AppendLine($"## {label}: {texts} texts, {issues.Count} issues");
            foreach (string line in issues) report.AppendLine(line);
        }

        void Page(NavSection section, Action? prepare = null, string? note = null)
        {
            _mainViewModel.CurrentSection = section;
            prepare?.Invoke();
            Settle(_mainWindow);
            Audit($"Page {section}{note}", _mainWindow);
        }

        try
        {
            Page(NavSection.Library);
            Page(NavSection.Library, () => _mainViewModel.SettingsVM.LibraryViewMode = SettingsViewModel.ViewModeDetailsList, " (details list)");
            _mainViewModel.SettingsVM.LibraryViewMode = libraryViewBefore;
            Page(NavSection.Tools);
            Page(NavSection.System, () => _mainViewModel.SystemVM.CurrentSubSection = SystemSubSection.All);
            Page(NavSection.Settings, () => _mainViewModel.SettingsVM.SelectedTab = SettingsCategoryTab.All);
            Page(NavSection.About, () => _mainViewModel.CurrentAboutSection = AboutSubSection.All);
            Page(NavSection.Activity);

            void Dialog(string label, Window dialog)
            {
                dialog.Owner = null;
                dialog.Show();
                Settle(dialog);
                Audit($"Dialog {label}", dialog);
                dialog.Close();
            }

            var game = _mainViewModel.Games.FirstOrDefault()?.Game ?? new GameEntry { Name = "Audit Game" };
            // Game Details has two tabs, and only the one on show is laid out: the Overview, then the Settings tab (all of the editor's cards).
            Dialog("Game Details", new GameDetailsDialog(new GameDetailsViewModel(game, new SteamMetadataService(), new SteamSearchService(), editHost: DiagnosticEditHost()) { IsLoading = false }));
            Dialog("Game Details Settings", NewEditGameWindow(game));
            Dialog("Edit Tool", new ToolEditDialog(new ToolEntry { Name = "Audit Tool", TargetPath = @"C:\Windows\notepad.exe" }, new[] { "Mods" }, _iconExtractorService));
            var themeEditor = new ThemeEditorViewModel(TrayTrigger.Services.Theming.ThemeService.Instance.Active, TrayTrigger.Services.Theming.ThemeService.Instance);
            Dialog("Edit Theme", new ThemeEditDialog(themeEditor));
            // The dialog opens the preview the way it was last left, so it is opened after the dialog is made.
            var themeEditorWithPreview = new ThemeEditorViewModel(TrayTrigger.Services.Theming.ThemeService.Instance.Active, TrayTrigger.Services.Theming.ThemeService.Instance);
            var previewDialog = new ThemeEditDialog(themeEditorWithPreview);
            themeEditorWithPreview.IsPreviewOpen = true;
            Dialog("Edit Theme (preview open)", previewDialog);
            Dialog("Scan for Games", new ScanForGamesDialog(_mainViewModel, new(), new(), new(), new(), new(), new(), new(), new()));
            Dialog("Welcome", new WelcomeDialog());
            Dialog("Confirm", new ModernDialog("Remove game", "Remove this game from your library?", "This only removes it from TrayTrigger. The game's files are not touched.", "Remove", "Cancel", DialogIconType.Warning));
            Dialog("Quick input", new QuickInputDialog("Rename", "Rename the category", "Name", "Favorites", null));

            File.WriteAllText(outPath, report.ToString());
            Console.WriteLine(report.ToString());
        }
        finally
        {
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

    /// <summary>Lets layout, bindings and pictures that load on idle finish before the screen is read.</summary>
    private void Settle(Window window)
    {
        window.UpdateLayout();
        Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        window.UpdateLayout();
    }

    private readonly record struct ShortOfRoom(string Kind, string Text);

    /// <summary>
    /// Whether this text needs more room than the block has: wider when it does not wrap, taller when it does. Measured with the block's own
    /// font and size, the way WPF lays the text out. TRIM when the block shows an ellipsis on purpose, CUT when it does not.
    /// </summary>
    private static ShortOfRoom? TextShortOfRoom(TextBlock block)
    {
        double width = block.ActualWidth - block.Padding.Left - block.Padding.Right;
        double height = block.ActualHeight - block.Padding.Top - block.Padding.Bottom;
        var typeface = new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch);
        var formatted = new FormattedText(block.Text, CultureInfo.CurrentUICulture, block.FlowDirection, typeface, block.FontSize, Brushes.Black,
            VisualTreeHelper.GetDpi(block).PixelsPerDip);
        if (block.LineHeight > 0 && !double.IsNaN(block.LineHeight)) formatted.LineHeight = block.LineHeight;
        string kind = block.TextTrimming != TextTrimming.None ? "TRIM" : "CUT";

        if (block.TextWrapping == TextWrapping.NoWrap)
            return formatted.Width > width + 1 ? new ShortOfRoom(kind, $"needs {formatted.Width:0} wide, has {width:0}") : null;

        formatted.MaxTextWidth = Math.Max(1, width);
        return formatted.Height > height + 1 ? new ShortOfRoom(kind, $"needs {formatted.Height:0} tall, has {height:0}") : null;
    }

    /// <summary>Where an element is, in words that stay the same from run to run: its type, its x:Name if it has one, and the three elements above it.</summary>
    private static string Describe(FrameworkElement element)
    {
        var above = new List<string>();
        DependencyObject? parent = VisualTreeHelper.GetParent(element);
        while (parent != null && above.Count < 3)
        {
            if (parent is FrameworkElement fe) above.Add(string.IsNullOrEmpty(fe.Name) ? fe.GetType().Name : fe.Name);
            parent = VisualTreeHelper.GetParent(parent);
        }
        string name = string.IsNullOrEmpty(element.Name) ? string.Empty : " #" + element.Name;
        return $"{element.GetType().Name}{name} in {string.Join("<", above)}";
    }

    private static string Shorten(string text)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ');
        return text.Length <= 48 ? text : text[..48] + "...";
    }
}
#endif
