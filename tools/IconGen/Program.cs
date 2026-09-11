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
        // social <out.png> <library screenshot.png>: the 1280x640 GitHub social preview card.
        if (args.Length >= 3 && args[0] == "social")
        {
            SavePng(RenderSocialPreview(args[2]), args[1]);
            Console.WriteLine($"Wrote {args[1]}");
            return 0;
        }

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

    // ---------------------------------------------------------------- social preview

    private static readonly Color Accent = Color.FromRgb(0x2F, 0x8F, 0xFF);

    /// <summary>
    /// 1280x640 card for GitHub's repository social preview: the TT mark and wordmark, the
    /// "Launch. Automate. Play." tagline, feature pills, and the library screenshot fading in on
    /// the right. Re-run after an icon or library redesign so the card never drifts from the app.
    /// </summary>
    private static BitmapSource RenderSocialPreview(string screenshotPath)
    {
        const int W = 1280, H = 640;
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            // Background: deep navy with a blue glow behind the mark.
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x0B, 0x10, 0x20), Color.FromRgb(0x05, 0x08, 0x0F), new Point(0, 0), new Point(1, 1)), null, new Rect(0, 0, W, H));
            dc.DrawRectangle(new RadialGradientBrush(Color.FromArgb(0x55, 0x1E, 0x5F, 0xC0), Color.FromArgb(0x00, 0x1E, 0x5F, 0xC0))
            {
                Center = new Point(0.18, 0.28), GradientOrigin = new Point(0.18, 0.28), RadiusX = 0.42, RadiusY = 0.8
            }, null, new Rect(0, 0, W, H));

            // Library screenshot, right side, fading in from the text column.
            if (File.Exists(screenshotPath))
            {
                var shot = new BitmapImage(new Uri(Path.GetFullPath(screenshotPath)));
                double scale = 880.0 / shot.PixelWidth;
                var target = new Rect(560, 52, shot.PixelWidth * scale, shot.PixelHeight * scale);
                dc.PushClip(new RectangleGeometry(new Rect(560, 52, W - 560 + 40, H - 52), 14, 14));
                dc.PushOpacityMask(new LinearGradientBrush(
                    new GradientStopCollection
                    {
                        new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0.0),
                        new GradientStop(Color.FromArgb(0xFF, 0, 0, 0), 0.28),
                        new GradientStop(Color.FromArgb(0xFF, 0, 0, 0), 1.0),
                    }, new Point(0, 0), new Point(1, 0)) { MappingMode = BrushMappingMode.RelativeToBoundingBox });
                dc.PushOpacity(0.92);
                dc.DrawImage(shot, target);
                dc.Pop(); dc.Pop(); dc.Pop();
                // Soft bottom fade so the cut-off edge does not read as a hard crop.
                dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(0x00, 0x05, 0x08, 0x0F), Color.FromArgb(0xFF, 0x05, 0x08, 0x0F), new Point(0, 0), new Point(0, 1)), null, new Rect(560, H - 140, W - 560, 140));
            }

            // Mark.
            var mark = RenderMark(256);
            dc.DrawImage(mark, new Rect(88, 62, 150, 150));

            // Wordmark: "Tray" white, "Trigger" accent blue.
            var bold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var semi = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var regular = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var tray = Text("Tray", bold, 84, Colors.White);
            var trigger = Text("Trigger", bold, 84, Accent);
            dc.DrawText(tray, new Point(84, 228));
            dc.DrawText(trigger, new Point(84 + tray.WidthIncludingTrailingWhitespace, 228));

            // Tagline, letter-spaced.
            double x = 90, y = 344;
            foreach (char ch in "LAUNCH.  AUTOMATE.  PLAY.")
            {
                var glyph = Text(ch.ToString(), semi, 21, Color.FromRgb(0x8F, 0xB8, 0xE8));
                dc.DrawText(glyph, new Point(x, y));
                x += glyph.WidthIncludingTrailingWhitespace + 3.5;
            }

            // One-line description.
            var desc = Text("Windows game launcher and gaming optimizer that lives in your system tray.", regular, 20, Color.FromRgb(0xB8, 0xC4, 0xD6));
            desc.MaxTextWidth = 540;
            dc.DrawText(desc, new Point(88, 388));

            // Feature pills.
            string[] pills = { "Steam · GOG · EA · Epic · Ubisoft", "Performance Profiles", "Reversible Windows Tweaks", "Hardware Telemetry" };
            double px = 88, py = 466;
            foreach (string label in pills)
            {
                var t = Text(label, semi, 15, Colors.White);
                double w = t.WidthIncludingTrailingWhitespace + 32, h = 38;
                if (px + w > 640) { px = 88; py += h + 12; }
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x12, 0x1F, 0x38)), new Pen(new SolidColorBrush(Color.FromArgb(0x99, 0x2F, 0x8F, 0xFF)), 1.2), new Rect(px, py, w, h), 19, 19);
                dc.DrawText(t, new Point(px + 16, py + (h - t.Height) / 2));
                px += w + 12;
            }

            var foot = Text("Free  ·  Open source (MIT)  ·  No account required", regular, 14, Color.FromRgb(0x6F, 0x7C, 0x92));
            dc.DrawText(foot, new Point(90, 592));
        }

        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return rtb;
    }

    private static FormattedText Text(string text, Typeface face, double size, Color color) =>
        new(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, new SolidColorBrush(color), 1.0)
        {
            TextAlignment = TextAlignment.Left
        };

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
