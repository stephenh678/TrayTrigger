using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.Views;

namespace TrayTrigger.ViewModels;

/// <summary>Playing a game from its card: the checks, the launch itself, and what the player sees when it fails.</summary>
public partial class LibraryViewModel
{
    public void LaunchGame(GameCardViewModel card)
    {
        Window? owner = WindowHelper.ActiveOwner();
        if (card.IsUnavailable)
        {
            // Both markers come from a check made earlier - at startup, or the last scan - so by
            // now the game may have been reinstalled, or the drive it lives on plugged back in.
            // Ask again before refusing: a card that is only dimmed must never be one that cannot
            // be played, and being told to go and find a file that is sitting right there is the
            // most annoying way to get that wrong.
            var was = card.Availability;
            card.Availability = InstalledGameIndex.Recheck(card.Game);
            if (card.IsAvailable)
            {
                LoggingService.Info("GameCard", was == GameAvailability.NotInstalled
                    ? $"'{card.Name}' was marked not installed, but its launcher has it again - launching."
                    : $"'{card.Name}' was marked missing, but '{card.Game.ExecutablePath}' is back - launching.");
            }
            else if (card.IsNotInstalled)
            {
                ShowNotInstalled(card, owner);
                return;
            }
            else
            {
                // From a hotkey or the tray there's no window for the dialog to sit on.
                if (LaunchPopup?.TryShowFailure(LaunchTarget.ForGame(card.Game), "Its executable wasn't found.", "Locate executable...",
                        () => PromptLocateMissingExecutable(card, WindowHelper.ActiveOwner())) != true)
                {
                    PromptLocateMissingExecutable(card, owner);
                }
                return;
            }
        }

        // Play on a game TrayTrigger is already tracking launches nothing: the game's window comes forward (or it
        // resumes). So no "Launching" popup, and no profile line that was not applied again.
        var tracked = _launcherService.TrackedLaunchOf(card.Game.Id);
        if (tracked != ProcessLauncherService.TrackedLaunch.None)
        {
            SwitchToTrackedGame(card, tracked);
            return;
        }

        // A second Play (a double-click, or the hotkey twice) while this one waits out the delay
        // would run the pre-launch scripts and start the game again. OnLaunchDispatched clears it.
        if (!_launchesInFlight.Add(card.Id))
        {
            LoggingService.Verbose("Library", $"Launch of '{card.Name}' ignored: one is already on its way.");
            return;
        }

        // Shown before dispatching, then held for LaunchDispatchDelay via a non-blocking timer
        // before the actual launch happens, so a game that takes focus or goes fullscreen almost
        // at once can't cover the notice before it is read. With the window hidden or behind
        // another app the toast can't be seen at all, so the launch popup stands in for it.
        if (LaunchPopup?.TryBeginLaunch(LaunchTarget.ForGame(card.Game)) != true)
        {
            ShowLaunchToast($"Launching \"{card.Name}\"...",
                seconds: (int)Math.Ceiling((LaunchDispatchDelay + LaunchToastAfterDispatch).TotalSeconds));
        }

        var launchDelayTimer = new DispatcherTimer { Interval = LaunchDispatchDelay };
        launchDelayTimer.Tick += (s, e) =>
        {
            launchDelayTimer.Stop();
            DispatchLaunch(card, owner);
        };
        launchDelayTimer.Start();
    }

    /// <summary>What the status line says when Play finds the game already tracked.</summary>
    internal static string AlreadyTrackedMessage(string name, ProcessLauncherService.TrackedLaunch state) => state switch
    {
        ProcessLauncherService.TrackedLaunch.Suspended => $"Resumed {name}",
        ProcessLauncherService.TrackedLaunch.Starting => $"{name} is still starting",
        _ => $"{name} is already running - switched to it",
    };

    /// <summary>
    /// Play on a game that is starting, running or suspended: the launcher service brings its window forward
    /// (and resumes it), off the UI thread like a real launch, and nothing is dispatched again.
    /// </summary>
    private void SwitchToTrackedGame(GameCardViewModel card, ProcessLauncherService.TrackedLaunch state)
    {
        LoggingService.Info("Library", $"Play on '{card.Name}', which is already {state.ToString().ToLowerInvariant()}: switching to it, not launching.");
        StatusMessage = AlreadyTrackedMessage(card.Name, state);
        _ = Task.Run(() =>
        {
            try { _launcherService.LaunchGame(card.Game, out _, out _); }
            catch (Exception ex) { LoggingService.Warn("Library", $"Switching to '{card.Name}' failed: {ex.Message}"); }
        });
    }

