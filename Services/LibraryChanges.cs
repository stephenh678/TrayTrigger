using System;
using System.Collections.Generic;
using System.Linq;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// The Activity &amp; History lines for what the player does to their own library: saving Edit Game, and
/// removing games or undoing it. A game that stops launching after an edit then has the edit on record.
/// Performance profile, CPU cores, HDR and Auto HDR are not here; <see cref="PerformanceActivity"/> records those.
/// Plain values in, text out.
/// </summary>
internal static class LibraryChanges
{
    /// <summary>The settings of a game that Edit Game's Save can change and that decide how it launches.</summary>
    internal sealed record GameSettings(
        string Name, string ExecutablePath, string Arguments, string WorkingDirectory, bool RunAsAdmin, bool LaunchDirectly,
        bool IsHidden, bool CloseLauncherOnExit,
        string PreLaunchScriptPath, string PostExitScriptPath, bool WaitForPreLaunchScript, bool AbortLaunchOnScriptFailure,
        bool RunScriptsHidden, bool RunScriptsAsAdmin, string ScriptArguments, bool SkipDefaultScripts, int PreLaunchScriptTimeoutSeconds,
        string DisplayMode, bool KeepMouseOnGameScreen,
        string DisplayMonitorId, bool MakeMainDisplay, bool DimOtherScreens, string AudioOutputDeviceId, string AudioInputDeviceId,
        string SteamAppId, string HeroArt, string PosterArt, string IconArt);

    public static GameSettings Capture(GameEntry g) => new(
        g.Name, g.ExecutablePath, g.Arguments, g.WorkingDirectory, g.RunAsAdmin, g.LaunchDirectly,
        g.IsHidden, g.CloseLauncherOnExit,
        g.PreLaunchScriptPath, g.PostExitScriptPath, g.WaitForPreLaunchScript, g.AbortLaunchOnScriptFailure,
        g.RunScriptsHidden, g.RunScriptsAsAdmin, g.ScriptArguments, g.SkipDefaultScripts, g.PreLaunchScriptTimeoutSeconds,
        DisplayModeService.RequestedMode(g)?.ToString() ?? string.Empty, g.KeepMouseOnGameScreen,
        g.DisplayMonitorId, g.MakeMainDisplay, g.DimOtherScreens, g.AudioOutputDeviceId, g.AudioInputDeviceId,
        g.SteamAppId?.Trim() ?? string.Empty,
        // Each picture is the file the player chose, or empty while it is Automatic (found for the game).
        g.HeroImagePath ?? string.Empty,
        g.HasCustomPoster ? g.CoverImagePath ?? string.Empty : string.Empty,
        g.HasCustomIcon ? g.IconPath ?? string.Empty : string.Empty);

