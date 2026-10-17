using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TrayTrigger.Services;
using TrayTrigger.Services.Theming;
using TrayTrigger.Views;

namespace TrayTrigger.ViewModels;

/// <summary>
/// One of the colors the editor changes: its swatch, its name, and the #RRGGBB box next to it. A color can be set by the
/// picker or typed; what is typed is used as soon as it is a color, and an error is only shown once the box is left.
/// </summary>
public sealed class ThemeColorRow : ViewModelBase
{
    private Color _color;
    private string _hex;
    private string? _error;

    internal ThemeColorRow(string key, string label, string description, Color color)
    {
        Key = key;
        Label = label;
        Description = description;
        Original = color;
        Suggestions = ThemeCatalog.SuggestedColors(key);
        _color = color;
        _hex = ThemeColor.Format(color);
        Swatch = Freeze(color);
        ResetCommand = new RelayCommand(() => Color = Original, () => _color != Original);
    }

    /// <summary>The color's name in the theme file ("TextPrimary").</summary>
    public string Key { get; }
    public string Label { get; }
    public string Description { get; }

    /// <summary>What the color was when the editor opened.</summary>
    public Color Original { get; }

    public ICommand ResetCommand { get; }

    /// <summary>Colors the picker offers for this one.</summary>
    public IReadOnlyList<Color> Suggestions { get; }

    /// <summary>Raised when the person changed the color (not when it was worked out from another).</summary>
    internal event Action<ThemeColorRow>? Edited;

    public Color Color
    {
        get => _color;
        set => Set(value, rewriteText: true, byPerson: true);
    }

    public Brush Swatch { get; private set; }

    /// <summary>What is in the box: #RRGGBB, or whatever is being typed.</summary>
    public string Hex
    {
        get => _hex;
        set
        {
            if (_hex == value) return;
            _hex = value;
            OnPropertyChanged();
            if (ThemeColor.TryParseTyped(value, out var typed))
            {
                Error = null;
                Set(typed, rewriteText: false, byPerson: true);
            }
        }
    }

    /// <summary>Set once the box is left with something that is not a color; null otherwise.</summary>
    public string? Error
    {
        get => _error;
        private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); }
    }

    public bool HasError => _error != null;

    private string? _contrastNote;

    /// <summary>Why this color makes some text hard to read ("Text on a card is 2.1:1 ..."), or null when it does not.</summary>
    public string? ContrastNote
    {
        get => _contrastNote;
        private set { if (SetProperty(ref _contrastNote, value)) OnPropertyChanged(nameof(HasContrastIssue)); }
    }

    public bool HasContrastIssue => _contrastNote != null;

    internal void SetContrastNote(string? note) => ContrastNote = note;

    /// <summary>Called when the box loses focus or Enter is pressed: tidies a valid entry, and says what is wrong with an invalid one.</summary>
    public void CommitHex()
    {
        // What the box shows for a color it was given (a see-through #AARRGGBB from the file) is that color, not a mistake.
        if (_hex.Trim() == ThemeColor.Format(_color))
        {
            Error = null;
        }
        else if (ThemeColor.TryParseTyped(_hex, out var typed))
        {
            Error = null;
            Set(typed, rewriteText: true, byPerson: true);
        }
        else
        {
            Error = "Use six digits, like #238636.";
        }
    }

    /// <summary>Sets the color because another color moved it (a card color follows the background), without counting as an edit.</summary>
    internal void Follow(Color color) => Set(color, rewriteText: true, byPerson: false);

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void Set(Color color, bool rewriteText, bool byPerson)
    {
        bool changed = color != _color;
        _color = color;
        // A color put in from somewhere else (the picker, Randomize, Reset) replaces whatever was typed wrong.
        if (rewriteText) Error = null;
        if (changed)
        {
            Swatch = Freeze(color);
            OnPropertyChanged(nameof(Swatch));
            OnPropertyChanged(nameof(Color));
        }
        if (rewriteText && _hex != ThemeColor.Format(color))
        {
            _hex = ThemeColor.Format(color);
            OnPropertyChanged(nameof(Hex));
        }
        if (changed && byPerson) Edited?.Invoke(this);
        if (changed) CommandManager.InvalidateRequerySuggested();
    }
}

/// <summary>One entry of the Position list: the word the theme file uses, and what the window shows.</summary>
public sealed record PositionChoice(string Id, string Label);

