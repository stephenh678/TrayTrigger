using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace TrayTrigger.Services.Theming;

/// <summary>
/// Writes a theme file for people to read: the few colors that matter most come first, each line says what it
/// colors, and the colors the app can work out for itself come last. The reader skips the // comments.
/// </summary>
internal static class ThemeFileTemplate
{
    private sealed record Entry(string Name, string Comment);

    private static readonly Entry[] Main =
    [
        new("Background", "Window and page color. Cards, hover, dialogs and borders are worked out from it."),
        new("Surface", "Cards and panels on the page. Worked out from Background while this line is commented out."),
        new("TextPrimary", "Main text. The dimmer text colors are worked out from it."),
        new("Accent", "Buttons, the selected tab and links. Hover, pressed and tints are worked out from it."),
        new("Danger", "Errors and delete buttons. Their tints and borders are worked out from it."),
    ];

    private static readonly Entry[] Optional =
    [
        new("Warning", "Warnings and the search highlight."),
        new("SuccessText", "Success messages. Their tint and solid fill are worked out from it."),
    ];

    private static readonly Entry[] FineTuning =
    [
        new("SurfaceHover", "Cards and rows under the mouse."),
        new("DialogCard", "Cards inside dialogs (Edit Game, Edit Tool)."),
        new("Border", "Thin lines around cards and between rows."),
        new("BorderStrong", "Stronger lines: text boxes and buttons."),
        new("BorderAccent", "Line around highlighted or selected cards."),
        new("AccentHover", "Accent buttons under the mouse."),
        new("AccentPressed", "Accent buttons while clicked."),
        new("AccentText", "Links, icons and captions in the accent color."),
        new("AccentWash", "Faint accent tint behind selected rows and notes."),
        new("Brand", "The \"Trigger\" half of the TrayTrigger name."),
        new("InfoText", "Information messages."),
        new("TextOnSolid", "Text on solid accent, danger and success buttons."),
        new("TextSecondary", "Second-level text: descriptions and sub-titles."),
        new("TextMuted", "Quiet text: hints and captions."),
        new("TextSoft", "Text between Secondary and Primary."),
        new("TextDisabled", "Text on things that are switched off."),
        new("DangerWash", "Faint red tint behind an error."),
        new("DangerBorder", "Line around an error."),
        new("DangerSolid", "Solid red buttons (Remove, Force Close)."),
        new("DangerSolidHover", "Solid red buttons under the mouse."),
        new("WarningWash", "Faint tint behind a warning."),
        new("WarningBorder", "Line around a warning."),
        new("Highlight", "Search matches."),
        new("SuccessWash", "Faint green tint behind a success message."),
        new("SuccessSolid", "Solid green badges."),
        new("Gold", "Stars and favorites."),
        new("BadgePurple", "Purple badges."),
        new("BadgePurpleWash", "Tint behind purple badges."),
        new("Scrim", "The darkening behind a dialog."),
        new("OverlayLine", "Thin lines drawn over game art."),
        new("ArtText", "Text over game art: the title."),
        new("ArtTextSecondary", "Text over game art: second line."),
        new("ArtTextMuted", "Text over game art: small print."),
    ];

    /// <summary>Every color name the file can list, in the order the file writes them.</summary>
    internal static IEnumerable<string> Names => Main.Concat(Optional).Concat(FineTuning).Select(e => e.Name);

    public static string Write(string name, ThemePalette palette, bool isDark, (string Image, double Strength)? background = null, string position = BackgroundPosition.Center)
    {
        var text = new StringBuilder();
        text.AppendLine("{");
        text.AppendLine("  \"schema\": 1,");
        text.AppendLine($"  \"name\": {Quote(name)},");
        text.AppendLine($"  \"base\": \"{(isDark ? "dark" : "light")}\",  // \"dark\" or \"light\": the starting point for any color you leave out");
        text.AppendLine();
        text.AppendLine("  // The picture behind the pages. The file goes in this folder; \"none\" shows no picture.");
        text.AppendLine("  // Use a JPG or PNG, at least 1280 x 720. Settings > Appearance > Edit theme... fills this in for you.");
        text.AppendLine("  \"background\": {");
        text.AppendLine($"    \"image\": {Quote(background?.Image ?? ThemeBackground.None)},");
        text.AppendLine($"    \"position\": {Quote(position)},  // Which part stays in view when the window is a different shape: center, left, right, top or bottom");
        text.AppendLine($"    \"strength\": {(background?.Strength ?? 0.6).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}  // 0 hides the picture, 1 shows it at full brightness");
        text.AppendLine("  },");
        text.AppendLine();
        text.AppendLine("  // Each color line ends with a comma on purpose, so a line can be turned on or off without breaking the file.");
        text.AppendLine("  \"colors\": {");

        // A color the file lists is used as written, and then it no longer follows the colors it is worked out from.
        // So only what the theme really sets is written as a line; a color the app works out for itself is written
        // as a comment (remove the // to set it). What is worked out is the same as what Resolve does when the file is read.
        var anchors = palette.Where(p => ThemeDerivation.Anchors.ContainsKey(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        var workedOut = ThemeDerivation.Resolve(ThemeCatalog.BasePalette(isDark), anchors);

        // What the app knows that this file has no line for (a color added in a later version) still goes in, at the end.
        var listed = new HashSet<string>(Names, StringComparer.Ordinal);
        var extra = palette.Keys.Where(k => !listed.Contains(k)).OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => new Entry(k, "")).ToArray();

        var sections = new (string Heading, Entry[] Entries)[]
        {
            ("Main colors: change these first. Write each as #RRGGBB.", Main),
            ("Optional.", Optional),
            ("Fine-tuning. TrayTrigger works these out from the colors above, so they are commented out (//). Remove the // to set one yourself.", FineTuning.Concat(extra).ToArray()),
        };

        for (int s = 0; s < sections.Length; s++)
        {
            if (s > 0) text.AppendLine();
            text.AppendLine($"    // ---- {sections[s].Heading}");
            foreach (var entry in sections[s].Entries)
            {
                if (!palette.TryGetValue(entry.Name, out var color)) continue;
                // A color that differs from what would be worked out is the theme's own: it stays a line, so the file reads back as the theme it came from.
                bool set = ThemeDerivation.Anchors.ContainsKey(entry.Name) || !workedOut.TryGetValue(entry.Name, out var derived) || derived != color;
                text.AppendLine($"    {(set ? "" : "// ")}{Quote(entry.Name)}: \"{ThemeColor.Format(color)}\",{(entry.Comment.Length > 0 ? "  // " + entry.Comment : "")}");
            }
        }

        text.AppendLine("  }");
        text.AppendLine("}");
        return text.ToString();
    }

    // Written as typed: the default would turn an apostrophe or an accented letter into a \u0027 or \u00EB code that is hard to read in a file meant to be edited.
    private static readonly JsonSerializerOptions Plain = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>A string as JSON text, in quotes.</summary>
    internal static string Quote(string value) => JsonSerializer.Serialize(value, Plain);
}
