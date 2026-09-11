using System.Collections.Generic;

namespace TrayTrigger.Models;

/// <summary>
/// Game metadata from the RAWG video-games database (rawg.io), the fallback source for games with
/// no Steam listing. In-memory only - never serialized to disk (mirrors <c>SteamAppDetails</c>).
/// RAWG is text metadata only here; its images are screenshots, not vertical box art, so cover art
/// stays Steam/SteamGridDB. Fields left empty when RAWG doesn't supply them.
/// </summary>
public class RawgGameDetails
{
    public int RawgId { get; set; }
    public string Slug { get; set; } = string.Empty;
    /// <summary>RAWG's own title for the matched game, so a caller can guard a loose search hit.</summary>
    public string Name { get; set; } = string.Empty;
    public string Developer { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    /// <summary>ISO date string as RAWG returns it (e.g. "2017-07-25"), or empty.</summary>
    public string ReleaseDate { get; set; } = string.Empty;
    /// <summary>Plain-text synopsis (RAWG's <c>description_raw</c>), or empty.</summary>
    public string Description { get; set; } = string.Empty;
    public List<string> Genres { get; set; } = new();
    public int? Metacritic { get; set; }
    public string EsrbRating { get; set; } = string.Empty;
    /// <summary>The game's official website, or the RAWG page as a fallback (for the attribution link).</summary>
    public string Website { get; set; } = string.Empty;
}
