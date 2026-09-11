using System.Text.Json.Serialization;

namespace TrayTrigger.Models;

/// <summary>
/// Settings › Launch &amp; Performance: scripts that run for every game that has no script of its
/// own for that phase. Resolution is per phase (see <see cref="Services.GameScriptService.ResolvePreLaunch"/>):
/// a game with its own pre-launch script but no post-exit script still gets the default post-exit.
/// A game opts out of both with <see cref="GameEntry.SkipDefaultScripts"/>. The options here apply
/// whenever a default script runs; a game's own options apply only to its own scripts. The
/// game's <see cref="GameEntry.ScriptArguments"/> are passed to default scripts too - that is how
/// one generic default is parameterised per game.
/// </summary>
public class ScriptDefaults
{
    public string PreLaunchScriptPath { get; set; } = string.Empty;
    public string PostExitScriptPath { get; set; } = string.Empty;
    public bool WaitForPreLaunchScript { get; set; } = true;
    public int PreLaunchScriptTimeoutSeconds { get; set; } = 30;
    public bool AbortLaunchOnScriptFailure { get; set; }
    public bool RunScriptsHidden { get; set; } = true;
    public bool RunScriptsAsAdmin { get; set; }

    [JsonIgnore]
    public bool HasPreLaunchScript => !string.IsNullOrWhiteSpace(PreLaunchScriptPath);
    [JsonIgnore]
    public bool HasPostExitScript => !string.IsNullOrWhiteSpace(PostExitScriptPath);
    [JsonIgnore]
    public bool HasAny => HasPreLaunchScript || HasPostExitScript;
}
