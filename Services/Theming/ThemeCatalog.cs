using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media;

namespace TrayTrigger.Services.Theming;

/// <summary>What the picker shows for one theme.</summary>
public sealed record ThemeInfo(string Id, string Name, string? Author, bool BuiltIn, bool IsDark);

/// <summary>
/// The themes there are: the ones inside the app, and the files in the user's Themes folder. Works out a
/// theme's colors from its base. Needs no running <see cref="System.Windows.Application"/>, so the tests use it directly.
/// </summary>
internal static class ThemeCatalog
{
    public const string DarkId = "Dark";
    public const string LightId = "Light";
    public const string HighContrastId = "High Contrast";

    /// <summary>%AppData%\TrayTrigger\Themes: where a user's own themes go.</summary>
    public static string UserThemesDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrayTrigger", "Themes");

    // ---- The two complete palettes -------------------------------------------------------------

    /// <summary>
    /// The colors in Resources/Colors.xaml: the dark theme, and the list of every name a theme may set.
    /// Read from a copy of the file each time, so it is the file's own, whatever theme is on screen.
    /// </summary>
    public static ThemePalette DarkPalette() => new(_dark ??= ReadDarkPalette());

    private static ThemePalette? _dark;

    private static ThemePalette ReadDarkPalette()
    {
        var palette = new ThemePalette();
        var file = AppResources.LoadFresh("Colors.xaml");
        foreach (var key in file.Keys.OfType<string>())
        {
            if (key.StartsWith("Brush", StringComparison.Ordinal) && file[key] is SolidColorBrush brush)
                palette[key[5..]] = brush.Color;
        }
        return palette;
    }

    private static ThemePalette? _light;

    /// <summary>The Light theme's colors: the dark ones with everything light.theme.json sets laid over them.</summary>
    public static ThemePalette LightPalette() => _light ??= ThemeDerivation.Resolve(DarkPalette(), BuiltIn(LightId)!.Colors);

    /// <summary>The palette a theme with this brightness builds on.</summary>
    public static ThemePalette BasePalette(bool isDark) => isDark ? DarkPalette() : LightPalette();

    /// <summary>Every color the theme shows: its base, the colors worked out from what it sets, and what it sets.</summary>
    public static ThemePalette Resolve(ThemeDefinition theme) => theme.Id switch
    {
        DarkId => DarkPalette(),
        LightId => LightPalette(),
        _ => ThemeDerivation.Resolve(BasePalette(theme.IsDark), theme.Colors)
    };

    // ---- Built-in themes -------------------------------------------------------------------------

    private static readonly string[] BuiltInFiles = ["light", "green", "amber", "purple", "abyssal"];

    private static IReadOnlyList<ThemeDefinition>? _builtIns;

    public static IReadOnlyList<ThemeDefinition> BuiltIns => _builtIns ??= LoadBuiltIns();

    public static ThemeDefinition? BuiltIn(string id) =>
        string.Equals(id, HighContrastId, StringComparison.OrdinalIgnoreCase)
            ? HighContrast()
            : BuiltIns.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The colors Windows has chosen for its contrast mode: the window, its text, the selection and the link
    /// color. Built each time it is asked for, because the user can change them while the app runs. With contrast
    /// mode off these are Windows' ordinary colors, which still read well.
    /// </summary>
    public static ThemeDefinition HighContrast()
    {
        Color window = System.Windows.SystemColors.WindowColor, text = System.Windows.SystemColors.WindowTextColor;
        Color highlight = System.Windows.SystemColors.HighlightColor, onHighlight = System.Windows.SystemColors.HighlightTextColor;
        Color link = System.Windows.SystemColors.HotTrackColor, gray = System.Windows.SystemColors.GrayTextColor;
        bool dark = ThemeColor.Luminance(window) < 0.5;

        var colors = new Dictionary<string, Color>
        {
            ["Background"] = window, ["Surface"] = window, ["DialogCard"] = window,
            ["SurfaceHover"] = ThemeColor.Mix(window, text, 0.18),
            ["Border"] = text, ["BorderStrong"] = text, ["BorderAccent"] = link,
            ["Accent"] = highlight, ["AccentHover"] = highlight, ["AccentPressed"] = highlight,
            ["TextOnSolid"] = onHighlight,
            ["AccentText"] = link, ["InfoText"] = link, ["Brand"] = link, ["AccentWash"] = window,
            ["TextPrimary"] = text, ["TextSecondary"] = text, ["TextMuted"] = text, ["TextSoft"] = text,
            ["TextDisabled"] = gray,
        };
        return new ThemeDefinition(HighContrastId, "High Contrast", "Windows", dark, ThemeOrigin.BuiltIn, null, colors, new ThemeBackground(ThemeBackground.None, null));
    }

