using System.Collections.Generic;

namespace TrayTrigger.Models;

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
    public string StoreUrl => !string.IsNullOrWhiteSpace(AppId) ? $"https://store.steampowered.com/app/{AppId}" : string.Empty;

    public int? MetacriticScore { get; set; }
    public string? MetacriticUrl { get; set; }
    public string? ReviewSummary { get; set; }
    public int? ReviewScorePercent { get; set; }

    public List<string> Genres { get; set; } = new();
    public string PrimaryGenre => Genres.Count > 0 ? Genres[0] : "Action";

    public List<string> PlayModes { get; set; } = new();
    public string? PcRequirementsMin { get; set; }
    public string? PcRequirementsRec { get; set; }

    public List<SteamNewsItem> NewsItems { get; set; } = new();
}
