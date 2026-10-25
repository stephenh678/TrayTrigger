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

/// <summary>The DLSS override from a card, and in a batch.</summary>
public partial class LibraryViewModel
{
    /// <summary>
    /// The right-click menu's DLSS Override tick. The work is handed to a <see cref="DlssCardViewModel"/>
    /// - the same one Edit Game > Performance builds - rather than repeated here: it owns which
    /// settings were captured, which were handed back because something else had changed them, and
    /// which are still TrayTrigger's to undo. A second copy of that reasoning that drifted would
    /// leave overrides on the driver with no record able to take them off again.
    ///
    /// <para>Turning it on searches the game's folder for its DLSS files, so it is not instant and
    /// the status bar says what is happening. Turning it off needs no search: the records already
    /// say what to put back.</para>
    /// </summary>
    private async Task ToggleDlssOverrideAsync(GameCardViewModel card)
    {
        if (card.IsDlssOverrideBusy) return;
        bool turningOn = !card.DlssOverrideOn;
        StatusMessage = turningOn
            ? $"Looking for DLSS in {card.Name}..."
            : $"Restoring NVIDIA's settings for {card.Name}...";

        var (_, line) = await SetDlssOverrideAsync(card, turningOn).ConfigureAwait(true);
        StatusMessage = line;
    }

    /// <summary>What switching one game's DLSS Override came to, for the batch summary.</summary>
    internal enum DlssChange
    {
        /// <summary>The game ended up in the state asked for.</summary>
        Done,
        /// <summary>Turning on, and the game ships no DLSS.</summary>
        NotAvailable,
        /// <summary>The driver refused, or something threw: the game is not in the state asked for.</summary>
        Failed
    }

    /// <summary>
    /// Switches one game's override and returns what happened, with the status-bar line the
    /// single-game menu shows for it. Shared by that menu and the batch menu, so a selection gets
    /// exactly what each game would have got on its own.
    /// </summary>
    /// <param name="recordActivity">False from the batch menu, which records one line for all its games.</param>
    private async Task<(DlssChange Outcome, string Line)> SetDlssOverrideAsync(GameCardViewModel card, bool turnOn, bool recordActivity = true)
    {
        var game = card.Game;
        card.IsDlssOverrideBusy = true;
        try
        {
            // The live record list, mutated in place and saved through persist - exactly how the
            // Edit Game dialog hands it over, so both routes leave the same thing on disk.
            var dlss = new DlssCardViewModel(
                game.ExecutablePath, game.Name, game.DlssSettings,
                // A lambda, not a method group: SaveGamesOnly takes caller-info arguments.
                persist: () => SaveGamesOnly(), overrides: DlssOverrides, probe: DlssProbe, game: game)
            {
                RecordsActivity = recordActivity,
            };

            await dlss.LoadAsync().ConfigureAwait(true);

            // Most games ship no DLSS. Say so rather than leaving a tick that quietly refuses:
            // the same sentence the card in Edit Game shows, so the two never disagree.
            if (turnOn && !dlss.CanEnable)
                return (DlssChange.NotAvailable, $"{card.Name}: {DlssCardViewModel.NotAvailableLine}");

            await dlss.SetOverrideAsync(turnOn).ConfigureAwait(true);

            card.NotifyDlssOverrideChanged();
            var outcome = card.DlssOverrideOn == turnOn ? DlssChange.Done : DlssChange.Failed;
            string line = dlss.Status is { Length: > 0 } problem
                ? $"{card.Name}: {problem}"
                : card.DlssOverrideOn
                    ? $"DLSS Override is on for {card.Name}."
                    : $"DLSS Override is off for {card.Name}; NVIDIA's settings are back as they were.";
            return (outcome, line);
        }
        catch (Exception ex)
        {
            LoggingService.Error("Dlss", $"Switching the DLSS Override for '{game.Name}' from the card menu failed: {ex.Message}", ex);
            return (DlssChange.Failed, $"{card.Name}: the DLSS Override could not be changed - see the log.");
        }
        finally
        {
            card.IsDlssOverrideBusy = false;
            card.NotifyDlssOverrideChanged();
        }
    }

    private bool _isBatchDlssRunning;

