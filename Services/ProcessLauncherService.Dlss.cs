using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>Watching a game for the DLSS settings the player chose, and applying them again if the game rewrites them.</summary>
public partial class ProcessLauncherService
{
    private void StartDlssObservation(ActiveGameSession session, Process process)
    {
        var game = session.Game;
        if (game.DlssSettings.Count == 0) return;

        // Once, not per tick: what the game ships cannot change while it is running, and the scan
        // walks the whole install folder.
        // Beside the renderer the override was written for - the same folder the card reads, and
        // the only one there is for a game launched by link.
        string renderer = game.DlssSettings[0].ExecutablePath;
        string? gameVersion = DlssProbeService.OldestVersion(DlssProbeService.FindShippedRuntimes(
            DlssProbeService.DlssSearchRoot(DlssProbeService.IsFilePath(renderer) ? renderer : game.ExecutablePath, game.WorkingDirectory) ?? string.Empty));

        int ticks = 0;
        string key = game.Id;

        Poller? self = null;
        self = new Poller(DlssObserveInterval, () =>
        {
            ticks++;

            // The renderer by name first: the tracked process may be a launcher, and may be gone
            // by now with the game still running.
            using var rendererProcess = FindRendererProcess(renderer);
            var target = rendererProcess ?? process;

            bool exited;
            try { exited = target.HasExited; } catch { /* a process that cannot be asked has exited */ exited = true; }

            string? note = null;
            var lastRun = exited ? null
                : DlssProbeService.ReadLastRun(DlssProbeService.ScanLoadedModules(target, out note), gameVersion);

            // Keep waiting only while there is still a chance of seeing something.
            if (lastRun == null && !exited && ticks < DlssObserveMaxTicks) return true;

            // Nothing seen is nothing to record: whatever an earlier run found stays.
            if (lastRun != null)
            {
                game.DlssLastRun = lastRun;
                PersistLibrary?.Invoke();
                string loaded = string.Join(", ", lastRun.FromNvidia.Select(v => v + " from NVIDIA").Concat(lastRun.FromGame.Select(v => v + " from the game's own files")));
                LoggingService.Info("Dlss", $"'{game.Name}' loaded DLSS {loaded}.");
                LaunchRecord.Note(game.Id, LaunchRecord.Game, $"DLSS {loaded}");
            }
            else
            {
                // Once per launch, and only for a game with the override on: it is the one thing
                // that says why the card has no Last run line.
                LoggingService.Info("Dlss", $"'{game.Name}': no DLSS runtime seen in {SafeProcess.Describe(target)} after {ticks} look(s){(exited ? " - the process had exited" : "")}. {note}");
                LaunchRecord.Note(game.Id, LaunchRecord.Game, "DLSS Override is on, but no DLSS was seen loading");
            }

            // Only this poller's own registration: a stub handoff starts a second one under the
            // same game, and removing by key alone would drop that one instead.
            _dlssObservers.TryRemove(new KeyValuePair<string, Poller>(key, self!));
            return false;
        });

        // A stub handoff attaches twice. The earlier poller is watching a process that is gone,
        // and would record "not running" for a game that is.
        var poller = self;
        _dlssObservers.AddOrUpdate(key, poller, (_, previous) =>
        {
            previous.Stop();
            return poller;
        });
    }

    /// <summary>Games whose DLSS problem has been noted in Activity &amp; History this run. Another app's
    /// change, or a refusing driver, stays that way launch after launch; once a run is enough.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _dlssProblemNoted = new(StringComparer.Ordinal);

    /// <summary>
    /// Puts the DLSS settings back if something reverted them since they were applied - NVIDIA App
    /// reverts overrides on games it does not list, whenever it starts. Does nothing for a game
    /// TrayTrigger has never applied to, which is almost every game, so the common path is a single
    /// list check.
    /// </summary>
    private void ReapplyDlssSettings(GameEntry game)
    {
        if (game.DlssSettings.Count == 0) return;

        try
        {
            var result = DlssOverrides.Reapply(game.DlssSettings);

            if (result.HadForeignChanges)
            {
                // Something else has set these since. Nothing is written - not this launch, and not
                // the next, for as long as it stays that way. The card's switch reads off.
                LoggingService.Info("Launcher", $"DLSS settings for '{game.Name}' were changed by something else; TrayTrigger left them alone.");
                if (_dlssProblemNoted.TryAdd(game.Id, 0))
                    ActivityService.Add(ActivityLevel.Problem, $"{game.Name}: DLSS Override was changed by another app, so TrayTrigger left it alone",
                        subject: game.Name, detail: "NVIDIA App or another tool set it since. Switch DLSS Override off and on in Edit Game to take it back.",
                        groupKey: $"dlss.foreign|{game.Id}");
            }
            else if (!result.Succeeded)
            {
                LoggingService.Warn("Launcher", $"Could not re-apply DLSS settings for '{game.Name}': {result.Error ?? "the driver refused"}.");
                if (_dlssProblemNoted.TryAdd(game.Id, 0))
                    ActivityService.Add(ActivityLevel.Problem, $"{game.Name}: the NVIDIA driver refused DLSS Override",
                        subject: game.Name, detail: result.Error, groupKey: $"dlss.refused|{game.Id}");
            }
            else if (!result.WasAlreadyCorrect)
            {
                // Recreating a profile marks the records, so undo can remove it again.
                PersistLibrary?.Invoke();
                _dlssProblemNoted.TryRemove(game.Id, out _);
                // Upkeep doing its job: logged, not in Activity & History.
                LoggingService.Info("Launcher", $"Re-applied DLSS settings for '{game.Name}' before launch.");
            }
        }
        catch (Exception ex)
        {
            // A launch must never fail because of this. The override being stale is a worse
            // outcome than a broken launch only in theory.
            LoggingService.Warn("Launcher", $"Re-applying DLSS settings for '{game.Name}' failed: {ex.Message}");
        }
    }
}
