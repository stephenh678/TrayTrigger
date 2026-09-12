using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace TrayTrigger.Services;

public static partial class WindowThemeService
{
    // ---------------------------------------------------------------------------------
    // First-show flicker suppression.
    //
    // WPF calls ShowWindow on the HWND before the first frame has been rendered, so the
    // user briefly sees an unpainted (white) client area and, if the dark title bar is
    // applied later, a light frame that then flips dark. On top of that, several dialogs
    // re-position themselves in Loaded, which is a visible SetWindowPos on an already
    // shown window.
    //
    // The fix is DWM cloaking: at SourceInitialized (HWND exists, not yet shown) we apply
    // the dark attributes and cloak the window. WPF still shows it, lays it out, renders
    // the first frame and fires Loaded/ContentRendered as normal, but DWM does not
    // composite it to the screen. At ContentRendered we uncloak, so the window pops in
    // fully painted, themed and positioned. Any Loaded-time centering happens while
    // cloaked and is therefore invisible.
    // ---------------------------------------------------------------------------------

    private const int DWMWA_CLOAK = 13;

    private sealed class FirstShowState
    {
        public bool Cloaked;
        public bool Rendered;
        public List<Action>? Pending;
        public DispatcherTimer? Fallback;
    }

    private static readonly ConditionalWeakTable<Window, FirstShowState> _firstShow = new();

    /// <summary>
    /// Call from a window's constructor (before it is shown). Applies dark title bar
    /// attributes before the HWND is visible and keeps the window cloaked until its
    /// first frame has rendered. Idempotent.
    /// </summary>
    public static void PrepareForFirstShow(Window window)
    {
        if (window == null) return;
        if (_firstShow.TryGetValue(window, out _)) return;

        var state = new FirstShowState();
        _firstShow.Add(window, state);

        // Already has an HWND (shown before we were called) - nothing to cloak, just theme it.
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            state.Rendered = true;
            SetDarkAttributes(window);
            return;
        }

        window.SourceInitialized += (s, e) =>
        {
            SetDarkAttributes(window);
            if (SetCloak(window, true))
            {
                state.Cloaked = true;
            }
        };

        window.ContentRendered += (s, e) => FinishFirstShow(window, state);

