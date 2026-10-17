using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TrayTrigger.Services.Theming;

/// <summary>
/// What a theme file says about its background picture. Null fields mean "not said": the theme takes the
/// built-in theme it is based on. An image of "none" turns the picture off.
/// </summary>
internal sealed record ThemeBackground(string? Image, double? Strength, string? Position = null)
{
    public const string None = "none";
}

/// <summary>
/// Which part of a picture stays in view when the window is a different shape from it and the picture is cropped.
/// "left" keeps the left edge and crops the right; "top" keeps the top edge; "center" crops both sides equally.
/// </summary>
internal static class BackgroundPosition
{
    public const string Center = "center";

    public static readonly string[] All = ["center", "left", "right", "top", "bottom"];

    /// <summary>The position written as <paramref name="text"/> (any case), or null when it is not one.</summary>
    public static string? Parse(string? text) =>
        All.FirstOrDefault(p => string.Equals(p, text?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static HorizontalAlignment Horizontal(string position) => position switch
    {
        "left" => HorizontalAlignment.Left,
        "right" => HorizontalAlignment.Right,
        _ => HorizontalAlignment.Center,
    };

    public static VerticalAlignment Vertical(string position) => position switch
    {
        "top" => VerticalAlignment.Top,
        "bottom" => VerticalAlignment.Bottom,
        _ => VerticalAlignment.Center,
    };
}

/// <summary>A theme's background once its base is taken into account: which picture, from where, how strongly, and which part stays in view.</summary>
internal sealed record ResolvedBackground(string Image, bool Embedded, string? Directory, double Strength, string Position = BackgroundPosition.Center);

/// <summary>
/// Reads a theme's background picture. The picture is checked (type, size) and decoded once at the size it will
/// be shown, so a huge file cannot slow the app down; a file that cannot be used gives a reason, never a crash.
/// </summary>
internal static class ThemeBackgroundLoader
{
    /// <summary>
    /// The most pixels a picture is kept at: one 2560 x 1440 screen, about 15 MB decoded. A larger picture is shrunk to
    /// this area whatever its shape, so a portrait or square picture costs no more memory than a landscape one.
    /// </summary>
    public const long MaxDecodePixels = 2560L * 1440;

    /// <summary>Larger than any wallpaper-style JPEG worth using; a file this big is not a good background.</summary>
    public const long MaxFileBytes = 20L * 1024 * 1024;

    /// <summary>8K. A picture with more pixels than this (a huge PNG compresses to a few MB) would take seconds to decode on the UI thread.</summary>
    public const long MaxSourcePixels = 7680L * 4320;

    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".bmp"];

    public static bool IsPictureFile(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The full path of a picture a theme names, when the name is a plain file name and the file would be in <paramref name="directory"/>,
    /// the theme's own folder. Null for a path, a network address or a name that is not valid. Nothing on disk (or the network) is touched,
    /// so a name from a theme file can be checked here before anything opens it.
    /// </summary>
    public static string? BesideTheme(string directory, string image)
    {
        try
        {
            if (string.IsNullOrEmpty(image) || image != Path.GetFileName(image)) return null;
            string path = Path.GetFullPath(Path.Combine(directory, image));
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(path) ?? string.Empty),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), StringComparison.OrdinalIgnoreCase) ? path : null;
        }
        catch (ArgumentException)
        {
            // A name the file system cannot take is just "not beside the theme"; the caller says the picture was not found.
            return null;
        }
    }

    /// <summary>The picture, frozen; or null with <paramref name="problem"/> saying why there is none.</summary>
    public static BitmapSource? Load(ResolvedBackground background, out string? problem, long maxPixels = MaxDecodePixels)
    {
        problem = null;
        try
        {
            if (!IsPictureFile(background.Image))
            {
                problem = $"{background.Image}: a background picture is a JPG, PNG or BMP file.";
                return null;
            }

            if (background.Embedded)
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"ThemeImage/{background.Image}");
                if (stream == null) { problem = $"{background.Image}: not found in the app."; return null; }
                return Decode(stream, maxPixels);
            }

            string path = Path.GetFullPath(Path.Combine(background.Directory ?? string.Empty, background.Image));
            // The picture lives beside the theme file: a name that climbs out of the folder is refused.
            // Compared without a trailing \ on either side, which a drive root ("D:\") keeps.
            if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(path) ?? string.Empty),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(background.Directory ?? string.Empty)), StringComparison.OrdinalIgnoreCase))
            {
                problem = $"{background.Image}: the picture has to be in the same folder as the theme file.";
                return null;
            }
            var info = new FileInfo(path);
            if (!info.Exists) { problem = $"{background.Image}: the picture is not in the Themes folder."; return null; }
            if (info.Length > MaxFileBytes) { problem = $"{background.Image}: larger than {MaxFileBytes / (1024 * 1024)} MB."; return null; }

            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Decode(file, maxPixels);
        }
        catch (Exception ex)
        {
            problem = $"{background.Image}: could not be read ({ex.Message}).";
            LoggingService.Warn("Theme", $"Background picture '{background.Image}' failed: {ex.Message}");
            return null;
        }
    }

    private static BitmapSource Decode(Stream source, long maxPixels)
    {
        // Held in memory so the file is not kept open, and so the stream can be read twice.
        using var memory = new MemoryStream();
        source.CopyTo(memory);

        memory.Position = 0;
        var header = BitmapDecoder.Create(memory, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        int width = header.PixelWidth, height = header.PixelHeight;
        if ((long)width * height > MaxSourcePixels)
            throw new InvalidDataException($"{width} x {height} pixels is more than the {MaxSourcePixels / 1_000_000} megapixels a background can be.");
        int turn = UprightTurn(header);

        memory.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.StreamSource = memory;
        // Setting only the width keeps the picture's shape. A turn does not change how many pixels there are.
        if ((long)width * height > maxPixels)
            image.DecodePixelWidth = Math.Max(1, (int)(width * Math.Sqrt((double)maxPixels / ((double)width * height))));
        image.EndInit();
        image.Freeze();
        if (turn == 0) return image;

        var upright = new CachedBitmap(new TransformedBitmap(image, new RotateTransform(turn)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        upright.Freeze();
        return upright;
    }

    /// <summary>
    /// How many degrees clockwise a picture has to be turned to stand upright, from the camera's orientation tag
    /// (a phone photo taken on its side is stored sideways). 0 when there is no tag, when it is one of the rare
    /// mirrored kinds, or when it cannot be read. The frame's stream has to be open.
    /// </summary>
    internal static int UprightTurn(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.GetQuery("/app1/ifd/{ushort=274}") is ushort orientation)
                return orientation switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed("Theme", ex, "reading a picture's orientation");
        }
        return 0;
    }
}

