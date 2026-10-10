using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TrayTrigger.ViewModels;

namespace TrayTrigger;

/// <summary>
/// The one screenshot that works in every build: <c>TrayTrigger.exe --screenshot &lt;file.png&gt;</c>
/// renders the library window at the size the website and README use, writes it, and exits.
/// It exists so the site's library screenshot can be refreshed from any PC running a release,
/// not only from a dev machine with a Debug build. The many other capture modes stay Debug-only
/// (App.DevDiagnostics.cs) and reuse the helpers here.
/// </summary>
public partial class App
{
    /// <summary>Device-independent size every library screenshot is taken at; at 125 % DPI that is a 1200 px wide PNG.</summary>
    private const int ScreenshotWidth = 960;
    private const int ScreenshotHeight = 700;

    /// <summary>True when the arguments ask for the release screenshot (exactly "--screenshot &lt;file&gt;").</summary>
    private static bool IsReleaseScreenshotRequest(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i].Equals("--screenshot", StringComparison.OrdinalIgnoreCase) || args[i].Equals("-screenshot", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Handles "--screenshot &lt;file&gt;" once the main window exists: captures the library and
    /// shuts down. Returns true when it did, so startup stops there. In Debug builds the dev
    /// argument handler runs first and has already exited, so this only ever fires in Release.
    /// </summary>
    private bool TryHandleReleaseScreenshot(string[] args)
    {
        if (_mainWindow == null) return false;
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (!args[i].Equals("--screenshot", StringComparison.OrdinalIgnoreCase) && !args[i].Equals("-screenshot", StringComparison.OrdinalIgnoreCase))
                continue;

            string targetPng = args[i + 1];
            // Before the window shows: the capture resizes it, and saving would make that the
            // user's window placement (and could overwrite a running instance's settings).
            _skipSettingsSaveOnExit = true;
            _mainViewModel.CurrentSection = NavSection.Library;
            _mainWindow.Show();
            // Icons and covers are decoded off the UI thread after the library loads; a capture
            // taken before they land shows the fallback glyph on every card.
            var heavyState = _mainViewModel.Library.HeavyStateLoad;
            PumpDispatcher(TimeSpan.FromSeconds(10), () => heavyState.IsCompleted);
            _mainWindow.UpdateLayout();
            if (!CaptureVisual(_mainWindow, ScreenshotWidth, ScreenshotHeight, targetPng))
            {
                _exitCode = 1;
            }
            ExitApplication();
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ capture

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>Writes the window to <paramref name="targetPng"/>; false when it could not be.</summary>
    private bool CaptureVisual(Window window, int width, int height, string targetPng)
    {
        try
        {
            // TRAYTRIGGER_SHOT_SIZE=WxH (device-independent units) overrides a mode's default
            // size - the README screenshots are taken at 1040x747, the review ones at 960x700.
            string? sizeOverride = Environment.GetEnvironmentVariable("TRAYTRIGGER_SHOT_SIZE");
            if (!string.IsNullOrWhiteSpace(sizeOverride))
            {
                var parts = sizeOverride.Split('x');
                if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h) && w > 0 && h > 0)
                {
                    width = w;
                    height = h;
                }
            }
            using var bmp = CaptureVisualBitmap(window, width, height);
            string? parentDir = Path.GetDirectoryName(targetPng);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            bmp.Save(targetPng, System.Drawing.Imaging.ImageFormat.Png);
            _logger($"[CaptureVisual] Success: {targetPng}");
            return true;
        }
        catch (Exception ex)
        {
            _logger($"[CaptureVisual] Error: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Runs the dispatcher for up to <paramref name="timeout"/>, or until <paramref name="until"/>
    /// holds, so queued work (the first frame, DWM painting the title bar, a background load's
    /// dispatch back to the UI thread) lands before the capture that follows.
    /// </summary>
    private static void PumpDispatcher(TimeSpan timeout, Func<bool>? until = null)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var deadline = DateTime.UtcNow + timeout;
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = until == null ? timeout : TimeSpan.FromMilliseconds(50)
        };
        timer.Tick += (s, args) =>
        {
            if (until == null || until() || DateTime.UtcNow >= deadline)
            {
                timer.Stop();
                frame.Continue = false;
            }
        };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// The window as a bitmap: the client area rendered by WPF (crisp at any DPI) under the real
    /// DWM title bar grabbed with PrintWindow, so the PNG looks like the window does on screen.
    /// Falls back to the client area alone if the native grab fails.
    /// </summary>
    private System.Drawing.Bitmap CaptureVisualBitmap(Window window, int width, int height)
    {
        // A placement restored as Maximized ignores Width/Height and would be captured at the
        // monitor's size.
        window.WindowState = WindowState.Normal;
        window.Width = width;
        window.Height = height;
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Show();
        window.Activate();
        window.Focus();
        window.UpdateLayout();

        // Pump dispatcher events to allow DWM to paint titlebar
        PumpDispatcher(TimeSpan.FromMilliseconds(400));

        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        int clientW = width;
        int clientH = height;
        if (hwnd != IntPtr.Zero && GetClientRect(hwnd, out RECT cRect) && cRect.Right > 50 && cRect.Bottom > 50)
        {
            clientW = cRect.Right - cRect.Left;
            clientH = cRect.Bottom - cRect.Top;
        }

        double effDpiX = window.ActualWidth > 0 ? (96.0 * clientW / window.ActualWidth) : 96.0;
        double effDpiY = window.ActualHeight > 0 ? (96.0 * clientH / window.ActualHeight) : 96.0;

        // 1. Render client area with RenderTargetBitmap
        var rtb = new RenderTargetBitmap(clientW, clientH, effDpiX, effDpiY, PixelFormats.Pbgra32);
        rtb.Render(window);

        // Convert rtb to GDI Bitmap
        var clientBmp = new System.Drawing.Bitmap(clientW, clientH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var rectClient = new System.Drawing.Rectangle(0, 0, clientW, clientH);
        var bmpData = clientBmp.LockBits(rectClient, System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        rtb.CopyPixels(System.Windows.Int32Rect.Empty, bmpData.Scan0, bmpData.Height * bmpData.Stride, bmpData.Stride);
        clientBmp.UnlockBits(bmpData);

        try
        {
            if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out RECT rect) && (rect.Right - rect.Left > 50) && (rect.Bottom - rect.Top > 50))
            {
                int w = rect.Right - rect.Left;
                int h = rect.Bottom - rect.Top;
                using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.Clear(System.Drawing.Color.FromArgb(18, 18, 20));
                    IntPtr hdc = g.GetHdc();
                    bool printed = false;
                    try
                    {
                        printed = PrintWindow(hwnd, hdc, 2);
                    }
                    finally
                    {
                        try { g.ReleaseHdc(hdc); } catch { /* nothing left to release */ }
                    }

                    if (printed)
                    {
                        var pt = new POINT { X = 0, Y = 0 };
                        ClientToScreen(hwnd, ref pt);
                        int clientX = Math.Max(0, pt.X - rect.Left);
                        int clientY = Math.Max(0, pt.Y - rect.Top);

                        int finalW = clientX + clientW + clientX;
                        int finalH = clientY + clientH + clientX;

                        var finalBmp = new System.Drawing.Bitmap(finalW, finalH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        using (var gFinal = System.Drawing.Graphics.FromImage(finalBmp))
                        {
                            gFinal.Clear(System.Drawing.Color.FromArgb(18, 18, 20));
                            int captionH = Math.Min(clientY, h);
                            gFinal.DrawImage(bmp,
                                new System.Drawing.Rectangle(0, 0, finalW, captionH),
                                new System.Drawing.Rectangle(0, 0, Math.Min(finalW, w), captionH),
                                System.Drawing.GraphicsUnit.Pixel);
                            gFinal.DrawImage(clientBmp, new System.Drawing.Rectangle(clientX, clientY, clientW, clientH));
                        }

                        clientBmp.Dispose();
                        return finalBmp;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger($"[CaptureVisualBitmap - Native] Falling back to client screenshot: {ex.Message}");
        }

        return clientBmp;
    }
}
