using System;
using System.Collections.Generic;
using System.Windows;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// Makes a window open at the size the player last left it at. A dialog asks for it with one line,
/// <c>WindowSizeMemory.Attach(this, "ScanForGames")</c>, in its constructor: the saved size is applied at once and the size
/// the window has when it closes is kept in <see cref="AppSettings.WindowSizes"/> under that name. Only a size that differs from the
/// window's own default is kept, so a player who never resized a window keeps following the default if it is ever changed.
/// The window comes back where it was centered each time: this remembers size, not position.
/// </summary>
public static class WindowSizeMemory
{
    private static Func<AppSettings>? _settings;
    private static Action<AppSettings>? _save;

    /// <summary>Called once at start-up: where the live settings are, and how they are saved.</summary>
    public static void Configure(Func<AppSettings> settings, Action<AppSettings> save)
    {
        _settings = settings;
        _save = save;
    }

    /// <param name="window">The window, before it is shown.</param>
    /// <param name="key">A name for the window in the settings; never changed once shipped.</param>
    /// <param name="extraWidth">Width the window has beyond its own size right now (a panel that slides out of it): left out of what is kept and added back by the window itself.</param>
    public static void Attach(Window window, string key, Func<double>? extraWidth = null)
    {
        var settings = _settings?.Invoke();
        if (settings == null) return;

        double defaultWidth = window.Width, defaultHeight = window.Height;
        if (double.IsNaN(defaultWidth) || double.IsNaN(defaultHeight)) return;

        if (settings.WindowSizes.TryGetValue(key, out var saved)) Apply(window, saved);

        window.Closed += (_, _) =>
        {
            if (window.WindowState != WindowState.Normal) return;
            double extra = extraWidth?.Invoke() ?? 0;
            if (window.ActualWidth - extra <= 0 || window.ActualHeight <= 0) return;
            Remember(key, SizeToKeep(window.ActualWidth, window.ActualHeight, extra, defaultWidth, defaultHeight));
        };
    }

    /// <summary>The size to keep for a window that closed at this size, or null when it is at its default (nothing is kept, and a size kept before is forgotten).</summary>
    internal static WindowSize? SizeToKeep(double actualWidth, double actualHeight, double extraWidth, double defaultWidth, double defaultHeight)
    {
        double width = Math.Round(actualWidth - extraWidth), height = Math.Round(actualHeight);
        return width == Math.Round(defaultWidth) && height == Math.Round(defaultHeight) ? null : new WindowSize { Width = width, Height = height };
    }

    /// <summary>
    /// Sets the window to a saved size, unless it is below the window's minimum or larger than the work area:
    /// a size saved on a bigger display can't come back too big to reach the buttons.
    /// </summary>
    internal static bool Apply(Window window, WindowSize? size)
    {
        if (size == null || double.IsNaN(size.Width) || double.IsNaN(size.Height)) return false;
        var work = SystemParameters.WorkArea;
        if (size.Width < window.MinWidth || size.Height < window.MinHeight || size.Width > work.Width || size.Height > work.Height) return false;
        window.Width = size.Width;
        window.Height = size.Height;
        return true;
    }

    internal static void Remember(string key, WindowSize? size)
    {
        var settings = _settings?.Invoke();
        if (settings == null) return;

        bool had = settings.WindowSizes.TryGetValue(key, out var old);
        if (size == null)
        {
            if (!had) return;
            settings.WindowSizes.Remove(key);
        }
        else
        {
            if (had && old!.Width == size.Width && old.Height == size.Height) return;
            settings.WindowSizes[key] = size;
        }
        _save?.Invoke(settings);
    }
}
