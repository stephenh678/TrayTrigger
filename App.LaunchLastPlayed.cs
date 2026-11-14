using TrayTrigger.Services;

namespace TrayTrigger;

/// <summary>
/// The launch-last-played hotkey (default Ctrl+Alt+L): plays the game played most recently, the same
/// Play as its card, so a game already running just comes forward. The window is usually out of
/// sight, so when nothing has been played yet the answer is a tray balloon.
/// </summary>
public partial class App
{
    private void OnLastPlayedHotkeyTriggered()
    {
        if (_isShuttingDown || _mainViewModel == null) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_mainViewModel.Library.LaunchLastPlayed()) return;
            const string message = "No game has been played yet, so there is none to launch.";
            LoggingService.Shown("Tray notification", message);
            ShowTrayNotification("TrayTrigger", message);
        });
    }
}
