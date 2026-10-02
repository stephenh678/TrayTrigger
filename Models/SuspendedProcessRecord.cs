using System;

namespace TrayTrigger.Models;

/// <summary>
/// One process TrayTrigger suspended for a game, kept on disk (suspended-games.json) while it is
/// suspended. A process left frozen when TrayTrigger stops - a crash, an update killing it - would
/// stay frozen for good with nothing on screen to resume it, so the next start resumes everything
/// listed. The start time tells the process apart from a later one given the same id.
/// </summary>
public sealed class SuspendedProcessRecord
{
    public string GameId { get; set; } = string.Empty;
    public string GameName { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public DateTime StartedUtc { get; set; }
    /// <summary>TrayTrigger muted this process's sound when it suspended it, so it unmutes it again.</summary>
    public bool Muted { get; set; }
}