/// <summary>
/// The Edit Theme window: the seven colors a theme is made of, its name, whether it starts from Dark or Light, and its
/// background picture. Nothing outside the window changes while it is open; the preview in the window shows the colors
/// (<see cref="Palette"/>, raised by <see cref="PaletteChanged"/>), and Save writes the theme file.
/// </summary>
public sealed class ThemeEditorViewModel : ViewModelBase
{
    private const double DefaultStrength = 0.6;

    private readonly ThemeService _themes;
    private readonly ThemeDefinition _source;
    private readonly bool _editingFile;
    private readonly Dictionary<string, Color> _handSet = new(StringComparer.Ordinal);
    private readonly string _initial;
    private bool _surfaceFollowsBackground;
    private readonly bool _surfaceListed;
    private readonly bool _openSurfaceFollows;
    private bool _isDark;
    private string _name;
    private double _strength;
    private string _position = BackgroundPosition.Center;
    private PictureChoice _picture = PictureChoice.Keep;
    private string? _pictureSource;
    private string _pictureInfo = string.Empty;
    private ThemeDefinition _pictureTheme;
    private BitmapSource? _thumbnail;
    private string _validationMessage = string.Empty;

    internal ThemeEditorViewModel(ThemeDefinition source, ThemeService themes)
    {
        _themes = themes;
        _source = source;
        _pictureTheme = source;
        _editingFile = source.Origin == ThemeOrigin.User && source.Path != null;
        _isDark = source.IsDark;
        _name = _editingFile ? source.Name : ThemeService.FreeThemeName("My " + source.Name);

        var palette = ThemeCatalog.Resolve(source);
        var worked = ThemeDerivation.Resolve(ThemeCatalog.BasePalette(_isDark), AnchorColors(palette));
        var listed = source.Colors;
        foreach (var (key, color) in palette)
        {
            if (ThemeDerivation.Anchors.ContainsKey(key) || key == "Surface") continue;
            // A theme file that lists a color pins it, whatever its value; a theme that becomes a new file only keeps what differs from what is worked out.
            bool pinned = _editingFile ? listed.ContainsKey(key) : color != worked[key];
            if (pinned) _handSet[key] = color;
        }
        _surfaceListed = _editingFile && listed.ContainsKey("Surface");
        _surfaceFollowsBackground = _editingFile ? !_surfaceListed : palette["Surface"] == worked["Surface"];

        Background = Row(palette, "Background", "Background", "Window and page");
        Surface = Row(palette, "Surface", "Surface", "Cards and panels");
        Text = Row(palette, "TextPrimary", "Text", "Main text");
        Accent = Row(palette, "Accent", "Accent", "Buttons, tabs, links");
        Danger = Row(palette, "Danger", "Danger", "Errors, delete buttons");
        Warning = Row(palette, "Warning", "Warning", "Warnings, highlights");
        Success = Row(palette, "SuccessText", "Success", "Success messages");
        Rows = [Background, Surface, Text, Accent, Danger, Warning, Success];
        foreach (var row in Rows) row.Edited += OnRowEdited;

        var background = ThemeCatalog.ResolveBackground(source);
        _strength = background?.Strength ?? DefaultStrength;
        _position = background?.Position ?? BackgroundPosition.Center;
        if (background != null)
        {
            _thumbnail = ThemeBackgroundLoader.Load(background, out _, ThumbnailPixels);
            _pictureInfo = _editingFile ? background.Image : "The picture of " + source.Name;
        }

        Corners = new ThemeCornersEditor(source);
        Corners.Changed += (_, _) =>
        {
            ClearMessage();
            ShapeChanged?.Invoke(this, EventArgs.Empty);
        };
        _useSettingsTextSize = source.TextSize == null;
        _textSize = Math.Clamp(source.TextSize ?? AppTypography.SystemPercent, MinTextSize, MaxTextSize);
        _useSettingsFont = source.Font == null;
        _font = AppFonts.Find(source.Font) ?? AppFonts.Find(AppFonts.SystemName) ?? AppFonts.DefaultName;
        _openedFont = _font;
        ResetTypeCommand = new RelayCommand(() =>
        {
            UseSettingsFont = true;
            UseSettingsTextSize = true;
        });
        TogglePreviewCommand = new RelayCommand(() => IsPreviewOpen = !IsPreviewOpen);
        _openSurfaceFollows = _surfaceFollowsBackground;
        RandomizeCommand = new RelayCommand(() => Randomize(true, true, look: true));
        RandomizeColorsCommand = new RelayCommand(() => Randomize(true, false));
        RandomizeCornersCommand = new RelayCommand(() => Randomize(false, true));
        ResetColorsCommand = new RelayCommand(ResetColors);
        UndoRandomizeCommand = new RelayCommand(UndoRandomize, () => _undo != null);

        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
        ChoosePictureCommand = new RelayCommand(ChoosePicture);
        RemovePictureCommand = new RelayCommand(RemovePicture, () => HasPicture);
        OpenFileCommand = new RelayCommand(OpenFile, () => _editingFile);

        Palette = BuildPalette();
        UpdateContrast();
        _initial = Snapshot();
    }

