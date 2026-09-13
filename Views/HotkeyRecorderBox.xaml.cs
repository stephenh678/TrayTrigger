using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TrayTrigger.Services;

namespace TrayTrigger.Views;

/// <summary>
/// Records a global hotkey from the keys the user actually presses, instead of parsing typed
/// text. Click (or tab to) the box, press the combination, done. Escape keeps the old value,
/// Backspace or Delete clears it, Tab leaves. Every candidate goes through
/// <see cref="HotkeyManager.CheckAvailability"/> before it is accepted, so a combo that would
/// silently fail to register - taken by another game, by the window hotkey, or by another app -
/// is refused here with the reason, rather than accepted and then ignored.
///
/// <see cref="Hotkey"/> is the stored string, in <see cref="HotkeyManager.Format"/>'s canonical
/// spelling; <see cref="OwnerId"/> says who the combo is for (a game's id, or
/// <see cref="HotkeyManager.ManageOwnerId"/>), so re-recording a game's own current hotkey is
/// not reported as a clash with itself.
/// </summary>
public partial class HotkeyRecorderBox : UserControl
{
    private const string Placeholder = "Click, then press keys";
    private const string RecordingPrompt = "Press keys… (Esc cancels, Backspace clears)";

    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey), typeof(string), typeof(HotkeyRecorderBox),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((HotkeyRecorderBox)d).ShowStoredValue()));

    public static readonly DependencyProperty OwnerIdProperty = DependencyProperty.Register(
        nameof(OwnerId), typeof(string), typeof(HotkeyRecorderBox), new PropertyMetadata(string.Empty));

    public string Hotkey
    {
        get => (string)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    public string OwnerId
    {
        get => (string)GetValue(OwnerIdProperty);
        set => SetValue(OwnerIdProperty, value);
    }

    // The current refusal or warning, for a host that wants to lay it out across the whole row
    // (ShowInlineMessage="False") instead of wrapped under a narrow box.
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(HotkeyRecorderBox), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty MessageBrushProperty = DependencyProperty.Register(
        nameof(MessageBrush), typeof(Brush), typeof(HotkeyRecorderBox), new PropertyMetadata(null));
    public static readonly DependencyProperty HasMessageProperty = DependencyProperty.Register(
        nameof(HasMessage), typeof(bool), typeof(HotkeyRecorderBox), new PropertyMetadata(false));
    public static readonly DependencyProperty ShowInlineMessageProperty = DependencyProperty.Register(
        nameof(ShowInlineMessage), typeof(bool), typeof(HotkeyRecorderBox), new PropertyMetadata(true));

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        private set => SetValue(MessageProperty, value);
    }

    public Brush? MessageBrush
    {
        get => (Brush?)GetValue(MessageBrushProperty);
        private set => SetValue(MessageBrushProperty, value);
    }

    public bool HasMessage
    {
        get => (bool)GetValue(HasMessageProperty);
        private set => SetValue(HasMessageProperty, value);
    }

    /// <summary>False when the host renders <see cref="Message"/> itself.</summary>
    public bool ShowInlineMessage
    {
        get => (bool)GetValue(ShowInlineMessageProperty);
        set => SetValue(ShowInlineMessageProperty, value);
    }

    private bool _recording;
    /// <summary>The app-shortcut combo the user was warned about; pressing it again accepts it.</summary>
    private string? _warnedCandidate;

    public HotkeyRecorderBox()
    {
        InitializeComponent();
        ShowStoredValue();
    }

    // ------------------------------------------------------------------ display

    private void ShowStoredValue()
    {
        if (_recording) return;
        string stored = Hotkey ?? string.Empty;
        bool hasValue = !string.IsNullOrWhiteSpace(stored);
        // A value typed under an older build is shown in the canonical spelling; the stored text
        // is left alone until the user records again, so nothing rewrites settings on its own.
        Display.Text = hasValue ? HotkeyManager.Normalize(stored) ?? stored : Placeholder;
        Display.Foreground = Brush(hasValue ? "BrushTextPrimary" : "BrushTextMuted");
        ClearButton.Visibility = hasValue ? Visibility.Visible : Visibility.Collapsed;
        Box.BorderBrush = Brush("BrushBorderDark");
        HideMessage();
    }

    private void ShowMessage(string text, bool warning = false)
    {
        var brush = Brush(warning ? "BrushWarning" : "BrushDanger");
        Message = text;
        MessageBrush = brush;
        HasMessage = true;
        InlineMessage.Text = text;
        InlineMessage.Foreground = brush;
        InlineMessage.Visibility = ShowInlineMessage ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HideMessage()
    {
        Message = string.Empty;
        HasMessage = false;
        InlineMessage.Visibility = Visibility.Collapsed;
    }

    private Brush Brush(string key) => (Brush)FindResource(key);

    // ------------------------------------------------------------------ recording

    private void StartRecording()
    {
        _recording = true;
        _warnedCandidate = null;
        Display.Text = RecordingPrompt;
        Display.Foreground = Brush("BrushTextSecondary");
        Box.BorderBrush = Brush("BrushAccent");
        ClearButton.Visibility = Visibility.Collapsed;
        HideMessage();
    }

    private void StopRecording()
    {
        _recording = false;
        ShowStoredValue();
    }

    private void OnBoxMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Already focused (a combo was just recorded, or Escape was pressed): a click records again.
        if (IsKeyboardFocused) StartRecording();
        else Focus();
        e.Handled = true;
    }

    private void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus == this) StartRecording();
    }

    private void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_recording) StopRecording();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        Hotkey = string.Empty;
        StopRecording();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recording) return;

        // Alt combinations arrive as Key.System with the real key in SystemKey.
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        // Plain Tab still moves focus, so the box can't trap keyboard users.
        if (key == Key.Tab && modifiers == ModifierKeys.None) return;

        e.Handled = true;

        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            StopRecording();
            return;
        }

        if (key is Key.Back or Key.Delete && modifiers == ModifierKeys.None)
        {
            Hotkey = string.Empty;
            StopRecording();
            return;
        }

        string? candidate = HotkeyManager.Format(modifiers, key);
        if (candidate == null)
        {
            // A modifier on its own: show what is held so far and wait for the key.
            Display.Text = modifiers == ModifierKeys.None ? RecordingPrompt : HotkeyManager.Format(modifiers, Key.F1)![..^2] + "…";
            return;
        }

        if (HotkeyManager.IsReservedByWindows(modifiers, key))
        {
            ShowMessage(modifiers.HasFlag(ModifierKeys.Windows)
                ? "Win+ shortcuts are reserved by Windows. Use Ctrl, Alt or Shift."
                : $"{candidate} belongs to Windows. Choose another combination.");
            return;
        }

        // A single modifier plus a letter or digit shadows that shortcut in every app while
        // TrayTrigger runs. Warn once; the same combo pressed again is taken as a decision.
        if (HotkeyManager.IsCommonAppShortcut(modifiers, key) && !string.Equals(_warnedCandidate, candidate, StringComparison.Ordinal))
        {
            _warnedCandidate = candidate;
            string safer = HotkeyManager.Format(modifiers | (modifiers == ModifierKeys.Control ? ModifierKeys.Alt : ModifierKeys.Control), key)!;
            ShowMessage($"{candidate} is a common app shortcut and would stop working everywhere while TrayTrigger runs. {safer} is safer. Press {candidate} again to use it anyway.", warning: true);
            return;
        }

        var manager = HotkeyManager.Instance;
        if (manager != null)
        {
            if (!manager.CheckAvailability(candidate, OwnerId ?? string.Empty, out string reason))
            {
                ShowMessage(reason);
                return;
            }
        }
        else if (!HotkeyManager.ParseHotkey(candidate, out _, out _))
        {
            ShowMessage("Use Ctrl, Alt or Shift plus a key, or a function key on its own.");
            return;
        }

        Hotkey = candidate;
        StopRecording();
    }
}
