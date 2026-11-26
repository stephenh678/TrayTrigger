namespace TrayTrigger.Services;

/// <summary>
/// How the Library's tab row shares its width between the category strip (left) and the collection strip (right).
/// Each strip takes the width its tabs need. Only when both can't fit do they share the row, at the split the
/// divider between them was dragged to (the categories' share), and neither goes below <see cref="MinStripWidth"/>.
/// A strip that needs less than its share gives the rest to the other one.
/// </summary>
public static class LibraryTabRowLayout
{
    public const double DefaultSplit = 0.6;
    public const double MinStripWidth = 120;

    /// <summary>A saved split, kept to the range a drag can produce; anything unreadable is the default.</summary>
    public static double ClampSplit(double split) => double.IsFinite(split) ? Math.Clamp(split, 0.05, 0.95) : DefaultSplit;

    /// <summary>
    /// Both strips show and can't both fit, so they share the row at the split. Only then does the divider's drag
    /// mean anything: while both fit, each has all it needs whatever the split is.
    /// </summary>
    public static bool Shares(double available, double categoriesNeed, double collectionsNeed) =>
        collectionsNeed > 0 && categoriesNeed + collectionsNeed > Math.Max(0, available);

    /// <param name="available">The row's width, less the divider when collections show.</param>
    /// <param name="categoriesNeed">The width the category strip needs to show every tab.</param>
    /// <param name="collectionsNeed">The same for the collection strip; 0 when it is hidden.</param>
    public static (double Categories, double Collections) Widths(double available, double categoriesNeed, double collectionsNeed, double split)
    {
        available = Math.Max(0, available);
        if (collectionsNeed <= 0) return (Math.Min(categoriesNeed, available), 0);
        if (!Shares(available, categoriesNeed, collectionsNeed)) return (categoriesNeed, collectionsNeed);

        double min = Math.Min(MinStripWidth, available / 2);
        double categories = Math.Clamp(available * ClampSplit(split), min, available - min);
        // A strip that fits in less than its share keeps only what it needs; the other one has the rest.
        if (categoriesNeed < categories) categories = categoriesNeed;
        else if (collectionsNeed < available - categories) categories = available - collectionsNeed;
        return (categories, available - categories);
    }

    /// <summary>The split that puts the divider where it was dragged to, kept to the minimum strip widths.</summary>
    public static double SplitFor(double categoriesWidth, double available)
    {
        if (available <= 0) return DefaultSplit;
        double min = Math.Min(MinStripWidth, available / 2);
        return ClampSplit(Math.Clamp(categoriesWidth, min, available - min) / available);
    }
}
