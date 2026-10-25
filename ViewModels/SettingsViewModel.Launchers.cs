using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.Views;

namespace TrayTrigger.ViewModels;

/// <summary>Settings > Scan for Games: the launcher integration toggles and the choices made at first run.</summary>
public partial class SettingsViewModel
{
    // --- Launcher integration toggles ---
    // Each toggle gates only *discovery* ("Scan for Games" and the startup auto-scan) for its
    // platform. Games already in the library keep launching through their launcher and keep their
    // badge regardless - so turning one off asks whether to also remove that platform's games
    // (see SetIntegrationEnabled), rather than silently leaving them or silently deleting them.

    public bool SteamIntegrationEnabled
    {
        get => _settings.SteamIntegrationEnabled;
        set => SetIntegrationEnabled(DetectedLauncher.Steam, "Steam", _settings.SteamIntegrationEnabled, value, v => _settings.SteamIntegrationEnabled = v);
    }

    /// <summary>
    /// Unlike SteamIntegrationEnabled, this has no matching ScanLocations sync to trigger - GOG
    /// has no library-folder concept (every installed game's path is already pinned in its own
    /// registry entry), so this toggle alone gates whether "Scan for Games" looks for GOG titles.
    /// See ImportCoordinator.ScanForGamesAsync.
    /// </summary>
    public bool GogIntegrationEnabled
    {
        get => _settings.GogIntegrationEnabled;
        set => SetIntegrationEnabled(DetectedLauncher.Gog, "GOG", _settings.GogIntegrationEnabled, value, v => _settings.GogIntegrationEnabled = v);
    }

    /// <summary>Same no-scan-location-needed reasoning as GogIntegrationEnabled.</summary>
    public bool EaIntegrationEnabled
    {
        get => _settings.EaIntegrationEnabled;
        set => SetIntegrationEnabled(DetectedLauncher.Ea, "EA", _settings.EaIntegrationEnabled, value, v => _settings.EaIntegrationEnabled = v);
    }

    /// <summary>Same no-scan-location-needed reasoning as GogIntegrationEnabled.</summary>
    public bool EpicIntegrationEnabled
    {
        get => _settings.EpicIntegrationEnabled;
        set => SetIntegrationEnabled(DetectedLauncher.Epic, "Epic", _settings.EpicIntegrationEnabled, value, v => _settings.EpicIntegrationEnabled = v);
    }

    /// <summary>Same no-scan-location-needed reasoning as GogIntegrationEnabled.</summary>
    public bool UbisoftIntegrationEnabled
    {
        get => _settings.UbisoftIntegrationEnabled;
        set => SetIntegrationEnabled(DetectedLauncher.Ubisoft, "Ubisoft", _settings.UbisoftIntegrationEnabled, value, v => _settings.UbisoftIntegrationEnabled = v);
    }

    /// <summary>Same no-scan-location-needed reasoning as GogIntegrationEnabled.</summary>
    public bool XboxIntegrationEnabled
    {
        get => _settings.XboxIntegrationEnabled;
        set => SetIntegrationEnabled(DetectedLauncher.Xbox, "Xbox", _settings.XboxIntegrationEnabled, value, v => _settings.XboxIntegrationEnabled = v);
    }

    /// <summary>Same no-scan-location-needed reasoning as GogIntegrationEnabled.</summary>
    public bool BattleNetIntegrationEnabled
    {
        get => _settings.BattleNetIntegrationEnabled;
        set => SetIntegrationEnabled(DetectedLauncher.BattleNet, "Battle.net", _settings.BattleNetIntegrationEnabled, value, v => _settings.BattleNetIntegrationEnabled = v);
    }

    // True while ApplyDetectedLauncherChoices runs: the first-launch picker is choosing initial
    // defaults on an (effectively) empty library, so the "also remove its games?" prompt and the
    // "run Scan for Games" hint would both be noise there.
    private bool _applyingDetectedLaunchers;

