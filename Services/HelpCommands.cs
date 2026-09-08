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
    public static ICommand ShowTopic { get; } = new RelayCommand(p =>
    {
        if (p is string topicId && !string.IsNullOrWhiteSpace(topicId))
        {
            HelpDialog.ShowTopic(WindowHelper.ActiveOwner(), topicId);
        }
    });
}
