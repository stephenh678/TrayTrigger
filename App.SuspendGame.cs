using System.Threading.Tasks;
using TrayTrigger.Services;

namespace TrayTrigger;

/// <summary>
/// Suspend and resume from the hotkey (default Ctrl+Alt+P) and the tray's Now Playing menu. The
/// work is in <see cref="ProcessLauncherService.SuspendGame"/>; this decides what to say. The
/// window is usually out of sight - the player is in a game - so the answer is a tray balloon,
/// which Windows holds back while a full-screen game has the screen and shows when it's left.
/// </summary>
public partial class App
{
    private void OnSuspendHotkeyTriggered()
    {
        if (_isShuttingDown) return;
        Task.Run(() => ReportSuspendOutcome(_launcherService.ToggleSuspendFromHotkey(), fromHotkey: true));
    }

    private void SuspendFromTray(string gameId) =>
        Task.Run(() => ReportSuspendOutcome(_launcherService.SuspendGame(gameId), fromHotkey: false));

    private void ResumeFromTray(string gameId) =>
        Task.Run(() => ReportSuspendOutcome(_launcherService.ResumeGame(gameId), fromHotkey: false));

    /// <summary>
    /// A resume says nothing: the game coming back to the front says it. A suspend says how to
    /// get back, and anything that didn't happen says why.
    /// </summary>
    private void ReportSuspendOutcome(ProcessLauncherService.SuspendOutcome outcome, bool fromHotkey)
    {
        if (outcome.Result == ProcessLauncherService.SuspendResult.Resumed) return;

        string message = outcome.Message;
        if (outcome.Result == ProcessLauncherService.SuspendResult.Suspended)
        {
            string? hotkey = _mainViewModel?.Settings.SuspendGameHotkey;
            message += string.IsNullOrWhiteSpace(hotkey)
                ? " Resume it from the tray menu."
                : $" Press {hotkey} again, or use the tray menu, to resume it.";
        }
        else if (!fromHotkey && outcome.Result == ProcessLauncherService.SuspendResult.NoSession)
        {
            // The tray item belonged to a session that ended a moment ago; the menu is rebuilt.
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            LoggingService.Shown("Tray notification", message);
            ShowTrayNotification("TrayTrigger", message);
        });
    }
}
