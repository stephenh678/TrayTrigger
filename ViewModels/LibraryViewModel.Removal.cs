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

/// <summary>Removing games and undoing it.</summary>
public partial class LibraryViewModel
{
    public void DeleteGame(GameCardViewModel card)
    {
        Window? owner = WindowHelper.ActiveOwner();
        bool confirmed = ModernDialog.ConfirmDelete(
            owner,
            "Remove from Library",
            $"Are you sure you want to remove \"{card.Name}\" from your library?",
            "This will only remove the shortcut from TrayTrigger. Your installed game files will not be deleted. You can undo this for 10 seconds.",
            confirmText: "Remove",
            cancelText: "Cancel");

        if (!confirmed)
        {
            return;
        }

        RemoveGames(new List<GameCardViewModel> { card });
    }

    /// <summary>
    /// Takes the given cards out of the library as one undoable step (the single-game Remove and
    /// Select mode's batch Remove both land here). The caller has already confirmed. The games'
    /// cached files and fetched details are only deleted once the undo window ends - see
    /// <see cref="FinalizePendingRemoval"/>.
    /// </summary>
    private void RemoveGames(List<GameCardViewModel> cards)
    {
        if (cards.Count == 0) return;

        // Only one pending removal can be undone at a time. If another one is still sitting in
        // its undo window when this new one arrives, it's about to be overwritten and can no
        // longer be undone anyway - finalize its cleanup now instead of leaking it.
        FinalizePendingRemoval();

        // Record positions before anything moves so undo can put every game back where it was.
        foreach (var card in cards.OrderBy(c => Games.IndexOf(c)))
        {
            _lastRemoved.Add((card.Game, Games.IndexOf(card)));
        }
        foreach (var card in cards)
        {
            card.IsSelected = false;
            Games.Remove(card);
        }

        // Before the library is saved without them: from that save on, this file is the only
        // place these games' cached files and DLSS records are written down, and a crash inside
        // the undo window would otherwise strand both. See RecoverPendingRemoval.
        _storageService.SavePendingRemoval(_lastRemoved.Select(r => r.Game));

        RebuildCategories();
        SaveLibrary();
        UpdateHotkeys();

        string what = cards.Count == 1 ? $"\"{cards[0].Name}\"" : $"{cards.Count} games";
        LoggingService.Info("Library", $"Removed {what} from library (undoable for {UndoCountdown.DefaultWindow.TotalSeconds:0}s).");
        var removedLine = LibraryChanges.DescribeRemoved(cards.Select(c => c.Name).ToList(), undoable: true);
        ActivityService.Add(ActivityLevel.Change, removedLine.Text, subject: cards.Count == 1 ? cards[0].Name : null, detail: removedLine.Detail);

        UndoToastMessage = $"Removed {what}";
        IsUndoToastVisible = true;

        _undoTimer.Start(() =>
        {
            // The undo window has expired - the removal is now final.
            LoggingService.Verbose("Library", $"Undo window expired for {what} - deleting cached data.");
            FinalizePendingRemoval();
        });

        StatusMessage = $"Removed {what}";
        NotifyGameCountChanged();
    }

    /// <summary>
    /// Ends the undo window now and deletes the removed games' cached files and fetched details.
    /// Called when the window lapses, when another removal replaces it, before a bulk removal
    /// that could otherwise be raced by an undo, and on app exit - the pending removal lives only
    /// in memory, so exiting inside the window used to leave that data behind for good.
    /// </summary>
    public void FinalizePendingRemoval()
    {
        _undoTimer.Stop();
        IsUndoToastVisible = false;
        if (_lastRemoved.Count == 0) return;

        var removed = _lastRemoved.Select(r => r.Game).ToList();
        _lastRemoved.Clear();
        DeleteRemovedGameData(removed);
        _storageService.DeletePendingRemoval();
    }