        // Safety net: if ContentRendered never fires (e.g. zero-size content), don't leave
        // the window invisible. Loaded runs after the first layout pass, so a short timer
        // from there is more than enough for the first frame to land.
        window.Loaded += (s, e) =>
        {
            if (state.Rendered) return;
            state.Fallback = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(750)
            };
            state.Fallback.Tick += (ts, te) => FinishFirstShow(window, state);
            state.Fallback.Start();
        };

        window.Closed += (s, e) =>
        {
            state.Fallback?.Stop();
            state.Fallback = null;
            state.Pending = null;
        };
    }

    /// <summary>
    /// Runs <paramref name="action"/> once the window's first frame is on screen. If it
    /// already is, runs immediately. Use for Activate/Focus/Topmost tricks that would
    /// otherwise compete with the first paint.
    /// </summary>
    public static void WhenContentRendered(Window window, Action action)
    {
        if (window == null || action == null) return;

        if (!_firstShow.TryGetValue(window, out var state) || state.Rendered)
        {
            action();
            return;
        }

        (state.Pending ??= new List<Action>()).Add(action);
    }

    private static void FinishFirstShow(Window window, FirstShowState state)
    {
        if (state.Rendered) return;
        state.Rendered = true;

        state.Fallback?.Stop();
        state.Fallback = null;

        if (state.Cloaked)
        {
            state.Cloaked = false;

            // ContentRendered only means the UI thread has handed the first frame to WPF's
            // render thread. On a cold render thread (first-use shader compiles for effects,
            // glyph cache fills, new D3D surface for this HWND) the actual Present can lag
            // well behind that, and if we uncloak before it lands DWM composites the frame
            // with an unpresented client surface - i.e. a white box. Block until the render
            // thread has flushed, then let DWM composite that frame, then uncloak.
            WaitForRenderThread(window);
            try { DwmFlush(); } catch { /* best effort */ }

            SetCloak(window, false);
        }

        var pending = state.Pending;
        state.Pending = null;
        if (pending == null) return;

        foreach (var action in pending)
        {
            try { action(); }
            catch (Exception ex)
            {
                LoggingService.Warn("WindowThemeService", $"Deferred first-show action failed: {ex.Message}");
            }
        }
    }

    // WPF has no public "wait until the render thread has presented" API, but its internal
    // MediaContext.CompleteRender() does exactly that (it sync-flushes the composition
    // channel). Resolved once via reflection; if a future WPF build removes it we simply
    // fall back to uncloaking at ContentRendered like before.
    private static MethodInfo? _mediaContextFrom;
    private static MethodInfo? _completeRender;
    private static bool _renderFlushUnavailable;

    private static void WaitForRenderThread(Window window)
    {
        if (_renderFlushUnavailable) return;

        try
        {
            if (_completeRender == null)
            {
                var mediaContextType = typeof(Visual).Assembly.GetType("System.Windows.Media.MediaContext");
                _mediaContextFrom = mediaContextType?.GetMethod(
                    "From",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                    new[] { typeof(Dispatcher) });
                _completeRender = mediaContextType?.GetMethod(
                    "CompleteRender",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                    Type.EmptyTypes);

                if (_mediaContextFrom == null || _completeRender == null)
                {
                    _renderFlushUnavailable = true;
                    LoggingService.Warn("WindowThemeService", "MediaContext.CompleteRender not found; first-show uncloak will not wait for the render thread.");
                    return;
                }
            }

            var mediaContext = _mediaContextFrom!.Invoke(null, new object[] { window.Dispatcher });
            if (mediaContext != null)
            {
                _completeRender!.Invoke(mediaContext, null);
            }
        }
        catch (Exception ex)
        {
            _renderFlushUnavailable = true;
            LoggingService.Warn("WindowThemeService", $"Render-thread flush failed; disabling: {ex.GetBaseException().Message}");
        }
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmFlush();

    private static bool SetCloak(Window window, bool cloak)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return false;

            int value = cloak ? 1 : 0;
            int hr = DwmSetWindowAttribute(handle, DWMWA_CLOAK, in value, sizeof(int));
            if (hr != 0)
            {
                LoggingService.Verbose("WindowThemeService", $"DWMWA_CLOAK({cloak}) returned 0x{hr:X8}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("WindowThemeService", $"Failed to {(cloak ? "cloak" : "uncloak")} window: {ex.Message}");
            return false;
        }
    }

    // Owner.Left/Owner.Top report stale/incorrect values when the owner window is
    // maximized, which throws off manual centering (dialogs land near the bottom-right
    // instead of centered). PointToScreen reflects the owner's actual on-screen
    // position regardless of window state, so use that instead.
    public static void CenterOverOwner(Window window)
    {
        if (window == null) return;

        if (window.Owner == null)
        {
            // No owner to center over - fall back to centering on the work area.
            window.Left = (SystemParameters.WorkArea.Width - window.ActualWidth) / 2;
            window.Top = (SystemParameters.WorkArea.Height - window.ActualHeight) / 2;
            return;
        }

        Point ownerTopLeftScreen = window.Owner.PointToScreen(new Point(0, 0));
        Matrix transformFromDevice = PresentationSource.FromVisual(window.Owner)?.CompositionTarget?.TransformFromDevice
            ?? Matrix.Identity;
        Point ownerTopLeftDip = transformFromDevice.Transform(ownerTopLeftScreen);

        window.Left = ownerTopLeftDip.X + (window.Owner.ActualWidth - window.ActualWidth) / 2;
        window.Top = ownerTopLeftDip.Y + (window.Owner.ActualHeight - window.ActualHeight) / 2;
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, in int attrValue, int attrSize);

    // DWM wants a COLORREF (0x00BBGGRR), which is byte-reversed from the #RRGGBB literals the
    // palette in App.xaml is written in. Converting here means the caption tracks the palette
    // instead of a hand-swapped copy that silently drifts when the palette is retuned.
    private static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    private static void SetDarkAttributes(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            int useDarkMode = 1;
            // Windows 11 & Windows 10 20H1+
            DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, in useDarkMode, sizeof(int));
            // Windows 10 1809 - 1909 fallback
            DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, in useDarkMode, sizeof(int));

            // Windows 11 Build 22000+ support custom caption/text colors. Take them from the
            // window's own Background - every window sets one from the palette - so the caption
            // always matches the client area directly below it. That includes QuickInputDialog,
            // which uses BrushBgDark rather than the dialog color and so had the wrong caption
            // under the old main-window-vs-dialog split.
            if (window.Background is SolidColorBrush background)
            {
                int captionColor = ToColorRef(background.Color);
                DwmSetWindowAttribute(handle, DWMWA_CAPTION_COLOR, in captionColor, sizeof(int));

                int textColor = ToColorRef(
                    Application.Current?.TryFindResource("ColorTextPrimary") as Color? ?? Colors.White);
                DwmSetWindowAttribute(handle, DWMWA_TEXT_COLOR, in textColor, sizeof(int));
            }
            else
            {
                // Nothing solid to match. DWMWA_USE_IMMERSIVE_DARK_MODE above already gives the
                // window the standard dark caption, which beats guessing a color.
                LoggingService.Verbose("WindowThemeService", $"{window.GetType().Name} has no solid background; leaving the caption color to DWM.");
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("WindowThemeService", $"Failed to set dark title bar: {ex.Message}");
        }
    }
}