    /// <summary>
    /// Shared body of the five *IntegrationEnabled setters. Persists the flag and raises the
    /// change notification for <paramref name="propertyName"/> (the calling property), then:
    /// on enable, nudges the user to scan (and for Steam, re-syncs its library folders so they
    /// appear immediately); on disable, offers to remove that platform's games from the library,
    /// defaulting to keeping them.
    /// </summary>
    private void SetIntegrationEnabled(DetectedLauncher launcher, string platformName, bool current, bool value, Action<bool> store, [CallerMemberName] string propertyName = "")
    {
        if (current == value) return;

        // Disabling with games present is asked about *before* anything is persisted, so the
        // prompt's Cancel genuinely cancels: nothing changes and the checkbox snaps back.
        bool removeGames = false;
        if (!value && !_applyingDetectedLaunchers)
        {
            var (count, hidden) = _getPlatformGameCount?.Invoke(launcher) ?? (0, 0);
            if (count > 0)
            {
                string plural = count == 1 ? "" : "s";
                // Hidden entries aren't visible on any tab, so say so - otherwise "12 games"
                // can exceed everything the user can see and the sweep looks wrong.
                string hiddenNote = hidden > 0 ? $" ({hidden} hidden)" : string.Empty;
                Window? owner = WindowHelper.ActiveOwner();
                var choice = ModernDialog.PromptChoice(
                    owner,
                    $"Turn Off {platformName} Integration?",
                    $"You have {count} {platformName} game{plural}{hiddenNote} in your library. Remove {(count == 1 ? "it" : "them")} too?",
                    $"Turning off {platformName} integration only stops new {platformName} games from being detected. " +
                    $"Games already in your library stay and keep launching through {platformName} unless you remove them here.\n\n" +
                    "Removing can't be undone: playtime, hotkeys, categories and custom artwork for those games are lost. " +
                    "Installed game files are never deleted either way.",
                    primaryText: "Remove Games",
                    secondaryText: "Keep Games",
                    cancelText: "Cancel");

                if (choice == DialogResultOption.Cancel)
                {
                    // Re-sync the bound checkbox to the unchanged setting.
                    OnPropertyChanged(propertyName);
                    return;
                }
                removeGames = choice == DialogResultOption.Primary;
            }
        }

        store(value);
        OnPropertyChanged(propertyName);
        AutoSaveSettings();
        LoggingService.Info("Settings", $"{platformName} integration {(value ? "enabled" : "disabled")}.");

        if (launcher == DetectedLauncher.Steam)
        {
            if (value)
            {
                // Populate the library-folder rows under the toggle right away rather than
                // waiting for the next startup sync.
                if (ScanLocationService.SyncSteamLocations(_settings, _steamScannerService))
                {
                    AutoSaveSettings();
                }
            }
            RebuildScanLocationRows();
        }

        if (_applyingDetectedLaunchers) return;

        if (value)
        {
            StatusMessage = $"{platformName} integration enabled - run Scan for Games to add your {platformName} titles.";
            return;
        }

        if (!removeGames)
        {
            StatusMessage = $"{platformName} integration disabled - existing {platformName} games kept.";
            return;
        }

        int removed = _removePlatformGames?.Invoke(launcher) ?? 0;
        StatusMessage = $"{platformName} integration disabled - removed {removed} {platformName} game{(removed == 1 ? "" : "s")} from your library.";
    }

    /// <summary>
    /// Applies the user's choice from the first-launch <see cref="LauncherDetectionDialog"/>:
    /// enables only the confirmed launchers, through each toggle's own setter above so Settings
    /// UI bindings and auto-save behave exactly as if the user had flipped them by hand - setting
    /// the underlying AppSettings fields directly (as ImportCoordinator briefly did) leaves an
    /// already-bound checkbox showing its stale old value, since nothing raises the property's
    /// change notification in that case. The setters' interactive prompts are suppressed here.
    /// </summary>
    /// <param name="enabledLaunchers">The launchers the user ticked in the picker.</param>
    /// <param name="probedLaunchers">
    /// The launchers whose detection probe actually completed (see
    /// ImportCoordinator.DetectInstalledLaunchers). A launcher outside this set had its probe throw,
    /// so "absent from the picker" doesn't mean "not installed" - its toggle is left exactly as it
    /// was rather than silently turned off. Null means every launcher was probed.
    /// </param>
    public void ApplyDetectedLauncherChoices(IReadOnlyList<DetectedLauncher> enabledLaunchers, IReadOnlySet<DetectedLauncher>? probedLaunchers = null)
    {
        _applyingDetectedLaunchers = true;
        try
        {
            bool Probed(DetectedLauncher l) => probedLaunchers == null || probedLaunchers.Contains(l);

            if (Probed(DetectedLauncher.Steam)) SteamIntegrationEnabled = enabledLaunchers.Contains(DetectedLauncher.Steam);
            if (Probed(DetectedLauncher.Gog)) GogIntegrationEnabled = enabledLaunchers.Contains(DetectedLauncher.Gog);
            if (Probed(DetectedLauncher.Ea)) EaIntegrationEnabled = enabledLaunchers.Contains(DetectedLauncher.Ea);
            if (Probed(DetectedLauncher.Epic)) EpicIntegrationEnabled = enabledLaunchers.Contains(DetectedLauncher.Epic);
            if (Probed(DetectedLauncher.Ubisoft)) UbisoftIntegrationEnabled = enabledLaunchers.Contains(DetectedLauncher.Ubisoft);
            if (Probed(DetectedLauncher.Xbox)) XboxIntegrationEnabled = enabledLaunchers.Contains(DetectedLauncher.Xbox);
            if (Probed(DetectedLauncher.BattleNet)) BattleNetIntegrationEnabled = enabledLaunchers.Contains(DetectedLauncher.BattleNet);
        }
        finally
        {
            _applyingDetectedLaunchers = false;
        }
    }

    public bool AutoScanForGamesOnStartup
    {
        get => _settings.AutoScanForGamesOnStartup;
        set
        {
            if (_settings.AutoScanForGamesOnStartup != value)
            {
                _settings.AutoScanForGamesOnStartup = value;
                OnPropertyChanged();
                AutoSaveSettings();
            }
        }
    }

    /// <summary>"Notify me when a scan finds new games": a toast as well as the Activity &amp; History line.</summary>
    public bool NotifyWhenScanAddsGames
    {
        get => _settings.NotifyWhenScanAddsGames;
        set
        {
            if (_settings.NotifyWhenScanAddsGames != value)
            {
                _settings.NotifyWhenScanAddsGames = value;
                OnPropertyChanged();
                AutoSaveSettings();
            }
        }
    }

    /// <summary>"Show what's new after an update": the notes window, and the tray notification that offers it.</summary>
    public bool ShowWhatsNew
    {
        get => _settings.ShowWhatsNew;
        set
        {
            if (_settings.ShowWhatsNew != value)
            {
                _settings.ShowWhatsNew = value;
                OnPropertyChanged();
                AutoSaveSettings();
            }
        }
    }
}
