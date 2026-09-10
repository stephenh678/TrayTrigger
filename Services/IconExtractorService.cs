using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace TrayTrigger.Services;

public partial class IconExtractorService
{
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHDefExtractIconW(string pszIconFile, int iIndex, uint uFlags, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIconSize);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// Extracts an executable/DLL's icon at up to 256x256, instead of the small (typically 32x32)
    /// frame Icon.ExtractAssociatedIcon returns - that default looked blurry once upscaled for
    /// display at 64px or in Compact Icons view. Returns null (falling back to the caller's own
    /// smaller-icon path) if the shell can't produce a large icon for this file.
    /// </summary>
    private static Bitmap? ExtractLargeIconBitmap(string path, int size = 256)
    {
        IntPtr hLarge = IntPtr.Zero;
        IntPtr hSmall = IntPtr.Zero;
        try
        {
            uint packedSize = (uint)size | ((uint)Math.Min(size, 48) << 16);
            int hr = SHDefExtractIconW(path, 0, 0, out hLarge, out hSmall, packedSize);
            if (hr != 0 || hLarge == IntPtr.Zero) return null;

            using var icon = Icon.FromHandle(hLarge);
            return icon.ToBitmap();
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("IconExtractorService", $"Large icon extraction failed for '{path}': {ex.Message}");
            return null;
        }
        finally
        {
            if (hLarge != IntPtr.Zero) DestroyIcon(hLarge);
            if (hSmall != IntPtr.Zero) DestroyIcon(hSmall);
        }
    }
    private readonly StorageService _storageService;

    public IconExtractorService(StorageService storageService)
    {
        _storageService = storageService;
    }

    /// <summary>
    /// Extracts or generates an icon for the game, caches it as a PNG in %LocalAppData%\TrayTrigger\Icons\{gameId}.png,
    /// and returns the absolute path to the cached PNG.
    /// </summary>
    public string ExtractAndCacheIcon(string gameId, string sourcePath, string gameName)
    {
        _storageService.EnsureDirectories();
        string cachedIconPath = Path.Combine(_storageService.IconsDirectory, $"{gameId}.png");

        try
        {
            // If source is already a valid image/icon file
            if (!string.IsNullOrEmpty(sourcePath) && File.Exists(sourcePath))
            {
                string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
                if (ext == ".png")
                {
                    if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(cachedIconPath), StringComparison.OrdinalIgnoreCase))
                    {
                        return cachedIconPath;
                    }
                    using var fs = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var img = Image.FromStream(fs);
                    SaveDownscaled(img, cachedIconPath);
                    LoggingService.Verbose("IconExtractorService", $"Icon for '{gameName}' cached from source PNG '{sourcePath}'.");
                    return cachedIconPath;
                }

                if (ext == ".ico")
                {
                    // Requesting 256x256 makes GDI+ pick the closest (i.e. largest available)
                    // frame in the .ico instead of the system's small-icon default.
                    using var ico = new Icon(sourcePath, new System.Drawing.Size(256, 256));
                    using var bmp = ico.ToBitmap();
                    bmp.Save(cachedIconPath, ImageFormat.Png);
                    LoggingService.Verbose("IconExtractorService", $"Icon for '{gameName}' cached from .ico '{sourcePath}'.");
                    return cachedIconPath;
                }

                if (ext == ".jpg" || ext == ".jpeg" || ext == ".bmp")
                {
                    using var fs = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var img = Image.FromStream(fs);
                    SaveDownscaled(img, cachedIconPath);
                    LoggingService.Verbose("IconExtractorService", $"Icon for '{gameName}' cached from source image '{sourcePath}'.");
                    return cachedIconPath;
                }

                // Prefer the large (up to 256x256) shell icon for .exe/.dll/.lnk over
                // ExtractAssociatedIcon's small default, falling back to the latter if the shell
                // couldn't produce one (e.g. an unusual file type it doesn't recognize).
                using var largeBmp = ExtractLargeIconBitmap(sourcePath);
                if (largeBmp != null)
                {
                    largeBmp.Save(cachedIconPath, ImageFormat.Png);
                    LoggingService.Verbose("IconExtractorService", $"Icon for '{gameName}' cached via shell large-icon extraction from '{sourcePath}'.");
                    return cachedIconPath;
                }

                using var associatedIcon = Icon.ExtractAssociatedIcon(sourcePath);
                if (associatedIcon != null)
                {
                    using var bmp = associatedIcon.ToBitmap();
                    bmp.Save(cachedIconPath, ImageFormat.Png);
                    LoggingService.Verbose("IconExtractorService", $"Icon for '{gameName}' cached via associated-icon fallback from '{sourcePath}'.");
                    return cachedIconPath;
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("IconExtractorService", $"Extraction failed for '{sourcePath}': {ex.Message}");
        }

        // Fallback: Generate a crisp, modern letter/badge icon
        try
        {
            GenerateFallbackIcon(cachedIconPath, gameName);
            LoggingService.Verbose("IconExtractorService", $"Icon for '{gameName}' generated as a letter-badge fallback (source: '{sourcePath}').");
            return cachedIconPath;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("IconExtractorService", $"Fallback generation failed: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// Saves a raster source as the cached icon, downscaling it first if either dimension
    /// exceeds 256px so an oversized artwork file (e.g. a Steam hero image) never becomes a
    /// multi-megabyte "icon" on disk. See M-23.
    /// </summary>
    private static void SaveDownscaled(Image img, string outputPath)
    {
        const int maxDimension = 256;
        if (img.Width <= maxDimension && img.Height <= maxDimension)
        {
            img.Save(outputPath, ImageFormat.Png);
            return;
        }

        double scale = Math.Min((double)maxDimension / img.Width, (double)maxDimension / img.Height);
        int w = Math.Max(1, (int)Math.Round(img.Width * scale));
        int h = Math.Max(1, (int)Math.Round(img.Height * scale));

        using var resized = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(resized))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(img, 0, 0, w, h);
        }
        resized.Save(outputPath, ImageFormat.Png);
    }

    /// <summary>
    /// Generates a clean dark rounded badge with the game's initial letter or icon.
    /// </summary>
    private static void GenerateFallbackIcon(string outputPath, string gameName)
    {
        const int size = 128;
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Color.Transparent);

        // Background rounded box (#26262B with subtle border)
        float pad = 4f;
        float rectSize = size - (pad * 2);
        float radius = 24f;

        using (var bgBrush = new SolidBrush(Color.FromArgb(38, 38, 43))) // #26262B
        using (var path = CreateRoundedRect(pad, pad, rectSize, rectSize, radius))
        {
            g.FillPath(bgBrush, path);
            using var borderPen = new Pen(Color.FromArgb(0, 122, 204), 3f); // #007ACC
            g.DrawPath(borderPen, path);
        }

        // Initial letter
        string letter = "?";
        if (!string.IsNullOrWhiteSpace(gameName))
        {
            letter = gameName.Trim().Substring(0, 1).ToUpperInvariant();
        }

        using var font = new Font("Segoe UI", 56, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.FromArgb(240, 240, 240));

        var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };

