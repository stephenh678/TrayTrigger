using System;
using System.Collections.Generic;
using System.Linq;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// Every change to what TrayTrigger changes for performance is recorded in Activity &amp; History
/// as a Change, even though the user made it: a game's Performance Profile, CPU Cores and DLSS
/// Override, a tier's tweaks, the profile new games get. They decide what TrayTrigger does to the
/// PC, so their history is the record of what it was told to do. The owner's rule, 2026-10-04.
/// System tweaks are recorded by <c>SystemViewModel</c>.
/// </summary>
public static class PerformanceActivity
{
    public static void GameChanged(GameEntry game, PerformanceProfileMode was, PerformanceProfileMode now) =>
        GamesChanged([(game, was)], now);

    /// <summary>Profile changes for one or more games: one line for the change, the games in the detail.</summary>
    public static void GamesChanged(IReadOnlyList<(GameEntry Game, PerformanceProfileMode Was)> changed, PerformanceProfileMode now) =>
        Record(DescribeGamesChanged(changed, now, DateTime.UtcNow), changed.Select(c => c.Game).ToList());

    public static void GameChanged(GameEntry game, CpuAffinityMode was, CpuAffinityMode now) =>
        GamesChanged([(game, was)], now);

    public static void GamesChanged(IReadOnlyList<(GameEntry Game, CpuAffinityMode Was)> changed, CpuAffinityMode now) =>
        Record(DescribeGamesChanged(changed, now, DateTime.UtcNow), changed.Select(c => c.Game).ToList());

    internal static (string Text, string? Detail, string GroupKey) DescribeGamesChanged(
        IReadOnlyList<(GameEntry Game, PerformanceProfileMode Was)> changed, PerformanceProfileMode now, DateTime utcNow) =>
        Describe("profile", "profile", changed, now, m => m.ToString(), utcNow);

    internal static (string Text, string? Detail, string GroupKey) DescribeGamesChanged(
        IReadOnlyList<(GameEntry Game, CpuAffinityMode Was)> changed, CpuAffinityMode now, DateTime utcNow) =>
        Describe("CPU Cores", "cpu", changed, now, CpuTopology.MenuLabel, utcNow);

    public static void GameChanged(GameEntry game, HdrMode was, HdrMode now) =>
        Record(DescribeGamesChanged([(game, was)], now, DateTime.UtcNow), [game]);

    internal static (string Text, string? Detail, string GroupKey) DescribeGamesChanged(
        IReadOnlyList<(GameEntry Game, HdrMode Was)> changed, HdrMode now, DateTime utcNow) =>
        Describe("HDR", "hdr", changed, now, HdrModes.Label, utcNow);

    /// <summary>
    /// One game: "Elden Ring's profile changed from Off to Aggressive", grouped per game and setting
    /// so its row shows where it stands now. Several: one line, each game and what it was in the detail.
    /// </summary>
    private static (string Text, string? Detail, string GroupKey) Describe<T>(string setting, string key,
        IReadOnlyList<(GameEntry Game, T Was)> changed, T now, Func<T, string> label, DateTime utcNow)
    {
        var real = changed.Where(c => !EqualityComparer<T>.Default.Equals(c.Was, now)).ToList();
        if (real.Count == 0) return (string.Empty, null, string.Empty);
        if (real.Count == 1)
        {
            var (game, was) = real[0];
            return ($"{game.Name}'s {setting} changed from {label(was)} to {label(now)}", null, $"{key}.game|{game.Id}");
        }
        string detail = string.Join(Environment.NewLine, real.Select(c => $"{c.Game.Name} - was {label(c.Was)}"));
        string capital = char.ToUpperInvariant(setting[0]) + setting[1..];
        return ($"{capital} set to {label(now)} for {real.Count} games", detail, $"{key}.games|{utcNow.Ticks}");
    }

    /// <summary>DLSS Override switched for one or more games (Edit Game, the card's menu, the batch menu).</summary>
    public static void DlssChanged(IReadOnlyList<GameEntry> games, bool on) =>
        Record(DescribeDlssChanged(games.Select(g => (g.Id, g.Name)).ToList(), on, DateTime.UtcNow), games);

    internal static (string Text, string? Detail, string GroupKey) DescribeDlssChanged(
        IReadOnlyList<(string Id, string Name)> games, bool on, DateTime utcNow)
    {
        if (games.Count == 0) return (string.Empty, null, string.Empty);
        string state = on ? "on" : "off";
        string? back = on ? null : "NVIDIA's settings are back as they were.";
        if (games.Count == 1)
            return ($"DLSS Override turned {state} for {games[0].Name}", back, $"dlss.game|{games[0].Id}");
        string names = string.Join(Environment.NewLine, games.Select(g => g.Name));
        return ($"DLSS Override turned {state} for {games.Count} games", back == null ? names : $"{back}{Environment.NewLine}{names}",
            $"dlss.games|{utcNow.Ticks}");
    }

    /// <summary>A tweak switched on or off in a tier on the System page.</summary>
    public static void TierTweakChanged(string tier, string tweak, bool enabled) =>
        Record(DescribeTierTweakChanged(tier, tweak, enabled), []);

    internal static (string Text, string? Detail, string GroupKey) DescribeTierTweakChanged(string tier, string tweak, bool enabled)
    {
        string name = tweak.Replace(" (profile)", string.Empty).Trim();
        string? detail = tier == nameof(PerformanceProfileMode.Optimized)
            ? "Aggressive applies every Optimized tweak too, so this changes both."
            : null;
        return ($"{tier} profile: {name} turned {(enabled ? "on" : "off")}", detail, $"profile.tier|{tier}|{name}");
    }

    public static void NewGameProfileChanged(PerformanceProfileMode was, PerformanceProfileMode now)
    {
        if (was == now) return;
        Record(($"Profile for new games changed from {was} to {now}", "Games already in the library keep theirs.", "profile.newgames"), []);
    }

    /// <summary>Settings' Reset to Defaults put profile settings back: one line, what changed in the detail.</summary>
    public static void ResetToDefaults(IReadOnlyList<string> changes)
    {
        if (changes.Count == 0) return;
        Record(("Reset to Defaults put the Performance Profile settings back to their defaults",
            string.Join(Environment.NewLine, changes), $"profile.reset|{DateTime.UtcNow.Ticks}"), []);
    }

    private static void Record((string Text, string? Detail, string GroupKey) line, IReadOnlyList<GameEntry> games)
    {
        if (line.Text.Length == 0) return;
        ActivityService.Add(ActivityLevel.Change, line.Text, subject: games.Count == 1 ? games[0].Name : null,
            detail: line.Detail, groupKey: line.GroupKey);
    }
}
