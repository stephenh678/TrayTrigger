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
    /// <summary>
    /// Hidden from the library grid and tray menu, but the record itself is kept - unlike
    /// deleting, this means "Scan for Games"/"Add Folder" still recognize this exe/AppId as
    /// already known and never re-suggest it.
    /// </summary>
    public bool IsHidden { get; set; }
    public string Hotkey { get; set; } = string.Empty;
    public string IconPath { get; set; } = string.Empty;
    public string? CoverImagePath { get; set; }
    public bool IsSteamGame { get; set; }
    public bool ForceSteamOverlayTag { get; set; }
    public string? SteamAppId { get; set; }

    /// <summary>
    /// The launcher integration that imported this entry (or that it was later linked to when a
    /// manual import recognised its install folder). Null for a plain Local game and for entries
    /// from before this field existed - see LibraryViewModel.IsPlatformGame for the legacy
    /// fallback. This is what "turn off integration + remove its games" sweeps, not the launch
    /// flags below, which a user can also set by hand.
    /// </summary>
    public LauncherPlatform? ImportedFrom { get; set; }

    /// <summary>
    /// Launch ExecutablePath directly instead of through the platform's client (GOG Galaxy, EA
    /// App, Epic Games Launcher, Ubisoft Connect, Steam). Off by default; set from Edit Game for a
    /// user who wants a specific alternate exe (a DX11 build, a mod launcher) to actually run -
    /// without it, the client-launch branches in ProcessLauncherService ignore ExecutablePath.
    /// </summary>
    public bool LaunchDirectly { get; set; }
    /// <summary>True for a game imported via GOG scanning. Unlike Steam, this doesn't change how
    /// ExecutablePath is interpreted - it's always a real local exe - it only changes how the game
    /// is launched (through GOG Galaxy when available; see ProcessLauncherService) and how re-scans
    /// dedupe against it.</summary>
    public bool IsGogGame { get; set; }
    public string? GogGameId { get; set; }
    /// <summary>True for a game imported via EA scanning. Same semantics as IsGogGame - only
    /// changes how the game is launched (through EA App when available) and how re-scans dedupe.</summary>
    public bool IsEaGame { get; set; }
    public string? EaContentId { get; set; }
    /// <summary>True for a game imported via Epic Games Store scanning. Same semantics as
    /// IsGogGame/IsEaGame.</summary>
    public bool IsEpicGame { get; set; }
    public string? EpicAppName { get; set; }
    /// <summary>True for a game imported via Ubisoft Connect scanning. Same semantics as
    /// IsGogGame/IsEaGame/IsEpicGame.</summary>
    public bool IsUbisoftGame { get; set; }
    public string? UbisoftGameId { get; set; }
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
