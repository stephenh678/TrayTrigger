using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TrayTrigger.Models;

/// <summary>
/// Store details, review summary, requirements and latest news for a Steam App ID. Persisted to
/// <c>steam-cache.json</c> beside the poster cache (see <see cref="Services.SteamMetadataService"/>)
/// so Game Details opens instantly and offline; <see cref="FetchedUtc"/> drives the background
/// re-fetch under the "Refresh game info" setting.
/// </summary>
public class SteamAppDetails
{
    public string AppId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Developers { get; set; } = string.Empty;
    public string Publishers { get; set; } = string.Empty;
    public string ReleaseDate { get; set; } = string.Empty;
    public string ShortDescription { get; set; } = string.Empty;
    public string? HeaderImageUrl { get; set; }
    public string? CapsuleImageUrl { get; set; }
    public string? PosterImageUrl { get; set; }
    public string? CoverImagePath { get; set; }
    [JsonIgnore]
    public string StoreUrl => !string.IsNullOrWhiteSpace(AppId) ? $"https://store.steampowered.com/app/{AppId}" : string.Empty;

    public int? MetacriticScore { get; set; }
    public string? MetacriticUrl { get; set; }
    public string? ReviewSummary { get; set; }
    public int? ReviewScorePercent { get; set; }

    public List<string> Genres { get; set; } = new();
    /// <summary>The genre used for auto-categorisation - <see cref="GenreRules.PrimaryOf"/>, the same
    /// rule RAWG uses (skips "Indie"). Empty when Steam lists none - nothing is invented, so RAWG's
    /// genre can fill the category instead (every caller guards on empty).</summary>
    [JsonIgnore]
    public string PrimaryGenre => GenreRules.PrimaryOf(Genres);

    public List<string> PlayModes { get; set; } = new();
    public string? PcRequirementsMin { get; set; }
    public string? PcRequirementsRec { get; set; }

    public List<SteamNewsItem> NewsItems { get; set; } = new();

    /// <summary>When this entry was fetched from Steam (UTC). Default for entries written before
    /// the disk cache existed, which the freshness rule treats as stale.</summary>
    public DateTime FetchedUtc { get; set; }
}
