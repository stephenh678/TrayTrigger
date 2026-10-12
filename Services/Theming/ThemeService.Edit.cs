using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;

namespace TrayTrigger.Services.Theming;

/// <summary>What the editor does with the theme's background picture.</summary>
internal enum PictureChoice
{
    /// <summary>Leave it as it is.</summary>
    Keep,
    /// <summary>Use the picture file the user chose.</summary>
    Replace,
    /// <summary>No picture.</summary>
    Remove
}

/// <summary>
/// Everything the theme editor hands over to be saved. <see cref="Palette"/> is every color as the editor shows it;
/// <see cref="ChangedColors"/> are the ones the person changed, which is all a theme file that already exists is given.
/// </summary>
internal sealed record ThemeEditRequest(
    ThemeDefinition Source,
    string Name,
    bool IsDark,
    ThemePalette Palette,
    IReadOnlyDictionary<string, Color> ChangedColors,
    PictureChoice Picture,
    string? PictureSource,
    double Strength,
    string Position = BackgroundPosition.Center);

/// <summary>What <see cref="ThemeService.SaveTheme"/> did: whether it worked, what to tell the user, and the theme's file.</summary>
internal sealed record ThemeSaveOutcome(bool Ok, string Message, string? ThemePath);

public sealed partial class ThemeService
{
    /// <summary>The theme on screen now, as a definition (for Auto, the one Auto picked).</summary>
    internal ThemeDefinition Active => _active;

    /// <summary>
    /// Saves what the theme editor made. A theme of the user's is changed in its own file, line by line as text, so
    /// its comments and any fine-tuning colors it has stay. A built-in theme cannot be changed: the edit becomes a new
    /// theme file. Pictures are copied unchanged, numbered after the theme (Forest-1.jpg, Forest-2.png); nothing is
    /// overwritten or deleted. If anything fails, what this call made is taken away again and the file is untouched.
    /// </summary>
    internal ThemeSaveOutcome SaveTheme(ThemeEditRequest request)
    {
        var made = new List<string>();
        try
        {
            var outcome = request.Source.Origin == ThemeOrigin.User && request.Source.Path != null
                ? SaveExisting(request, made)
                : SaveNew(request, made);
            if (!outcome.Ok) Undo(made.ToArray());
            return outcome;
        }
        catch (Exception ex)
        {
            Undo(made.ToArray());
            LoggingService.Warn("Theme", $"Saving the theme failed: {ex.Message}");
            return new ThemeSaveOutcome(false, "The theme could not be saved: " + ex.Message, null);
        }
    }

    private ThemeSaveOutcome SaveExisting(ThemeEditRequest r, List<string> made)
    {
        string path = r.Source.Path!;
        string directory = Path.GetDirectoryName(path)!;
        string baseName = ThemeFileReader.FileBaseName(path);
        string text = File.ReadAllText(path);

        string? edited = text;
        if (r.Name != r.Source.Name) edited = ThemeFileEditor.SetString(edited, "name", r.Name);
        if (edited != null && r.IsDark != r.Source.IsDark) edited = ThemeFileEditor.SetString(edited, "base", r.IsDark ? "dark" : "light");
        foreach (var (name, color) in r.ChangedColors)
        {
            if (edited == null) break;
            edited = ThemeFileEditor.SetColor(edited, name, color);
        }

        string? image = r.Source.Background?.Image;
        switch (r.Picture)
        {
            case PictureChoice.Replace when edited != null:
                image = PlacePicture(directory, baseName, r.PictureSource!, made);
                edited = ThemeFileEditor.SetImage(edited, image);
                break;
            case PictureChoice.Remove when edited != null:
                image = ThemeBackground.None;
                edited = ThemeFileEditor.SetImage(edited, image);
                break;
        }

        // What the file says about the picture once it is as edited (no "image" line at all means the base theme's picture, with
        // its strength and position; a picture of its own with no "strength" or "position" line is 60% and centered). A line
        // is only written when that is not what the window showed.
        var shown = edited != null && ThemeFileReader.Parse(edited, baseName, ThemeOrigin.User, path).Theme is { } themeNow
            ? ThemeCatalog.ResolveBackground(themeNow) : null;
        if (shown != null)
        {
            if (Math.Abs(r.Strength - shown.Strength) > 0.004) edited = ThemeFileEditor.SetStrength(edited!, r.Strength);
            if (edited != null && shown.Position != r.Position) edited = ThemeFileEditor.SetPosition(edited, r.Position);
        }

        // The edit has to read back as exactly what was asked, or nothing is written.
        if (edited == null || !ReadsBackAs(edited, baseName, path, r, image))
            return new ThemeSaveOutcome(false, "The theme file could not be changed safely, so nothing was changed. You can edit the file yourself.", null);

        WriteWhole(path, edited);
        WatchUserThemes();
        LoggingService.Info("Theme", $"Saved the theme {baseName}.");
        return new ThemeSaveOutcome(true, $"{r.Name} was saved.", path);
    }

