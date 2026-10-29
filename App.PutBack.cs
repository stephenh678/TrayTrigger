using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using TrayTrigger.Services;
using TrayTrigger.Views;

namespace TrayTrigger;

/// <summary>
/// What a Performance Profile left changed because putting it back needed the user's permission (see
/// <see cref="PerformanceProfileService.CheckPutBacks"/>). TrayTrigger asks in its own words, at start and before it
/// closes, so the Windows prompt that follows is the answer to a click rather than a surprise.
/// </summary>
public partial class App
{
    /// <summary>Set at start when something is waiting; the prompt is shown when the window is, so a start in the tray stays quiet.</summary>
    private bool _putBackPromptPending;

    /// <summary>The exit dialog is open: a second Exit meanwhile doesn't stack another.</summary>
    private bool _exitPromptOpen;

    /// <summary>The Exit commands: asks first when something is still changed, then closes.</summary>
    private void RequestExit()
    {
        if (_isShuttingDown || _exitPromptOpen) return;
        _exitPromptOpen = true;
        try
        {
            if (!ConfirmExit()) return;
        }
        finally
        {
            _exitPromptOpen = false;
        }
        ExitApplication();
    }

    /// <summary>True to go on closing.</summary>
    private bool ConfirmExit()
    {
        var check = _performanceProfileService?.CheckPutBacks();
        if (check == null || check.Items.Count == 0) return true;

        var owner = _mainWindow is { IsVisible: true } ? _mainWindow : null;
        string list = PerformanceProfileService.JoinList(check.Items);
        if (check.GameRunning)
        {
            // Closing puts the game's changes back, and the ones that need permission ask for it: say so first.
            return ModernDialog.Confirm(owner, "A game is still running",
                $"Closing TrayTrigger puts back the changes it made for the game: {list}.",
                "Windows will ask for your permission to do that. Stay open to keep playing.",
                confirmText: "Close and put back", cancelText: "Stay open");
        }

        // Three ways out, with Esc and the title-bar close on the one that changes nothing: staying open.
        // Enter is on "Put back now", the choice that leaves nothing behind.
        var choice = ModernDialog.PromptChoice(owner, "Some settings are still changed",
            $"Your last game's changes weren't all put back: {list}. Put them back before closing?",
            "Windows may ask for your permission. If you close anyway, TrayTrigger offers again the next time it starts, and Activity & History keeps a reminder.",
            primaryText: "Close anyway", secondaryText: "Put back now", cancelText: "Stay open",
            iconType: Views.DialogIconType.Warning);
        if (choice == Views.DialogResultOption.Secondary) _performanceProfileService!.RetryUnrestored();
        return choice != Views.DialogResultOption.Cancel;
    }

    /// <summary>Called once crash recovery has run: remembers whether something is waiting, for <see cref="ShowPutBackPromptIfPending"/>.</summary>
    private void NotePutBackWaitingAtStart(bool windowWillShow)
    {
        if (_performanceProfileService?.CheckPutBacks().Items.Count is not > 0) return;
        _putBackPromptPending = true;
        if (windowWillShow) return;
        // Clicking it opens Activity & History, which shows the reminder, and the prompt with the window.
        ShowTrayNotification("Settings from your last game are still changed",
            "Click to put them back.", opensActivity: true);
    }

    /// <summary>The prompt after start: once, as soon as the window is showing.</summary>
    private void ShowPutBackPromptIfPending()
    {
        if (!_putBackPromptPending || _isShuttingDown) return;
        _putBackPromptPending = false;
        Dispatcher.BeginInvoke(AskToPutBackAtStart, DispatcherPriority.ApplicationIdle);
    }

    private async void AskToPutBackAtStart()
    {
        var service = _performanceProfileService;
        var check = service?.CheckPutBacks();
        // A game started since: what is listed would be its own changes, and putting those back mid-game undoes them.
        if (service == null || check == null || check.GameRunning || check.Items.Count == 0) return;

        var owner = _mainWindow is { IsVisible: true } ? _mainWindow : null;
        bool putBack = ModernDialog.Confirm(owner, "Settings from your last game are still changed",
            $"TrayTrigger couldn't put these back last time: {PerformanceProfileService.JoinList(check.Items)}. Put them back now?",
            "Windows may ask for your permission. If you'd rather not now, Activity & History keeps a reminder with Restore Previous, and TrayTrigger asks again the next time it starts.",
            confirmText: "Put back now", cancelText: "Not now");
        if (!putBack) return;

        bool allBack = await Task.Run(service.RetryUnrestored);
        if (!allBack)
        {
            ModernDialog.ShowWarning(owner, "Not everything was put back",
                $"Still changed: {PerformanceProfileService.JoinList(service.CheckPutBacks().Items)}.",
                "Restore Previous in Activity & History tries again.");
        }
    }
}
