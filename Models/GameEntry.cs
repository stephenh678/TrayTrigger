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
    /// <summary>True for a PC Game Pass / Microsoft Store (GDK) game imported via Xbox scanning.
    /// Unlike every other platform there is no direct-exe fallback: GDK executables need package
    /// identity, so the game is always launched by shell activation of <see cref="XboxAumid"/>
    /// and ExecutablePath is informational (icon, dedupe, "open folder") and re-resolved on each
    /// launch because it changes with every game update.</summary>
    public bool IsXboxGame { get; set; }
    /// <summary>Application User Model ID, "&lt;PackageFamilyName&gt;!&lt;AppId&gt;" - stable across updates.</summary>
    public string? XboxAumid { get; set; }
    /// <summary>Which source the Game Details window shows text metadata from (a per-game view
    /// preference; never affects the library category, art, or sorting). Auto by default - Steam
    /// when there's a Steam App ID, otherwise RAWG. See <see cref="MetadataSource"/>.</summary>
    public MetadataSource PreferredMetadataSource { get; set; } = MetadataSource.Auto;
    /// <summary>The RAWG game id this entry resolved to, cached so the details window doesn't
    /// re-search RAWG by name each time it opens. 0 when unresolved.</summary>
    public int RawgId { get; set; }
    public DateTime? LastPlayed { get; set; }
    public long CumulativePlaytimeMinutes { get; set; }
    public DateTime? LastEnrichmentAttemptUtc { get; set; }
    public PerformanceProfileMode PerformanceProfile { get; set; } = PerformanceProfileMode.Off;

    /// <summary>
    /// Which cores the game's process may run on. Independent of the profile tier: on an Intel
    /// hybrid CPU (12th gen+) some engines and anti-cheat titles run worse when threads land on
    /// E-cores, and pinning to P-cores is the standard fix. No-op on non-hybrid CPUs.
    /// </summary>
    public CpuAffinityMode CpuAffinity { get; set; } = CpuAffinityMode.Default;

    /// <summary>Optional .bat/.cmd/.ps1/.exe run just before the game starts. See <see cref="Services.GameScriptService"/>.</summary>
    public string PreLaunchScriptPath { get; set; } = string.Empty;
    /// <summary>
    /// Optional script run after the game exits. Runs for every launch path that has an exit
    /// signal: direct .exe launches, Steam (via Steam's own "running" flag), and GOG/EA/Epic/Ubisoft
    /// client launches (via install-directory process tracking). Only a bare protocol shortcut
    /// (a dropped .url with no platform ID) has no exit signal and skips it.
    /// </summary>
    public string PostExitScriptPath { get; set; } = string.Empty;
    /// <summary>Hold the game launch until the pre-launch script finishes (see <see cref="PreLaunchScriptTimeoutSeconds"/>).</summary>
    public bool WaitForPreLaunchScript { get; set; } = true;
    /// <summary>How long "wait for the pre-launch script" holds the launch before giving up. 1-600; default 30.</summary>
    public int PreLaunchScriptTimeoutSeconds { get; set; } = 30;
    /// <summary>
    /// Cancel the launch (and roll back the Performance Profile) when the pre-launch script exits
    /// non-zero, times out, or fails to start. Implies waiting for the script.
    /// </summary>
    public bool AbortLaunchOnScriptFailure { get; set; }
    /// <summary>
    /// After this game's session ends, close the platform client it was launched through (Steam,
    /// GOG Galaxy, EA App, Epic Games Launcher, Ubisoft Connect). Off by default. For Steam this
    /// also makes a cold start of the client silent (minimized to the tray) - see
    /// ProcessLauncherService.TryLaunchSteamSilently.
    /// </summary>
    public bool CloseLauncherOnExit { get; set; }
    /// <summary>Run scripts without a visible console window.</summary>
    public bool RunScriptsHidden { get; set; } = true;
    /// <summary>Run scripts elevated (UAC prompt). Environment variables are unavailable in this mode.</summary>
    public bool RunScriptsAsAdmin { get; set; }
    /// <summary>
    /// Free-text extra arguments for this game's scripts, appended after the five positional
    /// arguments so nothing shifts. Passed verbatim to .bat/.cmd (cmd.exe parses it), split with
    /// Windows command-line rules for .ps1/.exe. Shared by the pre-launch and post-exit scripts.
    /// This is how one generic script is parameterised per game (a save folder, a profile name).
    /// </summary>
    public string ScriptArguments { get; set; } = string.Empty;
    /// <summary>
    /// "Don't run the default scripts for this game": opts out of the Settings
    /// <see cref="AppSettings.ScriptDefaults"/> for both phases. Off by default, so existing
    /// libraries pick up defaults with no per-game clicks. Irrelevant for a phase where the game
    /// has its own script - that always wins over the default.
    /// </summary>
    public bool SkipDefaultScripts { get; set; }

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

            // Steam sessions have recorded real playtime since 1.3.6, so a Steam game with
            // nothing recorded reads the same as every other platform.
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
