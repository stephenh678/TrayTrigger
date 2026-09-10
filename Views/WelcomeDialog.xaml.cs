using System.Windows;
using TrayTrigger.Services;

namespace TrayTrigger.Views;

/// <summary>
/// One-time welcome shown the first time the window is actually displayed on a fresh install -
/// see MainWindow.MaybeShowWelcomePrompt. Styled after the in-app Help window (icon badge,
/// breadcrumb, title) since both are "here's what TrayTrigger is" framing, but with a single
/// static body rather than HelpDialog's dynamically loaded topic content.
/// </summary>
public partial class WelcomeDialog : Window
{
    public WelcomeDialog()
    {
        InitializeComponent();
        WindowThemeService.PrepareForFirstShow(this);

        Loaded += (s, e) =>
        {
            WindowThemeService.CenterOverOwner(this);
            Activate();
        };
    }

    private void OnGetStartedClick(object sender, RoutedEventArgs e) => Close();
}
