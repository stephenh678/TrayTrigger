using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

/// <summary>
/// How much room each game takes, for the Largest First sort: the same count as Game Details' install size, made once
/// per game in the background and saved with it. One game at a time, on a thread in Windows' background mode so its
/// disk reads give way to everything else, never while a game is running, and only for a game whose saved size is
/// missing or out of date (<see cref="InstallSizeService.NeedsCount"/>).
/// </summary>
public partial class LibraryViewModel
{
    /// <summary>The biggest games first, by <see cref="GameEntry.InstallSizeBytes"/>; games not counted yet last.</summary>
    public const string SortLargestFirst = "Largest First (Size on Disk)";

    /// <summary>How long a pass waits before it starts: after startup, so the window and the library come first, and
    /// after a game ends, so it has finished writing.</summary>
    internal static TimeSpan InstallSizeStartDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often a pass that is waiting for a game to end looks again.</summary>
    private static readonly TimeSpan WhileAGameRuns = TimeSpan.FromSeconds(30);

    private int _sizePassRunning;
    private volatile bool _sizePassAgain;

    /// <summary>Counts the sizes that are missing or out of date, on a thread of its own. Asked again while one runs,
    /// that pass goes round once more at the end instead of a second starting.</summary>
    internal void CountInstallSizesInBackground()
    {
        if (Interlocked.Exchange(ref _sizePassRunning, 1) == 1)
        {
            _sizePassAgain = true;
            return;
        }
        var thread = new Thread(RunInstallSizePass) { IsBackground = true, Name = "Install sizes", Priority = ThreadPriority.Lowest };
        thread.Start();
    }

    private void RunInstallSizePass()
    {
        try
        {
            using var background = InstallSizeService.BackgroundIo();
            do
            {
                _sizePassAgain = false;
                Thread.Sleep(InstallSizeStartDelay);
                CountOutOfDateSizes();
            }
            while (_sizePassAgain);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("InstallSize", $"Counting the library's install sizes stopped: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _sizePassRunning, 0);
            // Asked again in the moment between the last round and here.
            if (_sizePassAgain) CountInstallSizesInBackground();
        }
    }

    private void CountOutOfDateSizes()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;

        List<GameCardViewModel> due = dispatcher.Invoke(() =>
            Games.Where(c => InstallSizeService.NeedsCount(c.Game, DateTime.UtcNow)).ToList());
        if (due.Count == 0) return;

        LoggingService.Verbose("InstallSize", $"Counting the install size of {due.Count} game(s) in the background.");
        int counted = 0;
        foreach (var card in due)
        {
            var game = card.Game;
            // Never while a game is running: its disk is the game's.
            while (_launcherService.GetActiveSessions().Count > 0) Thread.Sleep(WhileAGameRuns);

            long? bytes = InstallSizeService.GameRoot(game) is { } root ? InstallSizeService.Measure(root) : null;
            var countedUtc = DateTime.UtcNow;
            dispatcher.Invoke(() =>
            {
                game.InstallSizeBytes = bytes;
                game.InstallSizeCountedUtc = countedUtc;
                card.NotifyInstallSizeChanged();
            });
            counted++;
        }

        dispatcher.Invoke(() =>
        {
            SaveGamesOnly();
            if (SelectedSortOption == SortLargestFirst) ApplySort();
        });
        LoggingService.Verbose("InstallSize", $"Counted the install size of {counted} game(s).");
    }
}
