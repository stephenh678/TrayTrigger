using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TrayTrigger.Services.Theming;

/// <summary>What <see cref="ThemeService.ImportThemeFiles"/> did: whether it worked, what to tell the user, and the new theme's file.</summary>
internal sealed record ThemeImportOutcome(bool Ok, string Message, string? ThemePath);

public sealed partial class ThemeService
{
    private const long MaxImportBytes = 64 * 1024;

    /// <summary>
    /// Imports a theme made elsewhere: one theme file (a plain .json is saved as .theme.json) and, if there is one, its
    /// picture. The picture is one of the chosen files, or else the one the theme names beside the theme file. Nothing is
    /// overwritten: a name already in use becomes "Name (2)". If anything fails, what this call made is taken away again.
    /// </summary>
    internal ThemeImportOutcome ImportThemeFiles(IReadOnlyList<string> paths)
    {
        var made = new List<string>();
        try
        {
            string? themeFile = paths.FirstOrDefault(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
            if (themeFile == null) return new ThemeImportOutcome(false, "Choose a theme file (.theme.json or .json).", null);

            if (new FileInfo(themeFile).Length > MaxImportBytes)
                return new ThemeImportOutcome(false, $"{Path.GetFileName(themeFile)} is too large to be a theme.", null);

            string text = File.ReadAllText(themeFile);
            string hint = ThemeFileReader.FileBaseName(themeFile);
            var parsed = ThemeFileReader.Parse(text, hint, ThemeOrigin.User, themeFile);
            if (parsed.Theme == null)
                return new ThemeImportOutcome(false, $"{Path.GetFileName(themeFile)} is not a theme: {parsed.Error}", null);
            var theme = parsed.Theme;
            if (theme.Colors.Count == 0 && theme.Shape == null && theme.Background == null && theme.TextSize == null && theme.Font == null)
                return new ThemeImportOutcome(false, $"{Path.GetFileName(themeFile)} sets no colors, so it is not a theme.", null);

            string directory = ThemeCatalog.UserThemesDirectory;
            Directory.CreateDirectory(directory);
            string baseName = CleanThemeName(hint, out _) ?? CleanThemeName(theme.Name, out _) ?? "Imported theme";
            string name = baseName;
            for (int i = 2; File.Exists(Path.Combine(directory, name + ".theme.json")); i++) name = $"{baseName} ({i})";
            string path = Path.Combine(directory, name + ".theme.json");

            // The picture: one the person chose, else the one the theme names, if it is beside the theme file.
            string? picture = paths.FirstOrDefault(p => IsPictureFile(p));
            string? wanted = theme.Background?.Image;
            if (picture == null && !string.IsNullOrEmpty(wanted) && wanted != ThemeBackground.None)
            {
                string beside = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(themeFile))!, wanted);
                if (IsPictureFile(beside) && File.Exists(beside)) picture = beside;
            }

            string note = string.Empty;
            if (picture != null)
            {
                var check = ThemePictureCheck.Inspect(picture);
                if (!check.Ok) note = $" Its picture was not added: {check.Message}";
                else
                {
                    string image = PlacePicture(directory, name, picture, made);
                    string? withImage = ThemeFileEditor.SetImage(text, image) ?? AddBackground(text, image);
                    if (withImage != null && ThemeFileReader.Parse(withImage, name, ThemeOrigin.User, path).Theme?.Background?.Image == image) text = withImage;
                    else note = " Its picture could not be added to the theme file.";
                }
            }
            else if (!string.IsNullOrEmpty(wanted) && wanted != ThemeBackground.None)
                note = $" Its picture ({wanted}) was not found: choose it together with the theme file to add it.";

            if (ThemeFileReader.Parse(text, name, ThemeOrigin.User, path).Theme == null)
            {
                Undo(made.ToArray());
                return new ThemeImportOutcome(false, "The theme file could not be imported.", null);
            }

            WriteWhole(path, text);
            WatchUserThemes();
            LoggingService.Info("Theme", $"Imported the theme {name} from {themeFile}.");
            return new ThemeImportOutcome(true, $"{theme.Name} was imported.{note}", path);
        }
        catch (Exception ex)
        {
            Undo(made.ToArray());
            LoggingService.Warn("Theme", $"Importing a theme failed: {ex.Message}");
            return new ThemeImportOutcome(false, "The theme could not be imported: " + ex.Message, null);
        }
    }

    private static bool IsPictureFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png";

    /// <summary>A theme file with no "background" block: one is added with the picture, at the file's first brace.</summary>
    private static string? AddBackground(string text, string image)
    {
        int brace = text.IndexOf('{');
        if (brace < 0) return null;
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        return text.Insert(brace + 1, nl + "  \"background\": { \"image\": " + ThemeFileTemplate.Quote(image) + " }," );
    }
}
