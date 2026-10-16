using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace TrayTrigger.Services;

/// <summary>
/// The font the app's text is set in. Any font installed on the machine can be chosen, except symbol fonts (Wingdings and the
/// like, which would turn every label into pictures). The chosen font is always followed by Segoe UI, so a font that is missing
/// or lacks a character falls back to the default instead of to an unrelated one. The font is the <c>AppFont</c> resource, which
/// the views use as a live resource, so a change shows at once. The icons and the monospace font are not affected.
/// </summary>
public static class AppFonts
{
    /// <summary>The font used when none is chosen.</summary>
    public const string DefaultName = "Segoe UI";

    /// <summary>A font given when the app was started ("--font", for a capture): it wins over the setting and is not saved.</summary>
    public static string? OverrideName { get; set; }

    /// <summary>The font from Settings, "" for the default.</summary>
    public static string SystemName { get; set; } = string.Empty;

    /// <summary>The fonts that can be chosen: the names to show, and every name a font goes by, each leading to the name shown.</summary>
    private sealed record FontList(IReadOnlyList<string> Names, IReadOnlyDictionary<string, string> ByAnyName);

    private static FontList? _known;

    private static FontList Known => _known ??= ReadInstalled();

    /// <summary>The names of the fonts that can be chosen, in the language of the machine, sorted. Read once.</summary>
    public static IReadOnlyList<string> Installed => Known.Names;

    /// <summary>
    /// The name to show for a font, or null when no such font is installed (or it is a symbol font). Any name the font has will
    /// do, its English one or one in another language, so a theme or a setting written on a computer in another language still finds it.
    /// </summary>
    public static string? Find(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : Known.ByAnyName.GetValueOrDefault(name.Trim());

    /// <summary>The font the text uses for this name: the font, then Segoe UI. An unknown name is the default.</summary>
    public static FontFamily Build(string? name) =>
        new(Find(name) is { } found && found != DefaultName ? found + ", " + DefaultName : DefaultName);

    /// <summary>
    /// Puts the font for the theme on screen in the running app: its own <c>font</c> when it has one that is installed here, and
    /// otherwise the font from Settings. A start-up override beats both. A no-op with no <see cref="Application"/>.
    /// </summary>
    public static void ApplyForTheme(string? themeFont)
    {
        if (Application.Current == null) return;
        Fill(Application.Current.Resources, OverrideName ?? Find(themeFont) ?? SystemName);
    }

    /// <summary>Puts a font in this dictionary as <c>AppFont</c>: the app's own, or a window's, such as the theme editor's preview.</summary>
    public static void Fill(ResourceDictionary target, string? name) => target["AppFont"] = Build(name);

    /// <summary>
    /// Makes this window take the app's font, as a live resource. Most controls take it from their style, but text that has no style
    /// of its own takes the window's, which would otherwise be the system font. Called from the window's constructor, so the first
    /// measure (and the size and place a window gets from it) already has the font. A font the window sets itself is kept.
    /// </summary>
    public static void Follow(Window window)
    {
        if (window.ReadLocalValue(Control.FontFamilyProperty) == DependencyProperty.UnsetValue)
            window.SetResourceReference(Control.FontFamilyProperty, "AppFont");
    }

    /// <summary>
    /// An installed font picked at random for Randomize: one that can write ordinary English text, so a roll does not turn the
    /// labels into boxes (some fonts hold only symbols, or only another script). Null when none was found.
    /// </summary>
    public static string? PickRandom(Random random)
    {
        var all = Installed;
        for (int i = 0; i < 40; i++)
        {
            string name = all[random.Next(all.Count)];
            if (CanWriteEnglish(name)) return name;
        }
        return null;
    }

    private static bool CanWriteEnglish(string name)
    {
        try
        {
            var face = new FontFamily(name).GetTypefaces().FirstOrDefault();
            return face != null && face.TryGetGlyphTypeface(out var glyphs)
                && "Aa0 ,.".All(c => glyphs.CharacterToGlyphMap.ContainsKey(c));
        }
        catch
        {
            // A font that cannot be read is not one to roll.
            return false;
        }
    }

    private static FontList ReadInstalled()
    {
        var language = XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag);
        var names = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var byAnyName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in Fonts.SystemFontFamilies)
        {
            try
            {
                if (IsSymbolFont(family)) continue;
                string name = family.FamilyNames.TryGetValue(language, out var local) ? local : family.Source;
                // Fonts whose names start with "@" are the sideways copies of East Asian fonts.
                if (name.Length == 0 || name[0] == '@') continue;
                names.Add(name);
                string shown = names.TryGetValue(name, out var first) ? first : name;
                foreach (string alias in family.FamilyNames.Values.Append(family.Source).Append(name))
                    byAnyName.TryAdd(alias, shown);
            }
            catch
            {
                // A damaged font file is left out of the list; it is not worth stopping the list for.
            }
        }
        names.Add(DefaultName);
        byAnyName.TryAdd(DefaultName, DefaultName);
        return new FontList(names.ToList(), byAnyName);
    }

    private static bool IsSymbolFont(FontFamily family)
    {
        var face = family.GetTypefaces().FirstOrDefault();
        return face != null && face.TryGetGlyphTypeface(out var glyphs) && glyphs.Symbol;
    }
}
