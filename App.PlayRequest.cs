using System;
using System.Linq;
using TrayTrigger.Services;

namespace TrayTrigger;

/// <summary>
/// <c>TrayTrigger.exe --play "Game"</c>: a shortcut, Stream Deck button, scheduled task or streaming host starts a library
/// game through TrayTrigger, so it gets its profile and put-back like any other launch. With TrayTrigger already running
/// the request goes to it over a pipe only the same Windows user can open; with it not running, it starts quietly in the
/// tray and plays the game. See <see cref="PlayRequest"/>.
/// </summary>
public partial class App
{
    private PlayRequestChannel? _playChannel;

    /// <summary>Listens for requests from a second copy. Marshalled to the UI thread, which owns the library.</summary>
    private void StartPlayChannel()
    {
        try
        {
            _playChannel = new PlayRequestChannel();
            _playChannel.Received += request => Dispatcher.BeginInvoke(() => HandlePlayRequest(request));
            _playChannel.Start();
            LoggingService.Info("App", "Play request listener started.");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("App", $"Could not start the play request listener: {ex.Message}");
        }
    }

    /// <summary>Plays the game a request names, or says in a tray notification why it can't. Never opens the window.</summary>
    private void HandlePlayRequest(PlayRequest request)
    {
        if (_isShuttingDown || _mainViewModel == null) return;

        var library = _mainViewModel.Library;
        var match = PlayMatch.Find(request.Query, library.Games.Select(c => (c.Id, c.Name)));
        string without = request.WithoutChanges ? " (without changes)" : string.Empty;

        switch (match.Kind)
        {
            case PlayMatchKind.One:
                var card = library.Games.FirstOrDefault(c => c.Id == match.Id);
                if (card == null) return;
                LoggingService.Info("App", $"Play request for '{request.Query}'{without}: playing '{card.Name}'.");
                library.LaunchGame(card, request.WithoutChanges);
                break;

            case PlayMatchKind.Several:
                if (LoggingService.IsVerboseEnabled)
                    LoggingService.Verbose("App", $"Play request for '{request.Query}'{without} matched {match.Names.Count} games and none was played: {string.Join(", ", match.Names)}.");
                LoggingService.Info("App", $"Play request for '{request.Query}'{without}: more than one game matches, so none was played.");
                ShowPlayRequestNotice("More Than One Game Matches",
                    $"\"{request.Query}\" matches {string.Join(", ", match.Names.Take(4))}{(match.Names.Count > 4 ? " and more" : "")}. Use the full name, or make a shortcut with Create Shortcut.");
                break;

            default:
                LoggingService.Info("App", $"Play request for '{request.Query}'{without}: no game in the library matches.");
                ShowPlayRequestNotice("Game Not Found",
                    string.IsNullOrWhiteSpace(request.Query)
                        ? "--play needs a game's name, as in --play \"Hades\"."
                        : $"No game in your library matches \"{request.Query}\".");
                break;
        }
    }

    /// <summary>A tray notification for a play request that could not start a game; echoed in the log like every other notice the user is shown.</summary>
    private void ShowPlayRequestNotice(string title, string message)
    {
        LoggingService.Shown("Tray notification", $"{title}: {message}");
        ShowTrayNotification(title, message);
    }
}