        g.DrawString(letter, font, textBrush, new RectangleF(0, 0, size, size), format);

        bmp.Save(outputPath, ImageFormat.Png);
    }

    private static GraphicsPath CreateRoundedRect(float x, float y, float width, float height, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + width - d, y, d, d, 270, 90);
        path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
        path.AddArc(x, y + height - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Safely loads a bitmap image from disk without locking the file on disk.
    /// Returns null if path is invalid or file does not exist.
    /// Optional decodePixelWidth scales the bitmap during decode to dramatically reduce memory consumption.
    /// </summary>
    public static BitmapImage? LoadBitmapSafely(string? path, int decodePixelWidth = 0)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream((int)fileStream.Length);
            fileStream.CopyTo(ms);
            ms.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (decodePixelWidth > 0)
            {
                bitmap.DecodePixelWidth = decodePixelWidth;
            }
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze(); // Freezes for cross-thread access and performance
            return bitmap;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("IconExtractorService", $"Failed to load bitmap from '{path}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Loads an icon from a path that could be either a real image file (a platform's own cached
    /// artwork - .ico/.png/.jpg) or an executable to extract an icon from. Every platform scanner
    /// (Steam/GOG/EA/Epic/Ubisoft) falls back to the game's own exe path as its "IconPath" when no
    /// dedicated artwork file is found - <see cref="LoadBitmapSafely"/> can only decode the former;
    /// asking WIC to decode a .exe throws "No imaging component suitable" instead of returning
    /// null, since a PE executable isn't a recognized image container at all.
    /// </summary>
    public static System.Windows.Media.ImageSource? LoadIconOrExtractFromExe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string ext = Path.GetExtension(path);
        return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)
            ? ExtractAssociatedBitmapSource(path)
            : LoadBitmapSafely(path);
    }

    /// <summary>
    /// Extracts an executable's associated icon as a frozen WPF BitmapSource, ensuring the native Win32 HICON handle is released.
    /// </summary>
    public static System.Windows.Media.ImageSource? ExtractAssociatedBitmapSource(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            // Icon.ExtractAssociatedIcon returns an Icon that owns its HICON, so disposing it
            // via `using` already destroys the handle - don't also call DestroyIcon manually,
            // that would double-destroy the same handle.
            using var ico = Icon.ExtractAssociatedIcon(path);
            if (ico == null) return null;

            var bs = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                ico.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bs.Freeze();
            return bs;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("IconExtractorService", $"Failed to extract associated icon from '{path}': {ex.Message}");
            return null;
        }
    }
}