    /// <summary>True while Windows' contrast mode is on.</summary>
    public static bool WindowsInHighContrast => System.Windows.SystemParameters.HighContrast;

    private static List<ThemeDefinition> LoadBuiltIns()
    {
        var list = new List<ThemeDefinition>
        {
            new(DarkId, "Dark", "TrayTrigger", IsDark: true, ThemeOrigin.BuiltIn, null, new Dictionary<string, Color>(), new ThemeBackground("dark.jpg", 1.0, "right"))
        };
        foreach (string file in BuiltInFiles)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Theme/{file}.theme.json");
            if (stream == null) { LoggingService.Warn("Theme", $"Built-in theme '{file}' is missing from the app."); continue; }
            using var reader = new StreamReader(stream);
            var result = ThemeFileReader.Parse(reader.ReadToEnd(), file, ThemeOrigin.BuiltIn);
            if (result.Theme == null) { LoggingService.Warn("Theme", $"Built-in theme '{file}' is not valid: {result.Error}"); continue; }
            list.Add(result.Theme);
        }
        return list;
    }

    /// <summary>
    /// The picture a theme shows, from its own <c>background</c> or else its base theme's. Null when there is none.
    /// A picture a theme names without a strength is shown at 60%.
    /// </summary>
    public static ResolvedBackground? ResolveBackground(ThemeDefinition theme)
    {
        string? image = theme.Background?.Image;
        double? strength = theme.Background?.Strength;
        string? position = theme.Background?.Position;
        bool embedded = theme.Origin == ThemeOrigin.BuiltIn;

        if (image == null)
        {
            // Not said: the same picture as the built-in theme this one is based on.
            var baseBackground = BuiltIn(theme.IsDark ? DarkId : LightId)?.Background;
            image = baseBackground?.Image;
            strength ??= baseBackground?.Strength;
            position ??= baseBackground?.Position;
            embedded = true;
        }

        if (string.IsNullOrWhiteSpace(image) || string.Equals(image, ThemeBackground.None, StringComparison.OrdinalIgnoreCase)) return null;
        string? directory = embedded || theme.Path == null ? null : Path.GetDirectoryName(theme.Path);
        return new ResolvedBackground(image, embedded, directory, strength ?? 0.6, position ?? BackgroundPosition.Center);
    }

    /// <summary>The id of the theme in this file: its file name, which is unique in the folder (the name inside it is not).</summary>
    public static string UserThemeId(string path) => "user:" + ThemeFileReader.FileBaseName(path);

    /// <summary>The color names a theme sets that the app has no color for: a typo, or a name from a newer version.</summary>
    public static IReadOnlyList<string> UnknownColorNames(ThemeDefinition theme)
    {
        var known = DarkPalette();
        return theme.Colors.Keys.Where(name => !known.ContainsKey(name)).OrderBy(name => name, StringComparer.Ordinal).ToList();
    }

    private static readonly string[] ExtraAccents = ["#14B8A6", "#EC4899", "#E06C75"];

    /// <summary>Colors worth offering for one color of a theme: what the built-in themes use for it, and for the accent a few more.</summary>
    public static IReadOnlyList<Color> SuggestedColors(string key)
    {
        var list = new List<Color>();
        void Add(Color c) { if (!list.Contains(c)) list.Add(c); }
        if (DarkPalette().TryGetValue(key, out var dark)) Add(dark);
        foreach (var theme in BuiltIns)
        {
            if (Resolve(theme).TryGetValue(key, out var color)) Add(color);
        }
        if (key == "Accent")
        {
            foreach (string hex in ExtraAccents)
            {
                if (ThemeColor.TryParse(hex, out var extra)) Add(extra);
            }
        }
        return list;
    }

    // ---- The user's themes ---------------------------------------------------------------------------

    /// <summary>Every theme file in the Themes folder that can be used, and a line for each that cannot.</summary>
    public static (List<ThemeDefinition> Themes, List<string> Problems) LoadUserThemes()
    {
        var themes = new List<ThemeDefinition>();
        var problems = new List<string>();
        try
        {
            if (!Directory.Exists(UserThemesDirectory)) return (themes, problems);

            foreach (string path in Directory.EnumerateFiles(UserThemesDirectory, "*.theme.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var result = ThemeFileReader.ReadFile(path, ThemeOrigin.User);
                foreach (string w in result.Warnings) LoggingService.Warn("Theme", $"{Path.GetFileName(path)}: {w}");
                if (result.Theme == null)
                {
                    problems.Add($"{Path.GetFileName(path)}: {result.Error}");
                    LoggingService.Warn("Theme", $"{Path.GetFileName(path)} was not loaded: {result.Error}");
                    continue;
                }
                // Two files with one name: the first stays, so a copy does not take over the original.
                if (themes.Any(t => string.Equals(t.Id, result.Theme.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    problems.Add($"{Path.GetFileName(path)}: another theme is already called \"{result.Theme.Name}\".");
                    continue;
                }
                themes.Add(result.Theme);
            }
        }
        catch (Exception ex)
        {
            problems.Add($"The Themes folder could not be read: {ex.Message}");
            LoggingService.Warn("Theme", $"Reading the Themes folder failed: {ex.Message}");
        }
        return (themes, problems);
    }

    /// <summary>The theme with this id, built in or the user's; null when there is none.</summary>
    public static ThemeDefinition? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return BuiltIn(id) ?? LoadUserThemes().Themes.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The text pairs of a theme that are too faint to read comfortably (below 4.5:1), as lines for the log
    /// and for Settings. Informational: a theme is still applied.
    /// </summary>
    public static List<string> ContrastProblems(ThemePalette p) => ContrastIssues(p).Select(i => i.Message).ToList();

    /// <summary>A text pair that is too faint, and the editor's colors (by their names in the theme file) that change it.</summary>
    internal sealed record ContrastIssue(string Message, string Short, string[] Fixes, string Label);

    /// <summary>
    /// The same pairs as <see cref="ContrastProblems"/>, each with the main colors to change to fix it: the dimmer text
    /// colors follow the Text color, the link color follows the Accent, and a card is the Surface.
    /// </summary>
    internal static List<ContrastIssue> ContrastIssues(ThemePalette p)
    {
        var issues = new List<ContrastIssue>();
        void Check(string text, string on, string label, params string[] colors)
        {
            if (!p.TryGetValue(text, out var fg) || !p.TryGetValue(on, out var bg)) return;
            double ratio = ThemeColor.Contrast(fg, bg);
            if (ratio < 4.5) issues.Add(new ContrastIssue($"{label} is {ratio:0.0}:1 (4.5:1 reads comfortably).", $"{label} is {ratio:0.0}:1", colors, label));
        }
        Check("TextPrimary", "Background", "Text on the window background", "TextPrimary", "Background");
        Check("TextPrimary", "Surface", "Text on a card", "TextPrimary", "Surface");
        Check("TextSecondary", "Surface", "Secondary text on a card", "TextPrimary", "Surface");
        Check("TextMuted", "Surface", "Muted text on a card", "TextPrimary", "Surface");
        Check("AccentText", "Surface", "Link text on a card", "Accent", "Surface");
        Check("TextOnSolid", "Accent", "Button text on the accent color", "Accent");
        return issues;
    }
}