    /// <summary>
    /// "Hades: launch arguments and run as administrator changed", with each old and new value as the detail;
    /// null when nothing that matters changed.
    /// </summary>
    public static (string Text, string Detail)? DescribeEdit(GameSettings before, GameSettings after)
    {
        var labels = new List<string>();
        var lines = new List<string>();

        void Text(string label, string was, string now)
        {
            if (string.Equals(was, now, StringComparison.Ordinal)) return;
            labels.Add(label);
            lines.Add($"{Capital(label)}: {Shown(was)} -> {Shown(now)}");
        }
        void Chosen(string label, string was, string now, string none)
        {
            if (string.Equals(was, now, StringComparison.Ordinal)) return;
            labels.Add(label);
            lines.Add($"{Capital(label)}: {(string.IsNullOrEmpty(was) ? none : "a chosen one")} -> {(string.IsNullOrEmpty(now) ? none : "a chosen one")}");
        }
        void Art(string label, string was, string now)
        {
            if (string.Equals(was, now, StringComparison.Ordinal)) return;
            labels.Add(label);
            string from = was.Length == 0 ? "automatic" : "a chosen one";
            string to = now.Length == 0 ? "automatic" : was.Length == 0 ? "a chosen one" : "another chosen one";
            lines.Add($"{Capital(label)}: {from} -> {to}");
        }
        void Flag(string label, bool was, bool now)
        {
            if (was == now) return;
            labels.Add(label);
            lines.Add($"{Capital(label)}: {(was ? "on" : "off")} -> {(now ? "on" : "off")}");
        }

        Text("name", before.Name, after.Name);
        Text("Steam App ID", before.SteamAppId, after.SteamAppId);
        Art("hero banner", before.HeroArt, after.HeroArt);
        Art("poster", before.PosterArt, after.PosterArt);
        Art("icon", before.IconArt, after.IconArt);
        Text("executable", before.ExecutablePath, after.ExecutablePath);
        Text("launch arguments", before.Arguments, after.Arguments);
        Text("working folder", before.WorkingDirectory, after.WorkingDirectory);
        Flag("run as administrator", before.RunAsAdmin, after.RunAsAdmin);
        Flag("launch directly", before.LaunchDirectly, after.LaunchDirectly);
        Text("display mode", before.DisplayMode, after.DisplayMode);
        Flag("keep the mouse on the game's screen", before.KeepMouseOnGameScreen, after.KeepMouseOnGameScreen);
        // A monitor and a sound device are named by ids that mean nothing to read, so only that they changed is said.
        Chosen("monitor", before.DisplayMonitorId, after.DisplayMonitorId, "the main display");
        Flag("make the monitor the main display", before.MakeMainDisplay, after.MakeMainDisplay);
        Flag("dim the other screens", before.DimOtherScreens, after.DimOtherScreens);
        Chosen("sound output", before.AudioOutputDeviceId, after.AudioOutputDeviceId, "not changed");
        Chosen("microphone", before.AudioInputDeviceId, after.AudioInputDeviceId, "not changed");
        Flag("hidden", before.IsHidden, after.IsHidden);
        Flag("close launcher on exit", before.CloseLauncherOnExit, after.CloseLauncherOnExit);
        Text("pre-launch script", before.PreLaunchScriptPath, after.PreLaunchScriptPath);
        Text("post-exit script", before.PostExitScriptPath, after.PostExitScriptPath);
        Flag("wait for the pre-launch script", before.WaitForPreLaunchScript, after.WaitForPreLaunchScript);
        Flag("cancel the launch if the script fails", before.AbortLaunchOnScriptFailure, after.AbortLaunchOnScriptFailure);
        Flag("run scripts hidden", before.RunScriptsHidden, after.RunScriptsHidden);
        Flag("run scripts as administrator", before.RunScriptsAsAdmin, after.RunScriptsAsAdmin);
        Text("script arguments", before.ScriptArguments, after.ScriptArguments);
        Flag("skip the default scripts", before.SkipDefaultScripts, after.SkipDefaultScripts);
        if (before.PreLaunchScriptTimeoutSeconds != after.PreLaunchScriptTimeoutSeconds)
        {
            labels.Add("script timeout");
            lines.Add($"Script timeout: {before.PreLaunchScriptTimeoutSeconds}s -> {after.PreLaunchScriptTimeoutSeconds}s");
        }

        if (labels.Count == 0) return null;
        // Named by its new name: that is what the library shows from now on.
        return ($"{after.Name}: {Join(labels)} changed", string.Join("\n", lines));
    }

    /// <summary>"Hades: poster changed", for a picture chosen from the library's menu: from Automatic, or from another chosen one.</summary>
    public static (string Text, string Detail) DescribeArtChosen(string name, string label, bool wasChosen) =>
        ($"{name}: {label} changed", $"{Capital(label)}: {(wasChosen ? "a chosen one" : "automatic")} -> {(wasChosen ? "another chosen one" : "a chosen one")}");

    /// <summary>"Hades: Steam App ID changed", from the library's Change Steam App ID; null when it is the same one.</summary>
    public static (string Text, string Detail)? DescribeSteamIdChange(string name, string? was, string? now)
    {
        string before = was?.Trim() ?? string.Empty, after = now?.Trim() ?? string.Empty;
        if (string.Equals(before, after, StringComparison.Ordinal)) return null;
        return ($"{name}: Steam App ID changed", $"Steam App ID: {Shown(before)} -> {Shown(after)}");
    }

    /// <summary>"Hades was removed from the library" or "3 games were removed from the library".</summary>
    public static (string Text, string Detail) DescribeRemoved(IReadOnlyList<string> names, bool undoable)
    {
        bool one = names.Count == 1;
        string text = one ? $"{names[0]} was removed from the library" : $"{names.Count} games were removed from the library";
        string files = one ? "Its files" : "Their files";
        string detail = (one ? "" : string.Join(", ", names) + ". ")
            + (undoable ? $"{files} on disk were not touched, and the removal could be undone for a few seconds." : $"{files} on disk were not touched.");
        return (text, detail);
    }

    /// <summary>"Hades was put back in the library".</summary>
    public static string DescribeRestored(IReadOnlyList<string> names)
        => names.Count == 1 ? $"{names[0]} was put back in the library" : $"{names.Count} games were put back in the library";

    private static string Shown(string value) => string.IsNullOrWhiteSpace(value) ? "(none)" : value;

    private static string Capital(string label) => char.ToUpperInvariant(label[0]) + label[1..];

    private static string Join(IReadOnlyList<string> labels) => labels.Count switch
    {
        1 => labels[0],
        2 => $"{labels[0]} and {labels[1]}",
        _ => $"{string.Join(", ", labels.Take(labels.Count - 1))} and {labels[^1]}",
    };
}