    private ThemeSaveOutcome SaveNew(ThemeEditRequest r, List<string> made)
    {
        string? name = CleanThemeName(r.Name, out string? error);
        if (name == null) return new ThemeSaveOutcome(false, error ?? "Give the theme a name.", null);

        string directory = ThemeCatalog.UserThemesDirectory;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name + ".theme.json");
        if (File.Exists(path)) return new ThemeSaveOutcome(false, $"There is already a theme called \"{name}\". Choose another name.", null);

        (string Image, double Strength)? background = null;
        switch (r.Picture)
        {
            case PictureChoice.Replace:
                background = (PlacePicture(directory, name, r.PictureSource!, made), r.Strength);
                break;
            case PictureChoice.Keep when ThemeCatalog.ResolveBackground(r.Source) is { } resolved:
                string kept = $"{name}-{NextPictureNumber(directory, name)}{Path.GetExtension(resolved.Image).ToLowerInvariant()}";
                string target = Path.Combine(directory, kept);
                // The editor showed this picture, so a theme saved without it would not be what was asked for.
                if (!CopyPicture(resolved, target))
                    return new ThemeSaveOutcome(false, "The theme's picture could not be copied, so nothing was saved. Remove the picture or choose another, then save again.", null);
                made.Add(target);
                background = (kept, r.Strength);
                break;
        }

        string text = ThemeFileTemplate.Write(name, r.Palette, r.IsDark, background, r.Position);
        if (!ReadsBackAs(text, name, path, r with { Name = name }, background?.Image ?? ThemeBackground.None))
            return new ThemeSaveOutcome(false, "The theme file could not be written safely, so nothing was saved.", null);

        WriteWhole(path, text);
        WatchUserThemes();
        LoggingService.Info("Theme", $"Saved the new theme {name}.");
        return new ThemeSaveOutcome(true, $"{name} was saved.", path);
    }

    /// <summary>
    /// Replaces a file's text in one step: written beside it, then moved over it, so a crash or a full disk part-way leaves
    /// the old file and not half of the new one (a theme file the user has put comments in is not easy to get back).
    /// </summary>
    private static void WriteWhole(string path, string text)
    {
        string temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.Swallowed("Theme", ex, "removing a temporary theme file");
            }
        }
    }

    /// <summary>Whether this text, read as a theme file, is the theme the editor showed: its name, base, picture and every color.</summary>
    private static bool ReadsBackAs(string text, string fileName, string path, ThemeEditRequest r, string? image)
    {
        var parsed = ThemeFileReader.Parse(text, fileName, ThemeOrigin.User, path).Theme;
        if (parsed == null || parsed.Name != r.Name || parsed.IsDark != r.IsDark) return false;
        if (image != null && parsed.Background?.Image != image) return false;

        var resolved = ThemeCatalog.Resolve(parsed);
        return r.Palette.All(p => resolved.TryGetValue(p.Key, out var color) && color == p.Value);
    }

    /// <summary>
    /// Puts the chosen picture beside the theme file as the next number after the theme's name; a picture already in the
    /// folder under that numbering is used where it is. Returns the file name; a copy this call made is added to <paramref name="made"/>.
    /// </summary>
    private static string PlacePicture(string directory, string baseName, string source, List<string> made)
    {
        bool inPlace = string.Equals(Path.GetDirectoryName(Path.GetFullPath(source)), Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            && NumberedPicture(baseName).IsMatch(Path.GetFileName(source));
        if (inPlace) return Path.GetFileName(source);

        string picture = $"{baseName}-{NextPictureNumber(directory, baseName)}{Path.GetExtension(source).ToLowerInvariant()}";
        string target = Path.Combine(directory, picture);
        File.Copy(source, target, overwrite: false);
        made.Add(target);
        return picture;
    }

    /// <summary>The name as a file name for a theme, or null with <paramref name="error"/> saying what is wrong with it.</summary>
    internal static string? CleanThemeName(string name, out string? error)
    {
        error = null;
        name = new string(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
        if (name.Length == 0) { error = "Give the theme a name."; return null; }
        // CON, NUL, COM1 ... (with any extension) are devices to Windows: the file would be "saved" nowhere.
        if (System.Text.RegularExpressions.Regex.IsMatch(name.Split('.')[0].TrimEnd(), "^(con|prn|aux|nul|com[1-9]|lpt[1-9])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        { error = $"Windows reserves \"{name}\" for a device; choose another name."; return null; }
        return name;
    }
}