    /// <summary>A preview picture is decoded at about this many pixels: the thumbnail is small.</summary>
    private const long ThumbnailPixels = 320 * 180;

    // ---- What the window shows -------------------------------------------------------------------------------

    public ThemeColorRow Background { get; }
    public ThemeColorRow Surface { get; }
    public ThemeColorRow Text { get; }
    public ThemeColorRow Accent { get; }
    public ThemeColorRow Danger { get; }
    public ThemeColorRow Warning { get; }
    public ThemeColorRow Success { get; }
    public IReadOnlyList<ThemeColorRow> Rows { get; }

    /// <summary>Every color of the theme as it is now: the seven, and the ones worked out from them.</summary>
    internal ThemePalette Palette { get; private set; }

    /// <summary>Raised after <see cref="Palette"/> changed, for the preview to repaint.</summary>
    internal event EventHandler? PaletteChanged;

    private IReadOnlyList<PosterSample>? _samples;

    /// <summary>
    /// The game cards of the live preview: the player's own games where there are any, at the library's own size. They are made the
    /// first time the panel opens (it starts closed, and making them reads cover pictures), so this is empty until then.
    /// </summary>
    public IReadOnlyList<PosterSample> Samples => _samples ?? [];

    /// <summary>The corner shapes and sizes, for the Corners card.</summary>
    public ThemeCornersEditor Corners { get; }

    /// <summary>Raised after a corner changed, for the preview to repaint.</summary>
    internal event EventHandler? ShapeChanged;

    // ---- Randomize -------------------------------------------------------------------------------------------------

    /// <summary>What the colors, corners, text size, font and picture strength were before the last Randomize, for Undo; null before the first.</summary>
    private RandomizeUndo? _undo;

    private sealed record RandomizeUndo(Color[] Colors, bool SurfaceFollowsBackground, CornersState Corners,
        bool UseSettingsTextSize, int TextSize, bool UseSettingsFont, string Font, double Strength);

    /// <summary>Where the colors and corners come from; a test or a capture replaces it to repeat a result.</summary>
    internal Random Random { get; set; } = new();

    public ICommand RandomizeCommand { get; }
    public ICommand RandomizeColorsCommand { get; }
    public ICommand RandomizeCornersCommand { get; }
    public ICommand ResetColorsCommand { get; }

    /// <summary>True when a main color is not as it was when the window opened: the colors card shows its Reset link.</summary>
    public bool ColorsDiffer => Rows.Any(r => r.Color != r.Original) || _surfaceFollowsBackground != _openSurfaceFollows;

    private void ResetColors()
    {
        // The colors Reset puts back were worked out from the starting point the window opened with.
        IsDark = _source.IsDark;
        foreach (var row in Rows) row.Color = row.Original;
        _surfaceFollowsBackground = _openSurfaceFollows;
        Recompute();
    }
    public ICommand UndoRandomizeCommand { get; }

    /// <summary>True after a Randomize: the Undo link shows, and puts back what was there before it.</summary>
    public bool CanUndoRandomize => _undo != null;

