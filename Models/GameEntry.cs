using System;
using System.Text.Json.Serialization;

namespace TrayTrigger.Models;

public class GameEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public bool RunAsAdmin { get; set; }
    public string Category { get; set; } = "Uncategorized";
    public bool IsFavorite { get; set; }
    public string Hotkey { get; set; } = string.Empty;
    public string IconPath { get; set; } = string.Empty;
    public string? CoverImagePath { get; set; }
    public bool IsSteamGame { get; set; }
    public bool ForceSteamOverlayTag { get; set; }
    public string? SteamAppId { get; set; }
    public DateTime? LastPlayed { get; set; }
    public long CumulativePlaytimeMinutes { get; set; }
    public DateTime? LastEnrichmentAttemptUtc { get; set; }
    public PerformanceProfileMode PerformanceProfile { get; set; } = PerformanceProfileMode.Off;

    /// <summary>Optional .bat/.cmd/.ps1/.exe run just before the game starts. See <see cref="Services.GameScriptService"/>.</summary>
    public string PreLaunchScriptPath { get; set; } = string.Empty;
    /// <summary>Optional script run after the game exits (direct .exe and Steam launches only).</summary>
    public string PostExitScriptPath { get; set; } = string.Empty;
    /// <summary>Hold the game launch until the pre-launch script finishes (capped at 30s).</summary>
    public bool WaitForPreLaunchScript { get; set; } = true;
    /// <summary>Run scripts without a visible console window.</summary>
    public bool RunScriptsHidden { get; set; } = true;
    /// <summary>Run scripts elevated (UAC prompt). Environment variables are unavailable in this mode.</summary>
    public bool RunScriptsAsAdmin { get; set; }

    [JsonIgnore]
    public bool HasScripts => !string.IsNullOrWhiteSpace(PreLaunchScriptPath) || !string.IsNullOrWhiteSpace(PostExitScriptPath);

    [JsonIgnore]
    public bool HasSteamOverlay => IsSteamGame || ForceSteamOverlayTag;

    [JsonIgnore]
    public string PlaytimeDisplay
    {
        get
        {
            if (CumulativePlaytimeMinutes > 0)
            {
                if (CumulativePlaytimeMinutes < 60)
                    return $"{CumulativePlaytimeMinutes}m played";

                double hours = Math.Round(CumulativePlaytimeMinutes / 60.0, 1);
                return $"{hours:0.#}h played";
            }

            if (IsSteamGame)
                return string.Empty;

            return "0 min played";
        }
    }

    [JsonIgnore]
    public string LastPlayedDisplay
    {
        get
        {
            if (!LastPlayed.HasValue)
                return "Never played";

            var dt = LastPlayed.Value.ToLocalTime();
            var diff = DateTime.Now - dt;

            if (diff.TotalDays < 1 && diff.TotalHours >= 0 && dt.Date == DateTime.Today)
                return $"Today, {dt:t}";

            if (diff.TotalDays < 2 && dt.Date == DateTime.Today.AddDays(-1))
                return $"Yesterday, {dt:t}";

            return dt.ToString("MMM d, yyyy");
        }
    }
}
