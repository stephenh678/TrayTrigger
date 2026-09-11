namespace TrayTrigger.Models;

/// <summary>
/// How long cached game info (Steam and RAWG details) is trusted before the Game Details window
/// re-fetches it in the background. The window always paints from the disk cache first; this
/// only decides whether a fresh copy is requested behind it. Manual actions (Refresh now,
/// Refresh metadata, Change match) always bypass it.
/// </summary>
public enum MetadataRefreshInterval
{
    /// <summary>Re-fetch every time the details window opens (the pre-1.3.9 Steam behaviour).</summary>
    EveryOpen,
    Daily,
    /// <summary>The default: news and ratings stay current enough, at a third of the requests.</summary>
    Every3Days,
    Weekly,
    Monthly,
    /// <summary>Never re-fetch on its own; only the manual refresh actions update the cache.</summary>
    Never,
}