    /// <summary>
    /// Fills the main colors, the corners or both at random as a new starting point (and, from the Randomize button at the top, the text
    /// size, the font and how strongly the picture shows, which are the theme's own from then on), which is then changed by hand like anything typed: the colors
    /// pass the contrast gate (their text is easy to read) and the corners can be anything the sliders allow. The name, the picture, the
    /// Dark or Light start and the fine-tuning colors a file lists are left as they are.
    /// </summary>
    private void Randomize(bool colors, bool corners, bool look = false)
    {
        ThemeRandomizer.Colors? found = null;
        if (colors)
        {
            found = ThemeRandomizer.Roll(Random, _isDark, c => ThemeCatalog.ContrastIssues(PaletteFor(c)).Count == 0);
            if (found == null)
            {
                ValidationMessage = "No colors that read well were found this time, with the colors the file sets by hand. Try again.";
                return;
            }
        }

        _undo = new RandomizeUndo(Rows.Select(r => r.Color).ToArray(), _surfaceFollowsBackground, Corners.Capture(),
            _useSettingsTextSize, _textSize, _useSettingsFont, _font, _strength);
        OnPropertyChanged(nameof(CanUndoRandomize));

        if (found != null)
        {
            // The card color follows the background again: it was worked out for these colors.
            Background.Color = found.Background;
            Text.Color = found.Text;
            Accent.Color = found.Accent;
            Danger.Color = found.Danger;
            Warning.Color = found.Warning;
            Success.Color = found.Success;
            _surfaceFollowsBackground = true;
            Recompute();
        }
        if (corners) Corners.Randomize(Random);
        if (look) RandomizeLook();
    }

    /// <summary>
    /// A text size within the sizes every screen is checked at, a font that can write English, and (when there is a picture to
    /// show) how strongly it shows. The theme gets its own text size and font, since a roll that followed Settings would show nothing.
    /// </summary>
    private void RandomizeLook()
    {
        _useSettingsTextSize = false;
        _textSize = Random.Next(AppTypography.MinScalePercent, AppTypography.RecommendedMaxScalePercent + 1);
        TextSizeEdited();

        if (AppFonts.PickRandom(Random) is { } font)
        {
            _useSettingsFont = false;
            _font = font;
            FontEdited();
        }

        if (HasPicture) Strength = Math.Round(Random.Next(4, 21) / 20.0, 2);
    }

    private void UndoRandomize()
    {
        if (_undo is not { } undo) return;
        _undo = null;
        OnPropertyChanged(nameof(CanUndoRandomize));

        for (int i = 0; i < Rows.Count; i++) Rows[i].Color = undo.Colors[i];
        _surfaceFollowsBackground = undo.SurfaceFollowsBackground;
        Recompute();
        Corners.Restore(undo.Corners);

        _useSettingsTextSize = undo.UseSettingsTextSize;
        _textSize = undo.TextSize;
        TextSizeEdited();
        _useSettingsFont = undo.UseSettingsFont;
        _font = undo.Font;
        FontEdited();
        Strength = undo.Strength;
    }

    /// <summary>Every color the theme would have with these main colors (and the fine-tuning ones a file sets by hand), for checking them before they are used.</summary>
    private ThemePalette PaletteFor(ThemeRandomizer.Colors colors)
    {
        var overrides = new Dictionary<string, Color>(_handSet)
        {
            ["Background"] = colors.Background,
            ["TextPrimary"] = colors.Text,
            ["Accent"] = colors.Accent,
            ["Danger"] = colors.Danger,
            ["Warning"] = colors.Warning,
            ["SuccessText"] = colors.Success,
        };
        return ThemeDerivation.Resolve(ThemeCatalog.BasePalette(_isDark), overrides);
    }

    private bool _isPreviewOpen;

    /// <summary>Whether the live preview panel is out. It starts closed; the window widens or narrows to match.</summary>
    public bool IsPreviewOpen
    {
        get => _isPreviewOpen;
        set
        {
            if (!SetProperty(ref _isPreviewOpen, value)) return;
            OnPropertyChanged(nameof(PreviewTabText));
            if (value && _samples == null)
            {
                _samples = PosterSample.Build(PosterSample.LibraryGames());
                OnPropertyChanged(nameof(Samples));
            }
        }
    }

    /// <summary>The words on the edge tab: where the preview is, and which way it goes.</summary>
    public string PreviewTabText => _isPreviewOpen ? "Live preview  <" : "Live preview  >";
    public ICommand TogglePreviewCommand { get; }

    /// <summary>Raised when the window should close: true when the theme was saved.</summary>
    public event Action<bool>? RequestClose;

    public string WindowTitle => _editingFile ? "Edit Theme" : "New Theme";
    public string Subtitle => _editingFile
        ? "The preview shows each change. Nothing is saved until you press Save Changes."
        : $"A copy of {_source.Name} with your changes. It is saved as a theme of your own.";

    public string HeadingText => string.IsNullOrWhiteSpace(_name) ? "New theme" : _name.Trim();

    public string Name
    {
        get => _name;
        set { if (SetProperty(ref _name, value)) { OnPropertyChanged(nameof(HeadingText)); ClearMessage(); } }
    }

