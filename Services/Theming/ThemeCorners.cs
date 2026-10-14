using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace TrayTrigger.Services.Theming;

/// <summary>
/// A shape the Edit Theme window offers for a kind of corner: which corners are round and how round each is next to
/// the size chosen. Shapes exist only in the window; a theme file holds the plain numbers they work out to.
/// </summary>
public sealed class CornerShape
{
    /// <summary>A corner that is as round as the shape allows, whatever the size chosen (a pill, the round end of a cap).</summary>
    internal const double Full = -1;

    private readonly double[] _corners;

    internal CornerShape(string id, string name, params double[] corners)
    {
        Id = id;
        Name = name;
        _corners = corners;
    }

    public string Id { get; }
    public string Name { get; }

    /// <summary>The entry that means "use the shape chosen above" (or the card's, for a poster).</summary>
    public bool IsSame { get; internal init; }

    /// <summary>The entry that stands for numbers a theme file holds that no shape here draws.</summary>
    public bool IsCustom { get; internal init; }

    /// <summary>True for a real shape, as opposed to the "same as" and "custom" entries.</summary>
    public bool HasPicture => !IsSame && !IsCustom;

    /// <summary>True when the size does not change the shape (a pill is as round as it can be).</summary>
    public bool IsFull => _corners.Contains(Full);

    /// <summary>The shape as a small picture in the list.</summary>
    public CornerRadius Preview => HasPicture ? At(10, 999) : default;

    /// <summary>
    /// The radius of each corner for a size in pixels, kept within 0 and <paramref name="max"/>. A fully round corner is
    /// <paramref name="full"/> pixels: enough to round the end of the tallest thing of that kind, and no more, because a
    /// radius much larger than half of a wide shape's height stretches into a lens instead of a pill.
    /// </summary>
    public CornerRadius At(double px, double max, double full = 999)
    {
        double Corner(double part) => Math.Round(Math.Clamp(part == Full ? Math.Min(max, full) : part * px, 0, max), 2);
        return new CornerRadius(Corner(_corners[0]), Corner(_corners[1]), Corner(_corners[2]), Corner(_corners[3]));
    }

    public override string ToString() => Name;
}

/// <summary>
/// One of the four kinds of corner a theme sets: its name in the file, how many pixels a step of the size slider is up to
/// <see cref="ThemeCorners.NormalSteps"/> and past it, how large it may be, and how large a fully round corner is.
/// </summary>
internal sealed record CornerTarget(string Key, string Label, double PxPerStep, double ExtraPerStep, double Max, double FullPx)
{
    /// <summary>The size in pixels at a step of the slider: even steps up to the normal range, steeper past it.</summary>
    public double Px(double step) =>
        step <= ThemeCorners.NormalSteps ? step * PxPerStep : ThemeCorners.NormalSteps * PxPerStep + (step - ThemeCorners.NormalSteps) * ExtraPerStep;
}

/// <summary>The shapes and the kinds of corner the editor offers, and how a set of numbers from a theme file is matched back to a shape and a size.</summary>
internal static class ThemeCorners
{
    /// <summary>The size the app's own corners are at: a tag 4 px, a control 6 px, a card 8 px.</summary>
    public const double DefaultStep = 4;

    /// <summary>The size slider runs from 0 to this.</summary>
    public const double MaxStep = 16;

    /// <summary>Up to this step the size is the usual range; past it the slider says "extreme" and each step is larger.</summary>
    public const double NormalSteps = 8;

    private const double F = CornerShape.Full;

    /// <summary>The shapes, in the order the list shows them. The first is today's look.</summary>
    public static readonly IReadOnlyList<CornerShape> Shapes =
    [
        new("rounded", "Rounded", 1, 1, 1, 1),
        new("leaf", "Leaf", 0, 1, 0, 1),
        new("reverse-leaf", "Reverse leaf", 1, 0, 1, 0),
        new("folder-tab", "Folder tab", 1, 1, 0, 0),
        new("speech-bubble", "Speech bubble", 1, 1, 1, 0),
        new("pill", "Pill", F, F, F, F),
        new("hanging", "Hanging", 0, 0, 1, 1),
        new("left-cap", "Left cap", F, 0, 0, F),
        new("one-big-corner", "One big corner", 2, 0, 0, 0),
        new("ramp", "Ramp", 0, 1 / 3.0, 2 / 3.0, 1),
        new("blade", "Blade", 1, 0.15, 1, 0.15),
    ];

    /// <summary>
    /// Tags, controls, cards and posters, in the order the window lists them. A step of the slider is a pixel for a tag,
    /// a pixel and a half for a control and two for a card, so step 4 is the app's own look for all three. Past step 8 a
    /// step is larger (a poster is 180 pixels wide: a few more pixels would hardly show).
    /// </summary>
    public static readonly IReadOnlyList<CornerTarget> Targets =
    [
        new("RadiusSmall", "Tags", 1, 1, ThemeShape.Settable["RadiusSmall"], 16),
        new("RadiusControl", "Controls", 1.5, 3, ThemeShape.Settable["RadiusControl"], 16),
        new("RadiusCard", "Cards", 2, 6, ThemeShape.Settable["RadiusCard"], 64),
        new("RadiusPoster", "Posters", 2, 10, ThemeShape.Settable["RadiusPoster"], 96),
    ];

    public static CornerShape SameAs(string what) => new("same", what) { IsSame = true };

    public static CornerShape Custom() => new("custom", "Custom (from the file)") { IsCustom = true };

    /// <summary>Whether two radii are the same at every corner (to a hundredth of a pixel).</summary>
    public static bool Same(CornerRadius a, CornerRadius b) =>
        Math.Abs(a.TopLeft - b.TopLeft) < 0.01 && Math.Abs(a.TopRight - b.TopRight) < 0.01
        && Math.Abs(a.BottomRight - b.BottomRight) < 0.01 && Math.Abs(a.BottomLeft - b.BottomLeft) < 0.01;

    /// <summary>The shape and size that work out to exactly these numbers, or null when none does (a file written by hand).</summary>
    public static (CornerShape Shape, double Step)? Recognise(CornerTarget target, CornerRadius value)
    {
        foreach (var shape in Shapes)
        {
            if (shape.IsFull)
            {
                if (Same(shape.At(0, target.Max, target.FullPx), value)) return (shape, DefaultStep);
                continue;
            }
            for (double step = 0; step <= MaxStep; step++)
                if (Same(shape.At(target.Px(step), target.Max, target.FullPx), value)) return (shape, step);
        }
        return null;
    }
}
