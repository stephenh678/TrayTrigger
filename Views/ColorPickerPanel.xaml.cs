using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TrayTrigger.Services.Theming;

namespace TrayTrigger.Views;

/// <summary>A suggested color in the picker: the color, a swatch of it, and its #RRGGBB name.</summary>
public sealed class ColorSuggestion
{
    public ColorSuggestion(Color color)
    {
        Color = color;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Brush = brush;
        Text = ThemeColor.Format(color);
    }

    public Color Color { get; }
    public Brush Brush { get; }
    public string Text { get; }
}

/// <summary>
/// Picks a color: drag in the square (across for saturation, up for brightness) or use the arrow keys; the strip
/// below sets the hue; suggested colors and the color it started as are one click away. <see cref="Color"/> follows
/// as you drag, two ways, so a box that shows the color in text stays in step.
/// </summary>
public partial class ColorPickerPanel : UserControl
{
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(Color), typeof(ColorPickerPanel),
        new FrameworkPropertyMetadata(default(Color), FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, e) => ((ColorPickerPanel)d).OnColorSet((Color)e.NewValue)));

    public static readonly DependencyProperty OriginalColorProperty = DependencyProperty.Register(
        nameof(OriginalColor), typeof(Color), typeof(ColorPickerPanel),
        new PropertyMetadata(default(Color), (d, _) => ((ColorPickerPanel)d).ShowChips()));

    public static readonly DependencyProperty SuggestionsProperty = DependencyProperty.Register(
        nameof(Suggestions), typeof(IEnumerable<Color>), typeof(ColorPickerPanel),
        new PropertyMetadata(null, (d, e) => ((ColorPickerPanel)d).ShowSuggestions((IEnumerable<Color>?)e.NewValue)));

    private const double SmallStep = 0.01, BigStep = 0.10;

    private double _hue, _saturation, _value;
    private bool _changing;
    private bool _dragging;

    public ColorPickerPanel()
    {
        InitializeComponent();

        // The drawing: a square that fades from white on the left to the hue on the right, and from clear at the top to black at the foot.
        var white = ThemeColor.FromHsv(0, 0, 1);
        var black = ThemeColor.FromHsv(0, 0, 0);
        WhiteFade.Fill = Frozen(new LinearGradientBrush(white, ThemeColor.WithAlpha(white, 0), 0));
        BlackFade.Fill = Frozen(new LinearGradientBrush(ThemeColor.WithAlpha(black, 0), black, 90));

        var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i <= 6; i++) rainbow.GradientStops.Add(new GradientStop(ThemeColor.FromHsv(i * 60, 1, 1), i / 6.0));
        HueStrip.Background = Frozen(rainbow);
    }

    public Color Color
    {
        get => (Color)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    /// <summary>The color the picker opened with: the chip beside the current one puts it back.</summary>
    public Color OriginalColor
    {
        get => (Color)GetValue(OriginalColorProperty);
        set => SetValue(OriginalColorProperty, value);
    }

    public IEnumerable<Color>? Suggestions
    {
        get => (IEnumerable<Color>?)GetValue(SuggestionsProperty);
        set => SetValue(SuggestionsProperty, value);
    }

    /// <summary>Puts the keyboard on the square, so the arrow keys work as soon as the picker opens.</summary>
    public void FocusSquare() => Square.Focus();

    // ---- The color changed from outside -----------------------------------------------------------------------

    private void OnColorSet(Color color)
    {
        if (_changing) return;
        var (h, s, v) = ThemeColor.ToHsv(color);
        // A grey or black has no hue of its own: keep the one the strip is on.
        if (s > 0 && v > 0) _hue = h;
        _saturation = s;
        _value = v;
        Show();
    }

    // ---- The person moves something -----------------------------------------------------------------------------

    private void Apply()
    {
        _changing = true;
        try
        {
            // A color that is see-through (cards over the picture) keeps how see-through it is while its hue and brightness move.
            Color = ThemeColor.WithAlpha(ThemeColor.FromHsv(_hue, _saturation, _value), Color.A);
        }
        finally { _changing = false; }
        Show();
    }

    private void PickAt(Point point)
    {
        _saturation = Math.Clamp(point.X / Math.Max(1, Square.ActualWidth), 0, 1);
        _value = 1 - Math.Clamp(point.Y / Math.Max(1, Square.ActualHeight), 0, 1);
        Apply();
    }

    private void Square_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Square.Focus();
        Square.CaptureMouse();
        _dragging = true;
        PickAt(e.GetPosition(Square));
        e.Handled = true;
    }

    private void Square_MouseMove(object sender, MouseEventArgs e)
    {
        // The button can be let go where nothing sees it (the window lost the mouse mid-drag): then the drag is over.
        if (_dragging && e.LeftButton != MouseButtonState.Pressed) _dragging = false;
        if (_dragging) PickAt(e.GetPosition(Square));
    }

    private void Square_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        Square.ReleaseMouseCapture();
    }

    private void Square_LostMouseCapture(object sender, MouseEventArgs e) => _dragging = false;

    private void Square_KeyDown(object sender, KeyEventArgs e)
    {
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? BigStep : SmallStep;
        switch (e.Key)
        {
            case Key.Left: _saturation = Math.Clamp(_saturation - step, 0, 1); break;
            case Key.Right: _saturation = Math.Clamp(_saturation + step, 0, 1); break;
            case Key.Up: _value = Math.Clamp(_value + step, 0, 1); break;
            case Key.Down: _value = Math.Clamp(_value - step, 0, 1); break;
            default: return;
        }
        e.Handled = true;
        Apply();
    }

    private void HueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_changing) return;
        _hue = e.NewValue;
        Apply();
    }

    private void Square_SizeChanged(object sender, SizeChangedEventArgs e) => Show();

    private void OriginalChip_Click(object sender, RoutedEventArgs e) => Color = OriginalColor;

    private void Suggestion_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is ColorSuggestion suggestion) Color = suggestion.Color;
    }

    // ---- What is on screen --------------------------------------------------------------------------------------

    private void Show()
    {
        HueFill.Fill = Frozen(new SolidColorBrush(ThemeColor.FromHsv(_hue, 1, 1)));
        Canvas.SetLeft(Handle, _saturation * Square.ActualWidth - Handle.Width / 2);
        Canvas.SetTop(Handle, (1 - _value) * Square.ActualHeight - Handle.Height / 2);

        // The slider only mirrors the hue here; setting it must not count as the person moving it.
        bool was = _changing;
        _changing = true;
        HueSlider.Value = Math.Min(_hue, HueSlider.Maximum);
        _changing = was;

        ShowChips();
    }

    private void ShowChips()
    {
        CurrentChip.Background = Frozen(new SolidColorBrush(Color));
        OriginalChip.Background = Frozen(new SolidColorBrush(OriginalColor));
    }

    private void ShowSuggestions(IEnumerable<Color>? colors)
    {
        var items = (colors ?? Enumerable.Empty<Color>()).Select(c => new ColorSuggestion(c)).ToList();
        SuggestionList.ItemsSource = items;
        SuggestionList.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static T Frozen<T>(T brush) where T : Freezable
    {
        brush.Freeze();
        return brush;
    }
}
