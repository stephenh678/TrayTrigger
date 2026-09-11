using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TrayTrigger.Models;

/// <summary>
/// Game metadata from the RAWG video-games database (rawg.io), the source for games with no
/// Steam listing. Persisted to a small on-disk cache (<c>rawg-cache.json</c> beside the poster
/// cache) so details open instantly and offline and re-opens cost no API quota. Beyond the text
/// fields it carries what RAWG can contribute to the library itself: a primary genre for the
/// category, play-mode tags, a screenshot for ambient art (never the poster - it's landscape),
/// and the stores the game is sold on. Fields are left empty when RAWG doesn't supply them.
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
    /// <summary>RAWG's community rating, 0-5, or null when unrated. Shown when there's no
    /// Metacritic score (most Game Pass / indie titles).</summary>
    public double? Rating { get; set; }
    public int RatingsCount { get; set; }
    public string EsrbRating { get; set; } = string.Empty;
    /// <summary>The game's page on rawg.io - the target of the required "Data from RAWG" link.</summary>
    public string RawgPageUrl { get; set; } = string.Empty;
    /// <summary>Play-mode chips derived from RAWG tags (Single-player, Co-op, controller support...),
    /// normalised to the same wording Steam's categories use.</summary>
    public List<string> PlayModes { get; set; } = new();
    /// <summary>RAWG's <c>background_image</c>: a landscape screenshot. Used for the blurred hero
    /// backdrop and as a last-resort crisp fallback before the icon tile - never as the poster.</summary>
    public string BackgroundImageUrl { get; set; } = string.Empty;
    /// <summary>Stores RAWG lists the game on. URLs are filled by a second call
    /// (<see cref="StoreLinksResolved"/>) only when the details window needs them.</summary>
    public List<RawgStoreLink> Stores { get; set; } = new();
    public bool StoreLinksResolved { get; set; }
    public DateTime FetchedUtc { get; set; }

    /// <summary>The genre used to categorise a game when it's still Uncategorized. "Indie" is a
    /// scale, not a kind of game, so it is skipped when anything else is listed. Empty when RAWG
    /// lists no genre - nothing is invented.</summary>
    [JsonIgnore]
    public string PrimaryGenre
    {
        get
        {
            foreach (var g in Genres)
            {
                if (!string.Equals(g, "Indie", StringComparison.OrdinalIgnoreCase))
                    return g;
            }
            return Genres.Count > 0 ? Genres[0] : string.Empty;
        }
    }
}

/// <summary>One store a game is sold on, per RAWG. <see cref="Url"/> is empty until resolved.</summary>
public class RawgStoreLink
{
    public int StoreId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}