    /// <summary>True for a theme that starts from Dark, false for one that starts from Light.</summary>
    public bool IsDark
    {
        get => _isDark;
        set
        {
            if (_isDark == value) return;
            _isDark = value;
            // A different starting point starts the colors again from it.
            var start = ThemeCatalog.BasePalette(value);
            foreach (var row in Rows.Where(r => r.Key != "Surface")) row.Follow(start[row.Key]);
            if (_surfaceFollowsBackground) Surface.Follow(start["Surface"]);
            FollowBasePicture();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsLight));
            Recompute();
        }
    }

    /// <summary>
    /// A new theme whose picture is just its starting theme's own (Dark's or Light's) takes the other one's when the starting
    /// point is flipped, so a Light copy of Dark does not come out with Dark's picture. A picture a theme has of its own
    /// (Abyssal's), or one chosen or removed here, is left alone.
    /// </summary>
    private void FollowBasePicture()
    {
        if (_editingFile || _picture != PictureChoice.Keep) return;
        var oldBase = ThemeCatalog.BuiltIn(_isDark ? ThemeCatalog.LightId : ThemeCatalog.DarkId);
        var newBase = ThemeCatalog.BuiltIn(_isDark ? ThemeCatalog.DarkId : ThemeCatalog.LightId);
        if (oldBase == null || newBase == null) return;
        string? shown = _pictureTheme.Background?.Image;
        if (shown != null && !string.Equals(shown, oldBase.Background?.Image, StringComparison.OrdinalIgnoreCase)) return;

        _pictureTheme = newBase;
        var background = ThemeCatalog.ResolveBackground(newBase);
        _thumbnail = background == null ? null : ThemeBackgroundLoader.Load(background, out _, ThumbnailPixels);
        _pictureInfo = "The picture of " + newBase.Name;
        Strength = background?.Strength ?? DefaultStrength;
        Position = background?.Position ?? BackgroundPosition.Center;
        PictureChanged();
    }

    public bool IsLight
    {
        get => !_isDark;
        set => IsDark = !value;
    }

    // ---- The picture ----------------------------------------------------------------------------------------------

    public bool HasPicture => _picture switch
    {
        PictureChoice.Remove => false,
        PictureChoice.Replace => true,
        _ => ThemeCatalog.ResolveBackground(_source) != null,
    };

    public BitmapSource? Thumbnail => _thumbnail;

    /// <summary>The picture's file name, or what choosing it found (its size, and anything worth knowing).</summary>
    public string PictureInfo => HasPicture ? _pictureInfo : "No picture. The page is the plain background color.";

    /// <summary>0 to 1: how much of the picture shows.</summary>
    public double Strength
    {
        get => _strength;
        set { if (SetProperty(ref _strength, Math.Round(Math.Clamp(value, 0, 1), 2))) { OnPropertyChanged(nameof(StrengthText)); OnPropertyChanged(nameof(ThumbnailTint)); ClearMessage(); } }
    }

    public string StrengthText => $"{Math.Round(_strength * 100)}%";

    /// <summary>
    /// How much of the Background color covers the thumbnail: what is left of the strength, as in the app, where the picture
    /// is dimmed by a layer of the theme's own background color. The thumbnail follows the slider as it moves.
    /// </summary>
    public double ThumbnailTint => 1 - _strength;

    public IReadOnlyList<PositionChoice> Positions { get; } =
        BackgroundPosition.All.Select(p => new PositionChoice(p, char.ToUpperInvariant(p[0]) + p[1..])).ToList();

    /// <summary>Which part of the picture stays in view when the window is a different shape: one of <see cref="BackgroundPosition.All"/>.</summary>
    public string Position
    {
        get => _position;
        set
        {
            string chosen = BackgroundPosition.Parse(value) ?? BackgroundPosition.Center;
            if (!SetProperty(ref _position, chosen)) return;
            OnPropertyChanged(nameof(ThumbnailHorizontal));
            OnPropertyChanged(nameof(ThumbnailVertical));
            ClearMessage();
        }
    }

    /// <summary>The thumbnail is cropped the way the window will crop the picture.</summary>
    public System.Windows.HorizontalAlignment ThumbnailHorizontal => BackgroundPosition.Horizontal(_position);
    public System.Windows.VerticalAlignment ThumbnailVertical => BackgroundPosition.Vertical(_position);

    // ---- Text size --------------------------------------------------------------------------------------------------

    private int _textSize = 100;
    private bool _useSettingsTextSize = true;

    public int MinTextSize => AppTypography.MinScalePercent;
    public int MaxTextSize => AppTypography.MaxScalePercent;

    /// <summary>True while the theme has no size of its own and shows at the size from Settings: the slider is off, and the file gets no line.</summary>
    public bool UseSettingsTextSize
    {
        get => _useSettingsTextSize;
        set
        {
            if (_useSettingsTextSize == value) return;
            _useSettingsTextSize = value;
            TextSizeEdited();
        }
    }

    public bool HasOwnTextSize => !_useSettingsTextSize;

    /// <summary>How large the app's text is while this theme is on, in percent: 100 is the size it was designed at. Its own, when <see cref="UseSettingsTextSize"/> is off.</summary>
    public int TextSize
    {
        get => _textSize;
        set
        {
            int percent = Math.Clamp(value, MinTextSize, MaxTextSize);
            bool changed = percent != _textSize;
            _textSize = percent;
            if (changed) TextSizeEdited();
            // Raised even when nothing changed: a number typed past the range comes back as the one it was brought to.
            else OnPropertyChanged();
        }
    }

    /// <summary>The size the preview and the warning go by: the theme's own, or Settings' when it has none.</summary>
    internal int EffectiveTextSize => _useSettingsTextSize ? AppTypography.SystemPercent : _textSize;

    private void TextSizeEdited()
    {
        ClearMessage();
        OnPropertyChanged(nameof(TextSize));
        OnPropertyChanged(nameof(UseSettingsTextSize));
        OnPropertyChanged(nameof(HasOwnTextSize));
        OnPropertyChanged(nameof(TextSizeIsLarge));
        OnPropertyChanged(nameof(CanResetType));
        TextSizeChanged?.Invoke(this, EventArgs.Empty);
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Raised when the text size changed, for the preview to show it.</summary>
    internal event EventHandler? TextSizeChanged;

    /// <summary>True past the size every screen is checked at: the window says some screens may clip text.</summary>
    public bool TextSizeIsLarge => EffectiveTextSize > AppTypography.RecommendedMaxScalePercent;

    public string TextSizeWarning => $"Past {AppTypography.RecommendedMaxScalePercent}% some screens may clip text. Lower the size to fix it.";

    public string TextSizeFollowNote => $"Follows the size in Settings, {AppTypography.SystemPercent}%.";


    // ---- Font -------------------------------------------------------------------------------------------------------

    private string _font = AppFonts.DefaultName;
    private bool _useSettingsFont = true;

    /// <summary>The font the list showed when the window opened: the theme's own if this computer has it, and otherwise the Settings font.</summary>
    private readonly string _openedFont;

    /// <summary>
    /// The font to write, null to follow Settings. A font left as it opened is written as the file has it: a theme shared by someone
    /// whose font this computer lacks (the list cannot show it) or spelled in another case keeps its own line.
    /// </summary>
    private string? FontToSave => _useSettingsFont ? null : _font == _openedFont && _source.Font != null ? _source.Font : _font;

    public IReadOnlyList<string> Fonts => AppFonts.Installed;

    /// <summary>True while the theme has no font of its own and shows in the font from Settings: the list is off, and the file gets no line.</summary>
    public bool UseSettingsFont
    {
        get => _useSettingsFont;
        set
        {
            if (_useSettingsFont == value) return;
            _useSettingsFont = value;
            FontEdited();
        }
    }

    public bool HasOwnFont => !_useSettingsFont;

    /// <summary>The font the app's text is set in while this theme is on. Its own, when <see cref="UseSettingsFont"/> is off.</summary>
    public string Font
    {
        get => _font;
        set
        {
            string chosen = AppFonts.Find(value) ?? _font;
            bool changed = chosen != _font;
            _font = chosen;
            if (changed) FontEdited();
            else OnPropertyChanged();
        }
    }

    /// <summary>The font the preview goes by: the theme's own, or Settings' when it has none.</summary>
    internal string EffectiveFont => _useSettingsFont ? AppFonts.SystemName : _font;

    private void FontEdited()
    {
        ClearMessage();
        OnPropertyChanged(nameof(Font));
        OnPropertyChanged(nameof(UseSettingsFont));
        OnPropertyChanged(nameof(HasOwnFont));
        OnPropertyChanged(nameof(CanResetType));
        FontChanged?.Invoke(this, EventArgs.Empty);
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Raised when the font changed, for the preview to show it.</summary>
    internal event EventHandler? FontChanged;

    public string FontFollowNote => $"Follows the font in Settings, {(AppFonts.SystemName.Length == 0 ? AppFonts.DefaultName : AppFonts.SystemName)}.";

    /// <summary>True when the theme has a font or a text size of its own: the card's Reset shows, and puts both back to following Settings.</summary>
    public bool CanResetType => !_useSettingsFont || !_useSettingsTextSize;

    public ICommand ResetTypeCommand { get; }
    public ICommand ChoosePictureCommand { get; }
    public ICommand RemovePictureCommand { get; }

    /// <summary>How the window asks for a picture file: the file picker. A test replaces it. Null when cancelled.</summary>
    internal Func<string?> PickPictureFile { get; set; } = DefaultPickPictureFile;

    /// <summary>How the window says a picture cannot be used. A test replaces it.</summary>
    internal Action<string, string> ShowProblem { get; set; } = (title, message) => ModernDialog.ShowWarning(WindowHelper.ActiveOwner(), title, message);

    private static string? DefaultPickPictureFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a background picture",
            Filter = "Pictures (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png",
            CheckFileExists = true,
        };
        // Earlier pictures of a theme live here, so going back to one is a click away.
        if (Directory.Exists(ThemeCatalog.UserThemesDirectory)) dialog.InitialDirectory = ThemeCatalog.UserThemesDirectory;
        return FileDialogCloak.Show(dialog) == true ? dialog.FileName : null;
    }

    private void ChoosePicture()
    {
        string? file = PickPictureFile();
        if (file == null) return;

        var check = ThemePictureCheck.Inspect(file);
        if (!check.Ok)
        {
            ShowProblem("That picture cannot be used", check.Message);
            return;
        }

        _picture = PictureChoice.Replace;
        _pictureSource = file;
        _pictureInfo = check.Message + (check.Note != null ? ". " + check.Note : string.Empty);
        _thumbnail = ThemeBackgroundLoader.Load(new ResolvedBackground(Path.GetFileName(file), false, Path.GetDirectoryName(file), 1), out _, ThumbnailPixels);
        PictureChanged();
    }

    private void RemovePicture()
    {
        _picture = PictureChoice.Remove;
        _pictureSource = null;
        _thumbnail = null;
        PictureChanged();
    }

    private void PictureChanged()
    {
        ClearMessage();
        OnPropertyChanged(nameof(HasPicture));
        OnPropertyChanged(nameof(Thumbnail));
        OnPropertyChanged(nameof(PictureInfo));
        CommandManager.InvalidateRequerySuggested();
    }

    // ---- Colors -----------------------------------------------------------------------------------------------------

    private static ThemeColorRow Row(ThemePalette palette, string key, string label, string description) => new(key, label, description, palette[key]);

    private static Dictionary<string, Color> AnchorColors(ThemePalette palette) =>
        palette.Where(p => ThemeDerivation.Anchors.ContainsKey(p.Key)).ToDictionary(p => p.Key, p => p.Value);

    private void OnRowEdited(ThemeColorRow row)
    {
        // A card color the person set stops following the background.
        if (ReferenceEquals(row, Surface)) _surfaceFollowsBackground = false;
        Recompute();
    }

    private ThemePalette BuildPalette()
    {
        var overrides = new Dictionary<string, Color>(_handSet);
        foreach (var row in Rows)
        {
            if (row.Key == "Surface" && _surfaceFollowsBackground) continue;
            overrides[row.Key] = row.Color;
        }
        return ThemeDerivation.Resolve(ThemeCatalog.BasePalette(_isDark), overrides);
    }

    private void Recompute()
    {
        Palette = BuildPalette();
        // A card color that follows the background moves with it.
        if (_surfaceFollowsBackground) Surface.Follow(Palette["Surface"]);
        ClearMessage();
        UpdateContrast();
        OnPropertyChanged(nameof(ColorsDiffer));
        PaletteChanged?.Invoke(this, EventArgs.Empty);
    }

    private List<ThemeCatalog.ContrastIssue> _issues = new();

    /// <summary>Works out what is hard to read once per change, and marks the colors that can fix it.</summary>
    private void UpdateContrast()
    {
        _issues = ThemeCatalog.ContrastIssues(Palette);
        foreach (var row in Rows)
        {
            // Every pair this color is part of, worded as in the line at the foot of the Main colors card, so a marked color and that line say the same thing.
            var mine = _issues.Where(i => i.Fixes.Contains(row.Key)).ToList();
            row.SetContrastNote(mine.Count == 0 ? null
                : "Hard to read with this color:\n" + string.Join("\n", mine.Select(i => "• " + i.Short)) + "\n4.5:1 or more reads comfortably.");
        }
        OnPropertyChanged(nameof(ContrastLine));
        OnPropertyChanged(nameof(ContrastOk));
        OnPropertyChanged(nameof(HasContrastLine));
    }

    /// <summary>Which pairs may be hard to read, or empty when none are (no all-clear is claimed); the colors that can fix it are marked in the list.</summary>
    public string ContrastLine => _issues.Count switch
    {
        0 => "",
        1 => $"{_issues[0].Label} may be hard to read. Hover the marked color for details.",
        _ => $"{_issues[0].Label} and {_issues.Count - 1} more pair{(_issues.Count == 2 ? "" : "s")} may be hard to read. Hover a marked color for details.",
    };

    public bool HasContrastLine => _issues.Count > 0;

    public bool ContrastOk => _issues.Count == 0;

    /// <summary>
    /// Said when the theme's file sets colors by hand: they keep their value when a main color changes, because a color
    /// that is listed is used as written. Empty when there are none.
    /// </summary>
    public string HandSetNote
    {
        get
        {
            if (!_editingFile || _handSet.Count == 0) return string.Empty;
            string names = string.Join(", ", _handSet.Keys.OrderBy(k => k, StringComparer.Ordinal).Take(3));
            string more = _handSet.Count > 3 ? $" and {_handSet.Count - 3} more" : string.Empty;
            return $"{_handSet.Count} fine-tuning color{(_handSet.Count == 1 ? " is" : "s are")} set in the file ({names}{more}), so they stay as they are when you change a main color. Open the file to change them.";
        }
    }

    public bool HasHandSetNote => HandSetNote.Length > 0;

    // ---- Save and cancel -----------------------------------------------------------------------------------------------

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenFileCommand { get; }

    public bool CanOpenFile => _editingFile;

    /// <summary>The theme file written by Save; null before then.</summary>
    internal string? SavedPath { get; private set; }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set { if (SetProperty(ref _validationMessage, value)) OnPropertyChanged(nameof(HasValidationMessage)); }
    }

    public bool HasValidationMessage => _validationMessage.Length > 0;

    private void ClearMessage() { if (_validationMessage.Length > 0) ValidationMessage = string.Empty; }

    public bool HasUnsavedChanges => Snapshot() != _initial;

    private string Snapshot() =>
        string.Join('|', _name, _isDark, string.Join(',', Rows.Select(r => ThemeColor.Format(r.Color))), _strength.ToString("0.00"), _position, _picture, _pictureSource, Corners.Snapshot(), _useSettingsTextSize ? "settings" : _textSize.ToString(), _useSettingsFont ? "settings" : _font);

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(_name))
        {
            ValidationMessage = "Give the theme a name.";
            return;
        }
        if (Rows.FirstOrDefault(r => r.HasError) is { } bad)
        {
            ValidationMessage = $"{bad.Label}: {bad.Error}";
            return;
        }

        // What changed, to write into a file that exists. A card color that follows the background is not written, unless the
        // file already has a line for it: that line would keep its old color and the file would no longer read back as shown.
        var changed = Rows
            .Where(r => r.Color != r.Original && !(r.Key == "Surface" && _surfaceFollowsBackground && !_surfaceListed))
            .ToDictionary(r => r.Key, r => r.Color);

        var (shapeSet, shapeClear) = Corners.ForSave(newFile: _source.Path == null);
        // A new theme keeps the picture the window showed, which after a flip is the other starting theme's.
        var request = new ThemeEditRequest(_editingFile ? _source : _pictureTheme, _name.Trim(), _isDark, Palette, changed, _picture, _pictureSource, _strength, _position, shapeSet, shapeClear, _useSettingsTextSize ? null : _textSize, FontToSave);
        var outcome = _themes.SaveTheme(request);
        if (!outcome.Ok)
        {
            ValidationMessage = outcome.Message;
            return;
        }

        SavedPath = outcome.ThemePath;
        RequestClose?.Invoke(true);
    }

    private void OpenFile()
    {
        if (_source.Path == null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_source.Path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { LoggingService.Swallowed("Theme", ex, "showing the theme file in Explorer"); }
    }
}
