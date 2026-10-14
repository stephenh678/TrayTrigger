using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace TrayTrigger.Services.Theming;

/// <summary>Where a theme came from.</summary>
public enum ThemeOrigin
{
    /// <summary>Ships inside the app. Cannot be edited or removed.</summary>
    BuiltIn,
    /// <summary>A file in the user's Themes folder.</summary>
    User
}

/// <summary>
/// One theme, ready to apply: what it is called, whether it is a dark or a light one, which built-in it
/// builds on, and the colors it sets. See docs/theming-plan.md.
/// </summary>
internal sealed record ThemeDefinition(
    string Id,
    string Name,
    string? Author,
    bool IsDark,
    ThemeOrigin Origin,
    string? Path,
    IReadOnlyDictionary<string, Color> Colors,
    /// <summary>The theme's picture behind the pages; null takes the base theme's.</summary>
    ThemeBackground? Background = null,
    /// <summary>What was skipped in the file (a color that is not one, a base that is neither dark nor light), for Settings to show.</summary>
    IReadOnlyList<string>? Warnings = null,
    /// <summary>The corner radii the theme sets (see <see cref="ThemeShape"/>); null leaves every radius at the app's own.</summary>
    IReadOnlyDictionary<string, CornerRadius>? Shape = null);

/// <summary>The result of reading a theme file: the theme, or why there is none, and what was skipped on the way.</summary>
internal sealed record ThemeLoadResult(ThemeDefinition? Theme, string? Error, IReadOnlyList<string> Warnings);

/// <summary>Reads theme files. Never throws: a file that cannot be used comes back as an error to show and log.</summary>
internal static class ThemeFileReader
{
    /// <summary>The newest file format this version understands.</summary>
    public const int CurrentSchema = 1;

    /// <summary>Larger than any real theme; a file this big is not one.</summary>
    private const int MaxBytes = 64 * 1024;

    private static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static ThemeLoadResult ReadFile(string path, ThemeOrigin origin)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxBytes) return Fail($"{info.Name} is too large to be a theme.");
            return Parse(File.ReadAllText(path), FileBaseName(path), origin, path);
        }
        catch (Exception ex)
        {
            return Fail($"Could not read {System.IO.Path.GetFileName(path)}: {ex.Message}");
        }
    }

    /// <summary>A theme file's name without ".theme.json": what a theme of the user's is known by.</summary>
    public static string FileBaseName(string path)
    {
        string file = System.IO.Path.GetFileName(path);
        const string suffix = ".theme.json";
        return file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? file[..^suffix.Length] : System.IO.Path.GetFileNameWithoutExtension(file);
    }

    /// <param name="fileName">
    /// The file's name without ".theme.json". It is what a theme of the user's is identified by, so that renaming the
    /// <c>name</c> inside the file, or copying the file, never changes or duplicates which theme is selected; it is
    /// also the name shown when the file does not give one.
    /// </param>
    public static ThemeLoadResult Parse(string json, string fileName, ThemeOrigin origin, string? path = null)
    {
        var warnings = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json, Options);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail("A theme file is a JSON object.");

            int schema = root.TryGetProperty("schema", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out int sv) ? sv : 1;
            if (schema > CurrentSchema)
                return Fail($"This theme is written for a newer TrayTrigger (format {schema}; this one reads up to {CurrentSchema}).");

            string name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString())
                ? n.GetString()!.Trim()
                : fileName;
            string? author = root.TryGetProperty("author", out var au) && au.ValueKind == JsonValueKind.String ? au.GetString() : null;

            string baseName = root.TryGetProperty("base", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString()!.Trim().ToLowerInvariant() : "dark";
            if (baseName is not ("dark" or "light"))
            {
                warnings.Add($"base \"{baseName}\" is not dark or light; using dark.");
                baseName = "dark";
            }

            var colors = new Dictionary<string, Color>(StringComparer.Ordinal);
            if (root.TryGetProperty("colors", out var c) && c.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in c.EnumerateObject())
                {
                    string key = prop.Name.StartsWith("Brush", StringComparison.Ordinal) ? prop.Name[5..] : prop.Name;
                    if (prop.Value.ValueKind != JsonValueKind.String || !ThemeColor.TryParse(prop.Value.GetString(), out var color))
                    {
                        warnings.Add($"{prop.Name}: \"{prop.Value}\" is not a color (use #RRGGBB or #AARRGGBB); skipped.");
                        continue;
                    }
                    colors[key] = color;
                }
            }

            ThemeBackground? background = null;
            if (root.TryGetProperty("background", out var bg) && bg.ValueKind == JsonValueKind.Object)
            {
                string? image = bg.TryGetProperty("image", out var im) && im.ValueKind == JsonValueKind.String ? im.GetString()?.Trim() : null;
                if (image is { Length: 0 }) image = ThemeBackground.None;
                double? strength = null;
                if (bg.TryGetProperty("strength", out var st) && st.ValueKind == JsonValueKind.Number && st.TryGetDouble(out double strengthValue))
                    strength = Math.Clamp(strengthValue, 0, 1);
                else if (bg.TryGetProperty("strength", out _))
                    warnings.Add("background.strength is not a number from 0 to 1; skipped.");
                string? position = null;
                if (bg.TryGetProperty("position", out var po))
                {
                    position = po.ValueKind == JsonValueKind.String ? BackgroundPosition.Parse(po.GetString()) : null;
                    if (position == null)
                        warnings.Add($"background.position is not one of {string.Join(", ", BackgroundPosition.All)}; skipped.");
                }
                background = new ThemeBackground(image, strength, position);
            }

            Dictionary<string, CornerRadius>? shape = null;
            if (root.TryGetProperty("shape", out var sh))
            {
                if (sh.ValueKind == JsonValueKind.Object) shape = ThemeShape.Read(sh, warnings);
                else warnings.Add("shape must be an object of radius names; skipped.");
            }

            string id = origin == ThemeOrigin.BuiltIn ? name : "user:" + fileName;
            return new ThemeLoadResult(
                new ThemeDefinition(id, name, author, baseName == "dark", origin, path, colors, background, warnings, shape),
                null, warnings);
        }
        catch (JsonException ex)
        {
            return Fail($"Not valid JSON: {ex.Message}");
        }
    }

    private static ThemeLoadResult Fail(string error) => new(null, error, Array.Empty<string>());
}