    /// <summary>
    /// What Play does for a game its launcher no longer has installed. Nothing is removed and
    /// nothing is offered to remove: reinstalling is the ordinary next step, and the entry is what
    /// carries this game's playtime, hotkey, scripts and profile through it.
    /// </summary>
    private void ShowNotInstalled(GameCardViewModel card, Window? owner)
    {
        IsLaunchToastVisible = false;
        string launcher = LauncherDisplayName(card.Game);
        string message = $"\"{card.Name}\" isn't installed in {launcher} any more.";

        // Only Steam can be sent to one game's page; the other clients have no such address, so
        // their popup gets the plain acknowledgement rather than a button that opens a library
        // the user then has to search by hand.
        bool canOpenInSteam = card.Game.IsSteamGame && UrlProtocolHelper.IsValidSteamAppId(card.Game.SteamAppId);
        bool shownInPopup = LaunchPopup?.TryShowFailure(
            LaunchTarget.ForGame(card.Game),
            $"It isn't installed in {launcher} any more.",
            canOpenInSteam ? "Open in Steam" : "Open TrayTrigger",
            canOpenInSteam ? () => card.OpenInSteamLibraryCommand.Execute(null) : null) == true;

        StatusMessage = message;
        if (shownInPopup) return;

        // From a hotkey or the tray there may be no window for this to sit on; ModernDialog falls
        // back to the active owner itself.
        if (canOpenInSteam)
        {
            if (ModernDialog.Confirm(owner, "Game Not Installed", message,
                    $"Reinstall it in {launcher} and it goes back to normal on the next scan.\n\nOpen it in Steam now?",
                    "Open in Steam", "Close"))
            {
                card.OpenInSteamLibraryCommand.Execute(null);
            }
            return;
        }

        ModernDialog.ShowInfo(owner, "Game Not Installed", message,
            $"Reinstall it in {launcher} and it goes back to normal on the next scan. Its playtime, hotkey, scripts and performance profile are kept in the meantime.");
    }

    /// <summary>The launcher to name in front of the user for an entry one of them owns.</summary>
    private static string LauncherDisplayName(GameEntry game) =>
        game.IsSteamGame ? "Steam"
        : game.IsXboxGame ? "the Xbox app"
        : game.IsBattleNetGame ? "Battle.net"
        : game.IsGogGame ? "GOG Galaxy"
        : game.IsEaGame ? "the EA app"
        : game.IsEpicGame ? "the Epic Games Launcher"
        : game.IsUbisoftGame ? "Ubisoft Connect"
        : "its launcher";

    private void PromptLocateMissingExecutable(GameCardViewModel card, Window? owner)
    {
        var res = ModernDialog.Confirm(
            owner,
            "Game Executable Missing",
            $"The executable for \"{card.Name}\" was not found.",
            $"Expected location:\n{card.Game.ExecutablePath}\n\nWould you like to locate the game executable now?",
            confirmText: "Locate...",
            cancelText: "Cancel");

        if (res)
        {
            RelocateGame(card);
        }
    }

    private void DispatchLaunch(GameCardViewModel card, Window? owner)
    {
        // LaunchGame can block for a long time - a UAC prompt plus an elevated Defender cmdlet
        // for an Aggressive profile, then a pre-launch script wait - so it runs off the UI
        // thread. The launcher's events already marshal back here on their own.
        _ = Task.Run(() =>
        {
            bool ok;
            string? err;
            bool isMissing;
            try
            {
                ok = _launcherService.LaunchGame(card.Game, out err, out isMissing);
            }
            catch (Exception ex)
            {
                ok = false;
                err = ex.Message;
                isMissing = false;
                LoggingService.Error("Library", $"Launch of '{card.Name}' threw: {ex.Message}", ex);
            }
            RunOnUiThread(() => OnLaunchDispatched(card, owner, ok, err, isMissing));
        });
    }

