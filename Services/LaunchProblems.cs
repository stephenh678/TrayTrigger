using System;

namespace TrayTrigger.Services;

/// <summary>
/// The Activity &amp; History lines for a launch that did not work: one that failed outright, and one where
/// the game was started but TrayTrigger never saw it running. Both are Problem entries, so a "I pressed
/// Play and nothing happened" has a record to look back at. Plain text in, text out.
/// </summary>
internal static class LaunchProblems
{
    public static string FailedGroupKey(string gameId) => $"launch.failed|{gameId}";
    public static string UnseenGroupKey(string gameId) => $"launch.unseen|{gameId}";

    /// <summary>"Elden Ring didn't launch", with the reason TrayTrigger gave the player as the detail.</summary>
    public static (string Text, string Detail) DescribeFailed(string gameName, string? error, bool executableMissing)
    {
        string reason = string.IsNullOrWhiteSpace(error)
            ? (executableMissing ? "Its executable wasn't found." : "No reason was given.")
            : error.Trim();
        return ($"{gameName} didn't launch", reason);
    }

    /// <summary>
    /// The line for a session that ended without the game ever being seen running, or null when that was
    /// not a problem (the player ended it, or the launch already failed and said so).
    /// </summary>
    public static (string Text, string Detail)? DescribeUnseen(string gameName, string endReason)
    {
        string? why = endReason switch
        {
            "Steam never reported the game running" => "Steam never reported it running",
            "game process never appeared" => "its process never appeared",
            "no process handle returned" => "Windows gave TrayTrigger no process to follow",
            "no Steam AppId to track" => "it has no Steam App ID to follow",
            "no install directory to track" => "TrayTrigger has no install folder to follow it in",
            _ => null,
        };
        if (why == null) return null;
        return ($"{gameName} was started, but TrayTrigger never saw it running",
            $"{char.ToUpperInvariant(why[0])}{why[1..]}, so no playtime was recorded for this launch.");
    }
}
