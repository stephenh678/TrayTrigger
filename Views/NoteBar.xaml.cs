using System.Windows;
using System.Windows.Controls;

namespace TrayTrigger.Views;

/// <summary>What a <see cref="NoteBar"/> is telling the person.</summary>
public enum NoteKind
{
    /// <summary>What is so: a setting that is not in use, and why.</summary>
    Info,

    /// <summary>Something they could do.</summary>
    Tip,

    /// <summary>Something that may go wrong.</summary>
    Warning,
}

/// <summary>
/// A note under a setting, the one look for them: an icon and a line of text on a card, in the color of its <see cref="Kind"/>.
/// The colors are the theme's own (live resources), so the note follows the theme on screen.
/// </summary>
public partial class NoteBar : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(NoteBar), new PropertyMetadata(string.Empty, (d, e) => ((NoteBar)d).Message.Text = (string)e.NewValue));

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(NoteKind), typeof(NoteBar), new PropertyMetadata(NoteKind.Info, (d, e) => ((NoteBar)d).ShowKind((NoteKind)e.NewValue)));

    /// <summary>What goes after the text, such as a link; null for none.</summary>
    public static readonly DependencyProperty LinkProperty = DependencyProperty.Register(
        nameof(Link), typeof(object), typeof(NoteBar), new PropertyMetadata(null));

    public NoteBar()
    {
        InitializeComponent();
        ShowKind(Kind);
        // The card is Surface unless the note says otherwise (a note on a Surface card sets Background to DialogCard).
        SetResourceReference(BackgroundProperty, "BrushSurface");
    }

    /// <summary>The text, for a caller that fills it with its own inlines (bold, code).</summary>
    internal TextBlock MessageBlock => Message;

    public object? Link
    {
        get => GetValue(LinkProperty);
        set => SetValue(LinkProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public NoteKind Kind
    {
        get => (NoteKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>The icon (Segoe MDL2: info, light bulb, warning) and the text color for the kind.</summary>
    internal static (string Glyph, string Brush) Look(NoteKind kind) => kind switch
    {
        NoteKind.Tip => ("", "BrushWarning"),
        NoteKind.Warning => ("", "BrushWarning"),
        _ => ("", "BrushAccentText"),
    };

    private void ShowKind(NoteKind kind)
    {
        var (glyph, brush) = Look(kind);
        Glyph.Text = glyph;
        Glyph.SetResourceReference(TextBlock.ForegroundProperty, brush);
        Message.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }
}