    /// <summary>
    /// The batch menu's DLSS Override. All-or-nothing like the other batch toggles: a mixed
    /// selection turns it on for every game, and only a selection where every game already has it
    /// turns it off - <see cref="BatchAllDlssOverrideOn"/> is the check mark, so what the click
    /// will do is on screen first.
    ///
    /// <para>One game at a time, through the same <see cref="SetDlssOverrideAsync"/> the
    /// single-game menu uses: turning it on searches each game's folder, and the status bar says
    /// which game it is on and how far through it is, as the batch metadata refresh does. Games
    /// already in the asked-for state are skipped, and so is a game that isn't installed when
    /// turning on, since there is no folder to search. The closing line says what didn't change
    /// and why: no DLSS in the game, not installed, or the driver refused.</para>
    /// </summary>
    public async Task BatchToggleDlssOverrideAsync()
    {
        var cards = SelectedCards;
        if (cards.Count == 0 || _isBatchDlssRunning) return;

        bool turnOn = !BatchAllDlssOverrideOn;
        // A game being switched from its own menu right now is left to that.
        var todo = cards.Where(c => c.DlssOverrideOn != turnOn && !c.IsDlssOverrideBusy).ToList();
        var notInstalled = turnOn ? todo.Where(c => c.IsUnavailable).ToList() : new List<GameCardViewModel>();
        todo = todo.Except(notInstalled).ToList();
        int alreadyThere = cards.Count - todo.Count - notInstalled.Count;

        _isBatchDlssRunning = true;
        CommandManager.InvalidateRequerySuggested();
        var noDlss = new List<string>();
        var failed = new List<string>();
        var changedGames = new List<GameEntry>();
        int changed = 0;
        try
        {
            for (int i = 0; i < todo.Count; i++)
            {
                var card = todo[i];
                StatusMessage = turnOn
                    ? $"Turning on DLSS Override ({i + 1} of {todo.Count}): looking for DLSS in {card.Name}..."
                    : $"Turning off DLSS Override ({i + 1} of {todo.Count}): restoring NVIDIA's settings for {card.Name}...";

                var (outcome, line) = await SetDlssOverrideAsync(card, turnOn, recordActivity: false).ConfigureAwait(true);
                switch (outcome)
                {
                    case DlssChange.Done: changed++; changedGames.Add(card.Game); break;
                    case DlssChange.NotAvailable: noDlss.Add(card.Name); break;
                    default:
                        failed.Add(card.Name);
                        LoggingService.Warn("Dlss", $"Batch DLSS Override: {line}");
                        break;
                }
            }
        }
        finally
        {
            _isBatchDlssRunning = false;
            CommandManager.InvalidateRequerySuggested();
            NotifySelectionChanged();
        }

        PerformanceActivity.DlssChanged(changedGames, turnOn);
        LoggingService.Info("Dlss", $"Batch DLSS Override {(turnOn ? "on" : "off")}: {changed} changed, {alreadyThere} already {(turnOn ? "on" : "off")}, {noDlss.Count} without DLSS, {notInstalled.Count} not installed, {failed.Count} failed.");
        StatusMessage = BatchDlssSummary(turnOn, changed, alreadyThere, noDlss, notInstalled.Select(c => c.Name).ToList(), failed);
    }

    /// <summary>The status-bar line a batch DLSS Override ends on: what changed, then each reason
    /// some games didn't, naming up to three of them.</summary>
    internal static string BatchDlssSummary(bool turnOn, int changed, int alreadyThere,
        IReadOnlyList<string> noDlss, IReadOnlyList<string> notInstalled, IReadOnlyList<string> failed)
    {
        static string Games(int n) => n == 1 ? "1 game" : $"{n} games";
        static string Names(IReadOnlyList<string> names) => names.Count <= 3
            ? string.Join(", ", names)
            : $"{string.Join(", ", names.Take(3))} and {names.Count - 3} more";

        var parts = new List<string>();
        if (changed > 0)
            parts.Add(turnOn
                ? $"DLSS Override is on for {Games(changed)}."
                : $"DLSS Override is off for {Games(changed)}; NVIDIA's settings are back as they were.");
        else if (alreadyThere == 0)
            parts.Add(turnOn ? "DLSS Override wasn't turned on for any game." : "DLSS Override wasn't turned off for any game.");
        if (alreadyThere > 0)
            parts.Add($"{Games(alreadyThere)} already had it {(turnOn ? "on" : "off")}.");
        if (noDlss.Count > 0)
            parts.Add($"No DLSS in {Names(noDlss)}.");
        if (notInstalled.Count > 0)
            parts.Add($"Not installed: {Names(notInstalled)}.");
        if (failed.Count > 0)
            parts.Add($"Couldn't change {Names(failed)} - see the log.");
        return string.Join(" ", parts);
    }

