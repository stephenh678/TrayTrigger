using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace TrayTrigger.Services.Theming;

/// <summary>What <see cref="ThemePictureCheck.Inspect"/> found: whether the picture can be a background, and what to tell the user.</summary>
internal sealed record PictureCheck(bool Ok, string Message, string? Note, int Width, int Height, long Bytes);

/// <summary>
/// The rules for a picture chosen as a theme's background. TrayTrigger does not resize it: the file is used as it
/// is, so a picture that does not meet the rules is refused with what to change. Pictures put in the Themes folder
/// by hand are held to the looser limits of <see cref="ThemeBackgroundLoader"/> only.
/// </summary>
internal static class ThemePictureCheck
{
    public const int MinLongSide = 1280;
    public const int MinShortSide = 720;

    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png"];

    public static bool IsAcceptedType(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Never throws: a file that cannot be read is a refusal with a reason.</summary>
    public static PictureCheck Inspect(string path)
    {
        if (!IsAcceptedType(path))
            return Refuse("That file type is not supported. Choose a JPG or PNG picture.");

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return Refuse("That file is not there any more.");

            int width, height;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                width = frame.PixelWidth;
                height = frame.PixelHeight;
                // A phone photo taken on its side is stored sideways and shown upright: its size is the upright one.
                if (ThemeBackgroundLoader.UprightTurn(frame) is 90 or 270) (width, height) = (height, width);
            }

            string size = $"{width}×{height}";
            if (Math.Max(width, height) < MinLongSide || Math.Min(width, height) < MinShortSide)
                return new PictureCheck(false, $"{size} is too small: it would look blurry. Use at least {MinLongSide}×{MinShortSide}.", null, width, height, info.Length);
            if ((long)width * height > ThemeBackgroundLoader.MaxSourcePixels)
                return new PictureCheck(false, $"{size} is larger than 8K (7680×4320). Resize it and choose it again.", null, width, height, info.Length);
            if (info.Length > ThemeBackgroundLoader.MaxFileBytes)
                return new PictureCheck(false, $"The file is {Megabytes(info.Length)}; the limit is {Megabytes(ThemeBackgroundLoader.MaxFileBytes)}. Save it as a JPG at a lower quality and choose it again.", null, width, height, info.Length);

            // Only informational: neither stops the picture being used.
            double ratio = (double)width / height;
            var notes = new System.Collections.Generic.List<string>();
            if (ratio < 1.5 || ratio > 1.9) notes.Add("This picture will be cropped to fit the window.");
            if ((long)width * height > ThemeBackgroundLoader.MaxDecodePixels) notes.Add("Large picture: TrayTrigger keeps a scaled copy in memory.");
            string? note = notes.Count > 0 ? string.Join(' ', notes) : null;

            return new PictureCheck(true, $"{size}, {Megabytes(info.Length)}", note, width, height, info.Length);
        }
        catch (Exception ex)
        {
            // Whatever the picture decoder throws for a damaged or mislabeled file (it is not one exception type).
            LoggingService.Warn("Theme", $"Could not read the picture {path}: {ex.Message}");
            return Refuse("That picture could not be read. It may be damaged, or not really a JPG or PNG.");
        }
    }

    private static PictureCheck Refuse(string message) => new(false, message, null, 0, 0, 0);

    private static string Megabytes(long bytes) =>
        bytes < 1024 * 1024 ? $"{Math.Max(1, bytes / 1024)} KB" : (bytes / (1024.0 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
}