    /// <summary>
    /// Finishes a removal that a previous run never got to the end of - a crash, or a kill, inside
    /// the undo window. The library was already saved without those games, so nothing else knows
    /// about their cached files, or holds the DLSS records that can put a driver override back.
    ///
    /// <para>A game that is in the library is skipped: the crash came before the library was saved
    /// without it, or after an undo had put it back, and either way it was never removed.</para>
    /// </summary>
    internal void RecoverPendingRemoval()
    {
        // A removal still inside its window owns the file; its own finalize or undo deals with it.
        if (_lastRemoved.Count > 0) return;

        var pending = _storageService.LoadPendingRemoval();
        if (pending.Count == 0) return;

        var inLibrary = Games.Select(c => c.Game.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gone = pending.Where(g => !inLibrary.Contains(g.Id)).ToList();

        if (gone.Count > 0)
        {
            LoggingService.Warn("Library", $"Found {gone.Count} game(s) a previous run removed without finishing (likely an abnormal exit) - cleaning up after them now.");
            DeleteRemovedGameData(gone);
        }
        _storageService.DeletePendingRemoval();
    }

    /// <summary>Games imported through <paramref name="launcher"/>'s integration, whatever
    /// their current category, and how many of those are hidden from every tab.</summary>
    public (int Total, int Hidden) CountPlatformGames(DetectedLauncher launcher)
    {
        int total = 0, hidden = 0;
        foreach (var g in Games)
        {
            if (!IsPlatformGame(g.Game, launcher)) continue;
            total++;
            if (g.Game.IsHidden) hidden++;
        }
        return (total, hidden);
    }

    /// <summary>True if a library entry is already linked to the platform game
    /// <paramref name="match"/> describes (by platform ID) - the batch dialog's "IN LIBRARY"
    /// check for a candidate that resolved to a platform, where the exe-path comparison can't
    /// work (a Steam entry's ExecutablePath is a steam:// URL, not the exe on disk).</summary>
    public bool IsPlatformGameInLibrary(PlatformMatch? match)
    {
        if (match == null) return false;
        return Games.Any(g => match switch
        {
            { Steam: { } s } => string.Equals(g.Game.SteamAppId, s.AppId, StringComparison.OrdinalIgnoreCase),
            { Gog: { } p } => string.Equals(g.Game.GogGameId, p.GameId, StringComparison.OrdinalIgnoreCase),
            { Ea: { } e } => string.Equals(g.Game.EaContentId, e.ContentId, StringComparison.OrdinalIgnoreCase),
            { Epic: { } p } => string.Equals(g.Game.EpicAppName, p.AppName, StringComparison.OrdinalIgnoreCase),
            { Ubisoft: { } u } => string.Equals(g.Game.UbisoftGameId, u.GameId, StringComparison.OrdinalIgnoreCase),
            { Xbox: { } x } => string.Equals(g.Game.XboxAumid, x.Aumid, StringComparison.OrdinalIgnoreCase),
            { BattleNet: { } b } => string.Equals(g.Game.BattleNetUid, b.Uid, StringComparison.OrdinalIgnoreCase),
            _ => false
        });
    }

    /// <summary>
    /// Bulk-removes every game imported through <paramref name="launcher"/>'s integration - the
    /// "also remove its games" half of turning that integration off in Settings. Unlike
    /// <see cref="DeleteGame"/> this is not undoable (the caller has already confirmed it) and
    /// cleans up cached artwork immediately. Installed game files are never touched.
    /// </summary>
    /// <returns>How many games were removed.</returns>
    public int RemovePlatformGames(DetectedLauncher launcher)
    {
        var toRemove = Games.Where(g => IsPlatformGame(g.Game, launcher)).ToList();
        if (toRemove.Count == 0) return 0;

        // A pending single-game undo would otherwise be able to resurrect a game whose artwork
        // is about to be cleaned up below (if it shares a cached file) - finalize it first.
        FinalizePendingRemoval();

        foreach (var card in toRemove)
        {
            // Same as RemoveGames: a selected card leaving the library must not keep counting
            // towards Select mode's "N selected".
            card.IsSelected = false;
            Games.Remove(card);
        }

        // Persist the removal *before* deleting cached files: a crash between the two would
        // otherwise leave games.json still listing these games with icon/cover paths that point
        // at deleted files, and nothing re-fetches art for an entry whose path is merely broken.
        // Written down first, as in RemoveGames, so a crash between the save and the cleanup is
        // finished on the next start instead of stranding artwork and DLSS overrides.
        _storageService.SavePendingRemoval(toRemove.Select(c => c.Game));
        RebuildCategories();
        SaveLibrary();
        UpdateHotkeys();
        NotifyGameCountChanged();
        NotifySelectionChanged();

        DeleteRemovedGameData(toRemove.Select(c => c.Game).ToList());
        _storageService.DeletePendingRemoval();
        LoggingService.Info("Library", $"Removed {toRemove.Count} {launcher} game(s) from the library after the {launcher} integration was turned off.");
        var removedLine = LibraryChanges.DescribeRemoved(toRemove.Select(c => c.Name).ToList(), undoable: false);
        ActivityService.Add(ActivityLevel.Change, removedLine.Text, subject: toRemove.Count == 1 ? toRemove[0].Name : null,
            detail: $"{removedLine.Detail} {(toRemove.Count == 1 ? "It was" : "They were")} removed with the {launcher} integration.");
        return toRemove.Count;
    }

    /// <summary>
    /// Provenance, not launch method: an entry belongs to a platform's integration when it was
    /// imported (or later linked) by it - <see cref="GameEntry.ImportedFrom"/>. Entries from
    /// before that field existed fall back to the launch flags, which is what set them at the
    /// time; a hand-dropped Steam shortcut marked "launch via Steam" in Edit Game after this
    /// change has ImportedFrom == null and is therefore never swept.
    /// </summary>
    private static bool IsPlatformGame(GameEntry game, DetectedLauncher launcher)
    {
        if (game.ImportedFrom is { } from)
        {
            return from == ToPlatform(launcher);
        }

        return launcher switch
        {
            DetectedLauncher.Steam => game.IsSteamGame,
            DetectedLauncher.Gog => game.IsGogGame,
            DetectedLauncher.Ea => game.IsEaGame,
            DetectedLauncher.Epic => game.IsEpicGame,
            DetectedLauncher.Ubisoft => game.IsUbisoftGame,
            DetectedLauncher.Xbox => game.IsXboxGame,
            DetectedLauncher.BattleNet => game.IsBattleNetGame,
            _ => false
        };
    }

    private static LauncherPlatform ToPlatform(DetectedLauncher launcher) => launcher switch
    {
        DetectedLauncher.Steam => LauncherPlatform.Steam,
        DetectedLauncher.Gog => LauncherPlatform.Gog,
        DetectedLauncher.Ea => LauncherPlatform.Ea,
        DetectedLauncher.Epic => LauncherPlatform.Epic,
        DetectedLauncher.Xbox => LauncherPlatform.Xbox,
        DetectedLauncher.BattleNet => LauncherPlatform.BattleNet,
        _ => LauncherPlatform.Ubisoft
    };

    /// <summary>The pointer is over the undo toast, or it has keyboard focus: hold the window open.</summary>
    public void SetUndoToastHeld(bool held) => _undoTimer.Hold(held);

    public void UndoDelete()
    {
        _undoTimer.Stop();
        IsUndoToastVisible = false;

        if (_lastRemoved.Count == 0) return;

        // Ascending index order so each insert lands at its original slot relative to the
        // games already put back before it.
        var restored = new List<GameCardViewModel>();
        foreach (var (game, index) in _lastRemoved.OrderBy(r => r.Index))
        {
            var card = CreateCardViewModel(game);
            if (index >= 0 && index <= Games.Count)
            {
                Games.Insert(index, card);
            }
            else
            {
                Games.Add(card);
            }
            restored.Add(card);
        }
        _lastRemoved.Clear();

        RebuildCategories();
        SaveLibrary();
        // After the save, not before: a crash in between finds the games back in the library and
        // skips them, where the other order would find nothing and have lost them.
        _storageService.DeletePendingRemoval();
        UpdateHotkeys();
        ApplySort();

        string what = restored.Count == 1 ? $"\"{restored[0].Name}\"" : $"{restored.Count} games";
        LoggingService.Info("Library", $"Undid removal of {what}.");
        ActivityService.Add(ActivityLevel.Change, LibraryChanges.DescribeRestored(restored.Select(c => c.Name).ToList()),
            subject: restored.Count == 1 ? restored[0].Name : null);
        StatusMessage = $"Restored {what} to library.";
        NotifyGameCountChanged();
    }

    /// <summary>
    /// Deletes what removed games leave behind outside games.json: their cached icon and poster
    /// files (including art they no longer pointed at) and their Steam/RAWG details. The files
    /// are always TrayTrigger-owned copies in the icon/cover cache folders - even "Change
    /// Icon"/"Change Poster" copy the chosen file in - so a game's installed files or a user's
    /// original image are never touched. Anything a remaining entry still uses (the same Steam
    /// App ID added twice, a Steam entry plus a local exe entry) is kept; otherwise re-adding the
    /// game would be served cached details pointing at a poster that was just deleted.
    /// </summary>
    private void DeleteRemovedGameData(IReadOnlyCollection<GameEntry> removed)
    {
        if (removed.Count == 0) return;

        var result = GameDataCleanup.DeleteRemovedGameData(
            removed,
            Games.Select(c => c.Game).ToList(),
            _storageService.IconsDirectory,
            _storageService.CoversDirectory,
            HeroArtService.HeroesDirectory);
        LoggingService.Verbose("Library", $"Cleaned up after {removed.Count} removed game(s): {result}.");

        UndoDlssOverridesOf(removed);
    }

    /// <summary>
    /// The DLSS override service. A property rather than a constructor argument, as on
    /// <see cref="ProcessLauncherService"/>; tests substitute a fake backend.
    /// </summary>
    internal DlssOverrideService DlssOverrides { get; set; } = new();

    /// <summary>
    /// The DLSS probe, alongside <see cref="DlssOverrides"/> and for the same reason: substituted
    /// by tests so the right-click menu's override path can be driven without an NVIDIA machine.
    /// Null means the real one.
    /// </summary>
    internal Func<string, string?, DlssProbeService.ProbeResult>? DlssProbe { get; set; }

    /// <summary>How many games have the DLSS Override on. Read by the System page, possibly off the UI thread.</summary>
    public int DlssOverrideGameCount
    {
        get
        {
            try { return Games.Count(c => c.Game.DlssSettings.Count > 0); }
            catch (InvalidOperationException) { return 0; }   // the library changed mid-count
        }
    }
}
