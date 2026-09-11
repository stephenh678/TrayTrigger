using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TrayTrigger.Tools.IconGen;

/// <summary>
/// Renders the TrayTrigger "TT" mark (white T over blue T on a dark rounded tile) at every size
/// Windows asks for, with per-size geometry so the 16-32 px tray/taskbar variants stay legible,
/// and packs them into app_icon.ico + a 256 px app_icon.png.
/// </summary>
internal static class Program
{
    private static readonly int[] IcoSizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    [STAThread]
    private static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : "Assets";
        Directory.CreateDirectory(outDir);

        var frames = new Dictionary<int, BitmapSource>();
        foreach (int size in IcoSizes)
        {
            frames[size] = RenderMark(size);
        }

        WriteIco(Path.Combine(outDir, "app_icon.ico"), frames);
        SavePng(frames[256], Path.Combine(outDir, "app_icon.png"));
        Console.WriteLine($"Wrote {Path.Combine(outDir, "app_icon.ico")} ({IcoSizes.Length} sizes) and app_icon.png");

        int previewIdx = Array.IndexOf(args, "preview");
        if (previewIdx >= 0)
        {
            string previewDir = args.Length > previewIdx + 1 ? args[previewIdx + 1] : outDir;
            Directory.CreateDirectory(previewDir);
            foreach (var (size, bmp) in frames)
            {
                SavePng(Upscale(bmp, Math.Max(1, 128 / size)), Path.Combine(previewDir, $"mark_{size}.png"));
            }
            SavePng(RenderTrayStrip(frames), Path.Combine(previewDir, "tray_strip.png"));
            Console.WriteLine($"Preview sheets written to {previewDir}");
        }

        int glyphIdx = Array.IndexOf(args, "glyphs");
        if (glyphIdx >= 0 && args.Length > glyphIdx + 1)
        {
            string glyphOut = args[glyphIdx + 1];
            var codes = args.Skip(glyphIdx + 2).ToArray();
            SavePng(RenderGlyphSheet(codes), glyphOut);
            Console.WriteLine($"Glyph sheet written to {glyphOut}");
        }

