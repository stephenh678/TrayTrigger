using System;
using System.Collections.Generic;
using System.Linq;

namespace TrayTrigger.Services;

/// <summary>
/// "Play this game", asked from outside the window: <c>TrayTrigger.exe --play "Hades"</c> (a shortcut, Stream Deck, Task
/// Scheduler, a streaming host's command). The running TrayTrigger plays it as its Play button does; with
/// <c>--no-changes</c> it is Play Without Changes. Only a game already in the library can be named: nothing in the
/// request is run, and nothing but the game's id or name is read from it.
/// </summary>
public sealed record PlayRequest(string Query, bool WithoutChanges)
{
    public const string PlaySwitch = "--play";
    public const string WithoutChangesSwitch = "--no-changes";

    /// <summary>The longest game name or id a request carries; anything longer is not a game in the library.</summary>
    internal const int MaxQueryLength = 260;

    /// <summary>The command-line arguments that play a game: <c>--play &lt;id&gt;</c>, with <c>--no-changes</c> for Play Without Changes. A game id is 32 hex digits, so it needs no quoting.</summary>
    public static string ArgumentsFor(string gameId, bool withoutChanges = false) =>
        $"{PlaySwitch} {gameId}" + (withoutChanges ? $" {WithoutChangesSwitch}" : string.Empty);

    /// <summary>The request on a command line, or null when it has no <c>--play</c>. A <c>--play</c> with nothing after it is a request with an empty query, which finds no game and says so.</summary>
    public static PlayRequest? FromArguments(IReadOnlyList<string> args)
    {
        string? query = null;
        bool withoutChanges = false;
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (a.Equals(PlaySwitch, StringComparison.OrdinalIgnoreCase))
            {
                // The next argument is the game, unless it is another switch (a missing name).
                bool hasValue = i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
                query = hasValue ? args[++i] : string.Empty;
            }
            else if (a.StartsWith(PlaySwitch + "=", StringComparison.OrdinalIgnoreCase))
            {
                query = a[(PlaySwitch.Length + 1)..];
            }
            else if (a.Equals(WithoutChangesSwitch, StringComparison.OrdinalIgnoreCase))
            {
                withoutChanges = true;
            }
        }
        return query == null ? null : new PlayRequest(Clean(query), withoutChanges);
    }

    /// <summary>The query without control characters (a newline would split the one-line wire format) and without surrounding quotes or spaces.</summary>
    private static string Clean(string query)
    {
        var chars = query.Where(c => !char.IsControl(c)).ToArray();
        string cleaned = new string(chars).Trim().Trim('"').Trim();
        return cleaned.Length > MaxQueryLength ? cleaned[..MaxQueryLength] : cleaned;
    }

    /// <summary>One line for the pipe: "1|Hades" (the first character says Play Without Changes).</summary>
    public string ToLine() => (WithoutChanges ? "1|" : "0|") + Clean(Query);

    public static bool TryParseLine(string? line, out PlayRequest request)
    {
        request = new PlayRequest(string.Empty, false);
        if (line == null || line.Length < 2 || line[1] != '|' || (line[0] != '0' && line[0] != '1')) return false;
        request = new PlayRequest(Clean(line[2..]), line[0] == '1');
        return true;
    }
}

public enum PlayMatchKind { None, One, Several }

/// <summary>What a <see cref="PlayRequest"/>'s query found in the library.</summary>
public sealed record PlayMatch(PlayMatchKind Kind, string? Id, IReadOnlyList<string> Names)
{
    /// <summary>
    /// The game a query names: its id, else its exact name, else the one name that starts with it, else the one that contains it
    /// (case does not matter). Two names matching the same way is "several", never a guess.
    /// </summary>
    public static PlayMatch Find(string query, IEnumerable<(string Id, string Name)> games)
    {
        var list = games.ToList();
        query = query.Trim();
        if (query.Length == 0) return new PlayMatch(PlayMatchKind.None, null, []);

        var byId = list.FirstOrDefault(g => string.Equals(g.Id, query, StringComparison.OrdinalIgnoreCase));
        if (byId.Id != null) return new PlayMatch(PlayMatchKind.One, byId.Id, [byId.Name]);

        foreach (var matches in new Func<(string Id, string Name), bool>[]
        {
            g => string.Equals(g.Name, query, StringComparison.OrdinalIgnoreCase),
            g => g.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase),
            g => g.Name.Contains(query, StringComparison.OrdinalIgnoreCase),
        })
        {
            var found = list.Where(matches).ToList();
            if (found.Count == 1) return new PlayMatch(PlayMatchKind.One, found[0].Id, [found[0].Name]);
            if (found.Count > 1) return new PlayMatch(PlayMatchKind.Several, null, found.Select(g => g.Name).Order(StringComparer.OrdinalIgnoreCase).ToList());
        }
        return new PlayMatch(PlayMatchKind.None, null, []);
    }
}