    private void OnLaunchDispatched(GameCardViewModel card, Window? owner, bool launched, string? err, bool isMissing)
    {
        _launchesInFlight.Remove(card.Id);
        if (launched)
        {
            // No RefreshProperties()/SaveLibrary() here: LaunchGame already raised GameUpdated
            // synchronously (LastPlayed change), which OnGameUpdatedFromLauncher just handled -
            // doing it again here was a redundant second full games.json + settings.json rewrite
            // on every launch.
            StatusMessage = $"Launched {card.Name}";
            LaunchPopup?.LaunchDispatched(card.Game.Id);

            if (_settings.MinimizeOnGameLaunch)
            {
                // Hidden as soon as the launch is dispatched. The launch popup stands in for the
                // in-window notice whenever this option is on (see App's LaunchPopupCoordinator),
                // and ProcessLauncherService gives the game's window focus as soon as it appears,
                // over whatever window Windows handed focus to when this one hid.
                RequestMinimizeToTray?.Invoke();
            }
        }
        else if (isMissing)
        {
            RecordFailedLaunch(card, err, executableMissing: true);
            IsLaunchToastVisible = false;
            card.RefreshProperties();
            if (LaunchPopup?.TryShowFailure(LaunchTarget.ForGame(card.Game), "Its executable wasn't found.", "Locate executable...",
                    () => PromptLocateMissingExecutable(card, WindowHelper.ActiveOwner())) != true)
            {
                PromptLocateMissingExecutable(card, owner);
            }
        }
        else
        {
            RecordFailedLaunch(card, err, executableMissing: false);
            IsLaunchToastVisible = false;
            StatusMessage = $"Error: {err}";
            string message = err ?? "Failed to launch game.";
            if (LaunchPopup?.TryShowFailure(LaunchTarget.ForGame(card.Game), message, "Open TrayTrigger", action: null) != true)
            {
                ModernDialog.ShowWarning(owner, "Launch Error", message);
            }
        }
    }

    /// <summary>A launch that failed outright also goes on Activity &amp; History, so it can be found after the popup is gone.</summary>
    private static void RecordFailedLaunch(GameCardViewModel card, string? err, bool executableMissing)
    {
        var (text, detail) = LaunchProblems.DescribeFailed(card.Name, err, executableMissing);
        ActivityService.Add(ActivityLevel.Problem, text, subject: card.Name, detail: detail,
            groupKey: LaunchProblems.FailedGroupKey(card.Game.Id));
    }

    /// <summary>
    /// Set by MainViewModel: whether the Library section (and therefore its status bar) is the one
    /// currently on screen. Used by <see cref="AnnounceImportResult"/> to decide whether a status
    /// message needs the floating toast to be seen at all.
    /// </summary>
    public Func<bool>? IsLibraryVisible { get; set; }

    /// <summary>
    /// Reports the outcome of an import/scan the way a completion message should be reported:
    /// into the Library status bar always, and additionally as the floating toast when the user is
    /// on another section (Settings, System, About) where that status bar is collapsed - a drop
    /// onto the Settings page or "Scan for Games" from Settings would otherwise finish silently.
    /// </summary>
    public void AnnounceImportResult(string message)
    {
        StatusMessage = message;
        if (IsLibraryVisible?.Invoke() == false)
        {
            ShowLaunchToast(message, icon: "", seconds: 5);
        }
    }

    /// <summary>Shows the floating launch toast for a few seconds - same non-blocking overlay
    /// pattern as the undo-delete toast, but auto-dismissing since there's no action to take.
    /// The toast lives outside the per-section grids, so it renders on every section.</summary>
    /// <summary>The in-window "Launching..." notice for a launch outside the library (a tool), with a game launch's timing.</summary>
    public void ShowLaunchNotice(string message) =>
        ShowLaunchToast(message, seconds: (int)Math.Ceiling((LaunchDispatchDelay + LaunchToastAfterDispatch).TotalSeconds));

    private void ShowLaunchToast(string message, string icon = "", int seconds = 3)
    {
        LoggingService.Shown("Toast", message);
        _launchToastTimer?.Stop();

        LaunchToastIcon = icon;
        LaunchToastMessage = message;
        IsLaunchToastVisible = true;

        _launchToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _launchToastTimer.Tick += (s, e) =>
        {
            _launchToastTimer.Stop();
            IsLaunchToastVisible = false;
        };
        _launchToastTimer.Start();
    }
}
