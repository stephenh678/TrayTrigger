#if DEBUG
#pragma warning disable CS8602, CS8604
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
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
/// --test-automation-names &lt;out.txt&gt;: shows each page and the main dialogs, asks every visible
/// interactive control's automation peer for the name a screen reader would announce, and lists
/// the ones that would be read as nothing, or as a lone icon-font glyph. Runs in process through
/// the peers WPF builds for UI Automation, so it needs no external client.
/// </summary>
public partial class App
{
    private void RunAutomationNameAudit(string outPath)
    {
        _skipSettingsSaveOnExit = true;
        var report = new StringBuilder();
        _mainWindow.Show();
        _mainWindow.Width = 1100;
        _mainWindow.Height = 900;

        void AuditWindow(string label, Visual root)
        {
            int total = 0;
            var missing = new List<string>();
            foreach (var element in Descendants(root).OfType<Control>())
            {
                if (!element.IsVisible) continue;
                if (element is not (ButtonBase or TextBoxBase or ComboBox or ListBoxItem or Slider or PasswordBox or HotkeyRecorderBox)) continue;
                // Controls inside a ComboBox's own template (its toggle button, its text box) are
                // announced as part of the combo box.
                if (element.TemplatedParent is ComboBox) continue;
                total++;
                var peer = UIElementAutomationPeer.CreatePeerForElement(element);
                string name = peer?.GetName() ?? string.Empty;
                bool glyphOnly = name.Length > 0 && name.All(c => c >= '\uE000' && c <= '\uF8FF' || char.IsWhiteSpace(c));
                // An access-key marker left in the name ("_Save") is read out as "underscore save".
                bool accessMarker = name.Contains('_');
                if (string.IsNullOrWhiteSpace(name) || glyphOnly || accessMarker)
                {
                    string hint = string.Join(" ", Descendants(element).OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t) && !t.All(c => c >= '' && c <= '')));
                    missing.Add($"  - {element.GetType().Name} \"{hint}\"");
                }
            }
            report.AppendLine($"{label}: {total} controls, {missing.Count} without a usable name");
            foreach (var m in missing.Distinct()) report.AppendLine(m);
        }

        void Page(NavSection section, Action? prepare = null)
        {
            _mainViewModel.CurrentSection = section;
            prepare?.Invoke();
            _mainWindow.UpdateLayout();
            Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            AuditWindow($"Page {section}", _mainWindow);
        }

        Page(NavSection.Library);
        Page(NavSection.Library, () => _mainViewModel.SettingsVM.LibraryViewMode = SettingsViewModel.ViewModeDetailsList);
        Page(NavSection.Tools);
        Page(NavSection.System, () => _mainViewModel.SystemVM.CurrentSubSection = SystemSubSection.All);
        Page(NavSection.Settings, () => _mainViewModel.SettingsVM.SelectedTab = SettingsCategoryTab.All);
        Page(NavSection.About, () => _mainViewModel.CurrentAboutSection = AboutSubSection.All);

        void Dialog(string label, Window dialog)
        {
            dialog.Owner = null;
            dialog.Show();
            dialog.UpdateLayout();
            Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            AuditWindow($"Dialog {label}", dialog);
            dialog.Close();
        }

        var game = _mainViewModel.Games.FirstOrDefault()?.Game ?? new GameEntry { Name = "Audit Game" };
        Dialog("Edit Game", new GameEditDialog(game, _mainViewModel.Categories, _iconExtractorService, scriptsEnabled: true));
        Dialog("Edit Tool", new ToolEditDialog(new ToolEntry { Name = "Audit Tool", TargetPath = @"C:\Windows\notepad.exe" }, new[] { "Mods" }, _iconExtractorService));
        Dialog("Scan for Games", new ScanForGamesDialog(_mainViewModel, new(), new(), new(), new(), new(), new(), new(), new()));
        var details = new GameDetailsViewModel(game, new SteamMetadataService(), new SteamSearchService()) { IsLoading = false };
        Dialog("Game Details", new GameDetailsDialog(details));
        Dialog("Welcome", new WelcomeDialog());

        File.WriteAllText(outPath, report.ToString());
        Console.WriteLine(report.ToString());
        ExitApplication();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }
}
#endif