        return 0;
    }

    // ---------------------------------------------------------------- mark geometry

    private sealed record MarkStyle(
        double CornerRadius,   // fraction of size
        double Border,         // px at this size, 0 = none
        double LetterScale,    // T box width as fraction of size
        double SkewDeg,        // italic lean, negative = top leans right
        double BarHeight,      // fraction of T box height
        double StemWidth,      // fraction of T box width
        Point WhiteOrigin,     // fraction of size
        Point BlueOrigin,
        bool Outline);         // thin dark outline around the letters (separates T's at tiny sizes)

    private static MarkStyle StyleFor(int size) => size switch
    {
        <= 16 => new MarkStyle(0.22, 0, 0.62, 0, 0.33, 0.33, new Point(0.06, 0.08), new Point(0.32, 0.32), true),
        <= 20 => new MarkStyle(0.22, 0, 0.62, 0, 0.33, 0.33, new Point(0.06, 0.09), new Point(0.32, 0.33), true),
        <= 24 => new MarkStyle(0.22, 0, 0.60, -6, 0.32, 0.32, new Point(0.07, 0.10), new Point(0.33, 0.34), true),
        <= 32 => new MarkStyle(0.22, 0, 0.56, -10, 0.30, 0.31, new Point(0.09, 0.13), new Point(0.35, 0.37), true),
        <= 48 => new MarkStyle(0.22, 1.5, 0.50, -13, 0.28, 0.30, new Point(0.15, 0.19), new Point(0.37, 0.39), false),
        _     => new MarkStyle(0.22, size * 0.024, 0.47, -14, 0.27, 0.29, new Point(0.17, 0.20), new Point(0.39, 0.40), false),
    };

    private static BitmapSource RenderMark(int size)
    {
        var style = StyleFor(size);
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            DrawMark(dc, size, style);
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return rtb;
    }

    private static void DrawMark(DrawingContext dc, double s, MarkStyle st)
    {
        // Tile
        double r = s * st.CornerRadius;
        var tile = new RectangleGeometry(new Rect(0, 0, s, s), r, r);
        var tileFill = new LinearGradientBrush(
            Color.FromRgb(0x16, 0x1F, 0x33), Color.FromRgb(0x0A, 0x0F, 0x1B), new Point(0, 0), new Point(0, 1));
        dc.DrawGeometry(tileFill, null, tile);

        if (st.Border > 0)
        {
            double inset = st.Border / 2;
            var borderGeom = new RectangleGeometry(new Rect(inset, inset, s - st.Border, s - st.Border), r - inset, r - inset);
            var borderPen = new Pen(new SolidColorBrush(Color.FromArgb(0xCC, 0x2F, 0x8F, 0xFF)), st.Border);
            dc.DrawGeometry(null, borderPen, borderGeom);
        }

        // Letters: build both T's in tile space, then skew the pair about the tile centre.
        bool snap = s <= 24; // whole-pixel geometry keeps 16-24 px crisp
        double box = Snap(s * st.LetterScale, snap);
        double bar = Snap(box * st.BarHeight, snap);
        double stem = Snap(box * st.StemWidth, snap);
        Geometry white = TGeometry(new Point(Snap(st.WhiteOrigin.X * s, snap), Snap(st.WhiteOrigin.Y * s, snap)), box, bar, stem, snap);
        Geometry blue = TGeometry(new Point(Snap(st.BlueOrigin.X * s, snap), Snap(st.BlueOrigin.Y * s, snap)), box, bar, stem, snap);

        var skew = new TransformGroup();
        skew.Children.Add(new TranslateTransform(-s / 2, -s / 2));
        skew.Children.Add(new SkewTransform(st.SkewDeg, 0));
        skew.Children.Add(new TranslateTransform(s / 2, s / 2));
        white.Transform = skew;
        blue.Transform = skew;

        var whiteFill = new LinearGradientBrush(
            Colors.White, Color.FromRgb(0xD6, 0xE4, 0xF7), new Point(0, 0), new Point(0, 1));
        var blueFill = new LinearGradientBrush(
            Color.FromRgb(0x3A, 0xA0, 0xFF), Color.FromRgb(0x0F, 0x6F, 0xE6), new Point(0, 0), new Point(0, 1));

        Pen? outline = st.Outline
            ? new Pen(new SolidColorBrush(Color.FromArgb(0xE0, 0x0A, 0x0F, 0x1B)), s <= 24 ? 1.0 : 1.5) { LineJoin = PenLineJoin.Miter }
            : null;

        // Blue T sits on top; a dark outline on it carves it out of the white T at small sizes.
        dc.DrawGeometry(whiteFill, null, white);
        if (outline != null)
        {
            dc.DrawGeometry(null, outline, blue);
        }
        dc.DrawGeometry(blueFill, null, blue);
    }

    /// <summary>Upright T as a single polygon; origin = top-left of its bounding box.</summary>
    private static double Snap(double v, bool snap) => snap ? Math.Round(v) : v;

    private static Geometry TGeometry(Point origin, double box, double bar, double stem, bool snap)
    {
        double sx = Snap(origin.X + (box - stem) / 2, snap);
        double cut = snap ? 0 : box * 0.10; // slanted stem foot (dropped when pixel-snapped)

        var fig = new PathFigure { StartPoint = origin, IsClosed = true, IsFilled = true };
        fig.Segments.Add(new LineSegment(new Point(origin.X + box, origin.Y), true));
        fig.Segments.Add(new LineSegment(new Point(origin.X + box, origin.Y + bar), true));
        fig.Segments.Add(new LineSegment(new Point(sx + stem, origin.Y + bar), true));
        fig.Segments.Add(new LineSegment(new Point(sx + stem, origin.Y + box - cut), true));
        fig.Segments.Add(new LineSegment(new Point(sx, origin.Y + box), true));
        fig.Segments.Add(new LineSegment(new Point(sx, origin.Y + bar), true));
        fig.Segments.Add(new LineSegment(new Point(origin.X, origin.Y + bar), true));

        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        return geo;
    }

    // ---------------------------------------------------------------- previews

    private static BitmapSource Upscale(BitmapSource src, int factor)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x28)), null,
                new Rect(0, 0, src.PixelWidth * factor, src.PixelHeight * factor));
            dc.DrawImage(src, new Rect(0, 0, src.PixelWidth * factor, src.PixelHeight * factor));
        }
        var rtb = new RenderTargetBitmap(src.PixelWidth * factor, src.PixelHeight * factor, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return rtb;
    }

    /// <summary>1:1 render of the small sizes on a taskbar-coloured strip, as the user will see them.</summary>
    private static BitmapSource RenderTrayStrip(Dictionary<int, BitmapSource> frames)
    {
        int w = 320, h = 72;
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F)), null, new Rect(0, 0, w, h));
            double x = 16;
            foreach (int size in new[] { 16, 20, 24, 32, 48 })
            {
                dc.DrawImage(frames[size], new Rect(x, (h - size) / 2.0, size, size));
                x += size + 24;
            }
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return rtb;
    }

    /// <summary>Renders Segoe MDL2 code points with labels so a glyph can be chosen by eye.</summary>
    private static BitmapSource RenderGlyphSheet(string[] hexCodes)
    {
        int cell = 96, cols = 6;
        int rows = (hexCodes.Length + cols - 1) / cols;
        int w = cols * cell, h = Math.Max(1, rows) * cell;
        var visual = new DrawingVisual();
        var tf = new Typeface(new FontFamily("Segoe MDL2 Assets, Segoe Fluent Icons"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var labelTf = new Typeface("Segoe UI");
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x1C)), null, new Rect(0, 0, w, h));
            for (int i = 0; i < hexCodes.Length; i++)
            {
                int cx = (i % cols) * cell, cy = (i / cols) * cell;
                string glyph = char.ConvertFromUtf32(Convert.ToInt32(hexCodes[i], 16));
                var ft = new FormattedText(glyph, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 36, Brushes.White, 1.0);
                dc.DrawText(ft, new Point(cx + (cell - ft.Width) / 2, cy + 14));
                var lt = new FormattedText(hexCodes[i].ToUpperInvariant(), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, labelTf, 12, Brushes.LightGray, 1.0);
                dc.DrawText(lt, new Point(cx + (cell - lt.Width) / 2, cy + cell - 24));
            }
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return rtb;
    }

    // ---------------------------------------------------------------- file output

    private static void SavePng(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>
    /// ICO container: sizes up to 64 px are stored as 32-bpp BGRA DIBs (what every Windows
    /// shell path, including NotifyIcon, decodes fastest); 128 and 256 are PNG-compressed.
    /// </summary>
    private static void WriteIco(string path, Dictionary<int, BitmapSource> frames)
    {
        var entries = frames.OrderBy(kv => kv.Key).Select(kv => (size: kv.Key, data: kv.Key >= 128 ? EncodePng(kv.Value) : EncodeDib(kv.Value))).ToList();

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write((ushort)0);            // reserved
        bw.Write((ushort)1);            // type: icon
        bw.Write((ushort)entries.Count);

        int offset = 6 + 16 * entries.Count;
        foreach (var (size, data) in entries)
        {
            bw.Write((byte)(size >= 256 ? 0 : size));
            bw.Write((byte)(size >= 256 ? 0 : size));
            bw.Write((byte)0);          // palette
            bw.Write((byte)0);          // reserved
            bw.Write((ushort)1);        // planes
            bw.Write((ushort)32);       // bpp
            bw.Write(data.Length);
            bw.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in entries)
        {
            bw.Write(data);
        }
    }

    private static byte[] EncodePng(BitmapSource bmp)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static byte[] EncodeDib(BitmapSource src)
    {
        var bmp = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bmp.PixelWidth, h = bmp.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        bmp.CopyPixels(px, stride, 0);

        int maskStride = ((w + 31) / 32) * 4;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(40);                   // BITMAPINFOHEADER size
        bw.Write(w);
        bw.Write(h * 2);                // XOR + AND masks
        bw.Write((ushort)1);
        bw.Write((ushort)32);
        bw.Write(0);                    // BI_RGB
        bw.Write(stride * h + maskStride * h);
        bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);

        for (int y = h - 1; y >= 0; y--)
        {
            bw.Write(px, y * stride, stride);
        }
        var maskRow = new byte[maskStride];
        for (int y = h - 1; y >= 0; y--)
        {
            Array.Clear(maskRow);
            for (int x = 0; x < w; x++)
            {
                if (px[y * stride + x * 4 + 3] == 0)
                {
                    maskRow[x / 8] |= (byte)(0x80 >> (x % 8));
                }
            }
            bw.Write(maskRow);
        }
        return ms.ToArray();
    }
}
