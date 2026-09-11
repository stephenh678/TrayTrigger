using System;
using System.Text.Json.Serialization;

namespace TrayTrigger.Models;

public class SteamNewsItem
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public string Snippet { get; set; } = string.Empty;

    /// <summary>True when Steam tagged the announcement as "patchnotes" (developer-marked patch notes).</summary>
    public bool IsPatchNotes { get; set; }

    [JsonIgnore]
    public string DateDisplay => Date > DateTime.MinValue ? Date.ToString("MMM d, yyyy") : string.Empty;
}
