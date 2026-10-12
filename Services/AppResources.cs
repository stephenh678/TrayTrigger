using System;
using System.Collections.Concurrent;
using System.Windows;

namespace TrayTrigger.Services;

/// <summary>
/// Finds a named resource from one of the app's design files in <c>Resources/</c> (Colors.xaml,
/// Typography.xaml, Shapes.xaml). The one place that knows how to read them in code, shared by
/// <see cref="AppPalette"/>, <see cref="AppTypography"/> and <see cref="AppShapes"/>.
///
/// <para>The running app answers from its own resources. With no <see cref="Application"/> (the unit
/// tests) the file is loaded on its own, once, so the answer is the same either way.</para>
/// </summary>
internal static class AppResources
{
    private static readonly ConcurrentDictionary<string, ResourceDictionary> Standalone = new();

    /// <summary>The resource with this key, looked up in the running app first and in <paramref name="file"/> if that has none.</summary>
    internal static object? Find(string key, string file)
    {
        var found = Application.Current?.TryFindResource(key) ?? Standalone.GetOrAdd(file, Load)[key];
        // A brush read from a resource file belongs to the thread that first asked for it until it is
        // frozen, and a second thread reading its Color then throws. Nothing here changes a palette brush.
        if (found is Freezable { IsFrozen: false, CanFreeze: true } freezable) freezable.Freeze();
        return found;
    }

    private static ResourceDictionary Load(string file)
    {
        // The pack:// scheme is registered by touching PackUriHelper; the app does this on its own.
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        return new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/TrayTrigger;component/Resources/{file}", UriKind.Absolute)
        };
    }
}
