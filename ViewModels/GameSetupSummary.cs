using System.Collections.Generic;
using System.Linq;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

/// <summary>One card in Game Details' "Your setup": what a settings area is doing for this game, and the Settings tab that edits it.</summary>
public sealed record GameSetupCard(string Title, string Chip, bool ChipIsOn, string Summary, GameEditSection Section, string LinkText);

/// <summary>
/// Reads a game as it is saved and says, area by area, what TrayTrigger changes for it. Facts the app
/// already records, in the words Edit Game uses for them; never a measured result.
/// </summary>
public static class GameSetupSummary
{
    /// <param name="scriptsEnabled">Settings' "Enable scripts" switch. With it off, a game's scripts do not run.</param>
    /// <param name="defaultScriptsConfigured">Settings has default scripts turned on.</param>
    public static IReadOnlyList<GameSetupCard> For(GameEntry game, bool scriptsEnabled, bool defaultScriptsConfigured)
    {
        var cards = new List<GameSetupCard> { Performance(game) };
        if (game.DlssSettings.Count > 0) cards.Add(Dlss());
        cards.Add(Display(game));
        cards.Add(Scripts(game, scriptsEnabled, defaultScriptsConfigured));
        cards.Add(Launch(game));
        return cards;
    }

    private static GameSetupCard Performance(GameEntry game)
    {
        bool profileOn = game.PerformanceProfile != PerformanceProfileMode.Off;
        var parts = new List<string>();
        if (profileOn) parts.Add($"The {game.PerformanceProfile} profile's tweaks apply");
        if (game.CpuAffinity != CpuAffinityMode.Default) parts.Add($"CPU cores: {CpuTopology.MenuLabel(game.CpuAffinity)}");
        if (game.Hdr != HdrMode.ProfileDefault) parts.Add($"HDR: {HdrModes.Label(game.Hdr)}");
        if (game.AutoHdr != HdrMode.ProfileDefault) parts.Add($"Auto HDR: {HdrModes.Label(game.AutoHdr)}");

        bool on = parts.Count > 0;
        string chip = profileOn ? game.PerformanceProfile.ToString() : on ? "Custom" : "Off";
        return new GameSetupCard("Performance", chip, on,
            on ? string.Join(". ", parts) + "." : "Nothing is changed when this game runs.",
            GameEditSection.Performance, "Edit performance");
    }

    private static GameSetupCard Dlss() =>
        new("NVIDIA DLSS", "On", true, "TrayTrigger holds a DLSS override for this game.", GameEditSection.Performance, "Edit DLSS");

    private static GameSetupCard Display(GameEntry game)
    {
        var parts = new List<string>();
        if (game.DisplayWidth > 0 || game.DisplayHeight > 0 || game.DisplayRefreshHz > 0)
        {
            string size = game.DisplayWidth > 0 && game.DisplayHeight > 0 ? $"{game.DisplayWidth}x{game.DisplayHeight}" : string.Empty;
            string rate = game.DisplayRefreshHz > 0 ? $"{game.DisplayRefreshHz} Hz" : string.Empty;
            parts.Add($"Display: {string.Join(" at ", new[] { size, rate }.Where(s => s.Length > 0))}");
        }
        if (!string.IsNullOrEmpty(game.DisplayMonitorId)) parts.Add("Uses a chosen monitor");
        if (game.MakeMainDisplay) parts.Add("Made the main display");
        if (game.DimOtherScreens) parts.Add("Other screens dimmed");
        if (game.KeepMouseOnGameScreen) parts.Add("Mouse kept on the game's screen");
        if (!string.IsNullOrEmpty(game.AudioOutputDeviceId)) parts.Add("Sound goes to a chosen output");
        if (!string.IsNullOrEmpty(game.AudioInputDeviceId)) parts.Add("Uses a chosen microphone");

        bool on = parts.Count > 0;
        return new GameSetupCard("Display and sound", on ? "Custom" : "Default", on,
            on ? string.Join(". ", parts) + "." : "The display and sound are left as they are.",
            GameEditSection.Display, "Edit display and sound");
    }

    private static GameSetupCard Scripts(GameEntry game, bool scriptsEnabled, bool defaultScriptsConfigured)
    {
        // Scripts set on a game do not run while the Settings switch is off, so the card says so rather than counting them.
        if (!scriptsEnabled)
        {
            return new GameSetupCard("Scripts", "Off", false,
                game.HasScripts ? "Scripts are turned off in Settings, so the ones set for this game do not run." : "Scripts are turned off in Settings.",
                GameEditSection.Scripts, "Edit scripts");
        }

        bool pre = !string.IsNullOrWhiteSpace(game.PreLaunchScriptPath), post = !string.IsNullOrWhiteSpace(game.PostExitScriptPath);
        bool same = pre && post && string.Equals(game.PreLaunchScriptPath.Trim(), game.PostExitScriptPath.Trim(), System.StringComparison.OrdinalIgnoreCase);
        int count = (pre ? 1 : 0) + (post && !same ? 1 : 0);
        if (count == 0)
        {
            return defaultScriptsConfigured && !game.SkipDefaultScripts
                ? new GameSetupCard("Scripts", "Defaults", true, "No script of its own. The default scripts from Settings run for this game.", GameEditSection.Scripts, "Edit scripts")
                : new GameSetupCard("Scripts", "None set", false, "No script is set for this game.", GameEditSection.Scripts, "Edit scripts");
        }

        string summary = (pre, post) switch
        {
            (true, true) when same => "One script runs before launch and after exit.",
            (true, true) => "One runs before launch and one after exit.",
            (true, false) => "One runs before launch.",
            _ => "One runs after exit.",
        };
        return new GameSetupCard("Scripts", count == 1 ? "1 script" : $"{count} scripts", true, summary, GameEditSection.Scripts, "Edit scripts");
    }

    private static GameSetupCard Launch(GameEntry game)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(game.Hotkey)) parts.Add($"Hotkey: {game.Hotkey}");
        if (game.RunAsAdmin) parts.Add("Runs as administrator");
        if (!string.IsNullOrWhiteSpace(game.Arguments)) parts.Add("Has launch arguments");
        if (!string.IsNullOrWhiteSpace(game.WorkingDirectory)) parts.Add("Has its own working folder");
        if (game.LaunchDirectly) parts.Add("Runs its executable directly");

        bool on = parts.Count > 0;
        return new GameSetupCard("Launch", on ? "Custom" : "Default", on,
            on ? string.Join(". ", parts) + "." : "Starts the usual way.", GameEditSection.Launch, "Edit launch");
    }
}
