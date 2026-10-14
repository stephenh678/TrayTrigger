using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

/// <summary>
/// A game card in the theme editor's live preview, drawn at the library's own size with a real game's name, playtime and cover
/// where the player's library has them (the three most played with a cover), and made-up games with a plain colored cover where
/// it does not. The three are shown as a card at rest, one under the mouse and one selected.
/// </summary>
public sealed class PosterSample
{
    public string Name { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string PlaytimeDisplay { get; init; } = string.Empty;
    public string LastPlayedDisplay { get; init; } = string.Empty;

    /// <summary>The game's own cover; null for a made-up game, which has <see cref="ArtBrush"/> instead.</summary>
    public BitmapImage? Cover { get; init; }

    public Brush ArtBrush { get; init; } = Brushes.Transparent;
    public bool HasCover => Cover != null;

    /// <summary>What is said under the card: its state.</summary>
    public string Caption { get; private init; } = string.Empty;

    public bool IsHover { get; private init; }
    public bool IsSelected { get; private init; }

    private static readonly (string Name, string Category, string Playtime, string LastPlayed, string From, string To)[] MadeUp =
    [
        ("Cyberpunk 2077", "RPG", "12h 40m played", "Today", "#2b4a7a", "#c9a227"),
        ("Elden Ring", "Action", "86h 5m played", "Yesterday", "#3a2f1f", "#8a6a3a"),
        ("Hades II", "Roguelike", "23h 15m played", "3 days ago", "#4a1f3a", "#c0504d"),
    ];

    private static readonly string[] Captions = ["Normal", "Mouse over", "Selected"];

    /// <summary>The three cards: the player's own most played games with a cover, then made-up ones for any that are missing.</summary>
    internal static IReadOnlyList<PosterSample> Build(IEnumerable<GameEntry>? library)
    {
        var real = new List<(GameEntry Game, BitmapImage Cover)>();
        try
        {
            var candidates = (library ?? [])
                .Where(g => !string.IsNullOrWhiteSpace(g.CoverImagePath) && File.Exists(g.CoverImagePath))
                .OrderByDescending(g => g.CumulativePlaytimeMinutes)
                .GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First());
            foreach (var game in candidates)
            {
                var cover = IconExtractorService.LoadBitmapSafely(game.CoverImagePath, decodePixelWidth: 368);
                if (cover != null) real.Add((game, cover));
                if (real.Count == 3) break;
            }
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed("Theme", ex, "choosing games for the theme preview");
        }

        var samples = new List<PosterSample>();
        for (int i = 0; i < 3; i++)
        {
            bool isReal = i < real.Count;
            var made = MadeUp[i];
            samples.Add(new PosterSample
            {
                Name = isReal ? real[i].Game.Name : made.Name,
                Category = isReal ? real[i].Game.Category : made.Category,
                PlaytimeDisplay = isReal ? real[i].Game.PlaytimeDisplay : made.Playtime,
                LastPlayedDisplay = isReal ? real[i].Game.LastPlayedDisplay : made.LastPlayed,
                Cover = isReal ? real[i].Cover : null,
                ArtBrush = isReal ? Brushes.Transparent : Gradient(made.From, made.To),
                Caption = Captions[i],
                IsHover = i == 1,
                IsSelected = i == 2,
            });
        }
        return samples;
    }

    /// <summary>The games in the player's library, when the main window is up; none in a test or a capture with no window.</summary>
    internal static IEnumerable<GameEntry> LibraryGames() =>
        Application.Current?.MainWindow?.DataContext is MainViewModel main ? main.Games.Select(g => g.Game).ToList() : [];

    private static Brush Gradient(string from, string to)
    {
        var brush = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(from), (Color)ColorConverter.ConvertFromString(to), 60);
        brush.Freeze();
        return brush;
    }
}