/// <summary>
/// The picture on screen behind the main window's pages, for the window to bind to. The window shows
/// <see cref="Image"/> under a layer of the theme's own background color that is <see cref="TintOpacity"/> thick.
/// </summary>
public sealed class ThemeBackgroundState : INotifyPropertyChanged
{
    public static ThemeBackgroundState Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    private BitmapSource? _image;
    private double _tintOpacity;
    private string _position = BackgroundPosition.Center;

    /// <summary>The picture, or null for none (and then nothing is drawn).</summary>
    public BitmapSource? Image => _image;

    /// <summary>How much of the page's background color covers the picture: 0 shows it fully, 1 hides it.</summary>
    public double TintOpacity => _tintOpacity;

    public bool HasImage => _image != null;

    /// <summary>
    /// Where the picture is anchored when it is cropped. The window has to say so: a picture scaled up to fill is larger
    /// than its slot, and WPF draws that from the top left unless it is told to center it.
    /// </summary>
    public HorizontalAlignment HorizontalAlign => BackgroundPosition.Horizontal(_position);
    public VerticalAlignment VerticalAlign => BackgroundPosition.Vertical(_position);

    internal void Set(BitmapSource? image, double strength, string position = BackgroundPosition.Center)
    {
        _image = image;
        _position = position;
        _tintOpacity = Math.Clamp(1 - strength, 0, 1);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}
