using System;
using System.Collections.Concurrent;
using System.Windows;

namespace TrayTrigger.Services;

/// <summary>
/// Finds a named resource from one of the app's design files in <c>Resources/</c> (Colors.xaml,
/// Typography.xaml, Shapes.xaml). The one place that knows how to read them in code, shared by
/// <see cref="AppPalette"/>, <see cref="AppTypography"/> and <see cref="AppShapes"/>.
///
/// <para>The running app answers from its own resources. In the unit tests, which have no App.xaml, the file is
/// loaded on its own, once, so the answer is the same either way.</para>
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

    /// <summary>A new copy of the file, not the shared one: the theme code reads it to learn the built-in colors and must not see a swapped theme.</summary>
    internal static ResourceDictionary LoadFresh(string file) => Load(file);

    private static ResourceDictionary Load(string file)
    {
        // A pack:// URI only parses once WPF has registered the pack scheme and the application's resources,
        // which Application's static constructor does. The running app has long since run it; a unit test that
        // reaches here before anything touched Application (ThemeCatalog.DarkPalette() on its own, say) has
        // not, and the URI fails as "Invalid port specified". Running it here makes the load not depend on
        // which test ran first.
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Application).TypeHandle);
        return new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/TrayTrigger;component/Resources/{file}", UriKind.Absolute)
        };
    }
}
