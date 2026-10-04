using System.Text.Json.Serialization;

namespace TrayTrigger.Models;

/// <summary>
/// Settings › Launch &amp; Performance: scripts that run for every game that has no script of its
/// own for that phase. Resolution is per phase (see <see cref="Services.GameScriptService.ResolvePreLaunch"/>):
/// a game with its own pre-launch script but no post-exit script still gets the default post-exit.
/// A game opts out of both with <see cref="GameEntry.SkipDefaultScripts"/>. The options here apply
/// whenever a default script runs; a game's own options apply only to its own scripts. A default
/// script gets the game's <see cref="GameEntry.ScriptArguments"/> when the game has some, and
/// <see cref="ScriptArguments"/> otherwise - that is how one generic default is set up once and
/// still varied per game.
/// </summary>
public class ScriptDefaults
{
    /// <summary>
    /// "Run the default scripts". Off by default like the feature switch itself: defaults are an
    /// advanced-user feature, and a typed path must not start running for every game until the
    /// user explicitly turns them on. Unticking later pauses them without clearing anything.
    /// </summary>
    public bool Enabled { get; set; } = false;
    public string PreLaunchScriptPath { get; set; } = string.Empty;
    public string PostExitScriptPath { get; set; } = string.Empty;
    public bool WaitForPreLaunchScript { get; set; } = true;
    public int PreLaunchScriptTimeoutSeconds { get; set; } = 10;
    public bool AbortLaunchOnScriptFailure { get; set; }
    public bool RunScriptsHidden { get; set; } = true;
    public bool RunScriptsAsAdmin { get; set; }
    /// <summary>Script Arguments for the default scripts, used for a game whose own are blank.
    /// Never passed to a game's own script.</summary>
    public string ScriptArguments { get; set; } = string.Empty;

    [JsonIgnore]
    public bool HasPreLaunchScript => !string.IsNullOrWhiteSpace(PreLaunchScriptPath);
    [JsonIgnore]
    public bool HasPostExitScript => !string.IsNullOrWhiteSpace(PostExitScriptPath);
    [JsonIgnore]
    public bool HasAny => HasPreLaunchScript || HasPostExitScript;
}