    /// <summary>Puts every game's DLSS Override back and returns a line for the status bar.</summary>
    public string RestoreAllDlssOverrides()
    {
        var result = DlssOverrides.RestoreAll(Games.Select(c => c.Game).ToList());
        if (result.Games == 0) return "No game has the DLSS Override on.";

        SaveGamesOnly();
        string games = result.Games == 1 ? "1 game" : $"{result.Games} games";
        int putBack = result.Games - result.Failed;
        if (putBack > 0)
            ActivityService.Add(ActivityLevel.Change, $"Restore All turned DLSS Override off for {(putBack == 1 ? "1 game" : $"{putBack} games")}",
                detail: "NVIDIA's settings are back as they were.", groupKey: $"dlss.restoreall|{DateTime.UtcNow.Ticks}");
        return result.Failed == 0
            ? $"DLSS Override put back for {games}."
            : $"DLSS Override put back for {result.Games - result.Failed} of {games}. The rest could not be changed - see the log.";
    }

    /// <summary>
    /// Puts back the driver settings TrayTrigger wrote for games that are now gone for good. The
    /// ownership records leave with the library entry, and they are the only thing that can undo
    /// the override - so without this, removing a game would leave its settings, and any profile
    /// TrayTrigger created for it, in NVIDIA's database with nothing able to take them out again.
    ///
    /// <para>Here rather than in <see cref="RemoveGames"/>, for the same reason the cached files
    /// are deleted here: inside the undo window the game may yet come back, records and all.</para>
    ///
    /// <para>A setting another library entry also holds a record for - two entries for the same
    /// executable - is left alone. It is still in use, and that entry's own undo will restore it.</para>
    /// </summary>
    private void UndoDlssOverridesOf(IReadOnlyCollection<GameEntry> removed)
    {
        var stillHeld = Games
            .SelectMany(c => c.Game.DlssSettings)
            .Select(r => (r.ApplicationName.ToLowerInvariant(), r.SettingId))
            .ToHashSet();

        foreach (var game in removed)
        {
            var records = game.DlssSettings
                .Where(r => !stillHeld.Contains((r.ApplicationName.ToLowerInvariant(), r.SettingId)))
                .ToList();
            if (records.Count == 0) continue;

            try
            {
                var undone = DlssOverrides.Undo(records);
                game.DlssSettings.Clear();
                game.DlssSettings.AddRange(undone.Records);

                // Settings something else has since changed are no longer TrayTrigger's to undo, so
                // only a failure leaves anything behind that should not be there.
                var failed = undone.Details.Where(d => d.Outcome == DlssSettingOutcome.Failed).ToList();
                if (!undone.Succeeded || failed.Count > 0)
                {
                    string why = undone.Error ?? failed.FirstOrDefault()?.Error ?? "the driver refused";
                    string ids = string.Join(", ", undone.Records.Select(r => $"0x{r.SettingId:X8}"));
                    LoggingService.Warn("Library", $"Could not put back the DLSS override for removed game '{game.Name}' ({why}). Still set on profile '{records[0].ProfileName}' for {records[0].ApplicationName}: {ids}.");
                    ActivityService.Add(ActivityLevel.Problem, $"{game.Name}: DLSS Override couldn't be put back when it was removed",
                        subject: game.Name,
                        detail: $"NVIDIA's settings for it stay changed, and TrayTrigger no longer has the record to undo them. NVIDIA App can reset the game's profile ({records[0].ProfileName}).",
                        groupKey: $"dlss.removed|{game.Id}");
                }
                else
                {
                    LoggingService.Info("Library", $"Put back the DLSS override for removed game '{game.Name}'.");
                }
            }
            catch (Exception ex)
            {
                // Removing a game must never fail because of this.
                LoggingService.Warn("Library", $"Undoing the DLSS override for removed game '{game.Name}' threw: {ex.Message}");
            }
        }
    }
}
