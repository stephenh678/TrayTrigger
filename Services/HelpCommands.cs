using System.Diagnostics;
using System.Windows.Input;
using TrayTrigger.ViewModels;
using TrayTrigger.Views;

namespace TrayTrigger.Services;

/// <summary>
/// App-wide "Learn more" command so any XAML can open a help topic without plumbing a command
/// through its view model:
///   Command="{x:Static services:HelpCommands.ShowTopic}" CommandParameter="tweaks/hags"
/// </summary>
public static class HelpCommands
{
    /// <summary>The community script catalog. Opened from the scripts cards in Settings and Edit Game.</summary>
    public const string ScriptCatalogUrl = "https://github.com/stephenh678/TrayTrigger-Scripts";

    public static ICommand ShowTopic { get; } = new RelayCommand(p =>
    {
        if (p is string topicId && !string.IsNullOrWhiteSpace(topicId))
        {
            HelpDialog.ShowTopic(WindowHelper.ActiveOwner(), topicId);
        }
    });

    /// <summary>
    /// Opens an https URL in the default browser:
    ///   Command="{x:Static services:HelpCommands.OpenUrl}" CommandParameter="https://..."
    /// Only https is accepted, so a XAML typo cannot launch a local program.
    /// </summary>
    public static ICommand OpenUrl { get; } = new RelayCommand(p =>
    {
        if (p is string url && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                LoggingService.Warn("Help", $"Could not open {url}: {ex.Message}");
            }
        }
    });
}
