using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Views;

/// <summary>
/// The launch popup's window. See the XAML header for what it is; <see cref="LaunchPopupCoordinator"/>
/// decides what it shows and when.
/// </summary>
public partial class LaunchPopupWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20;
    private const long WsExToolWindow = 0x80;
    private const long WsExNoActivate = 0x08000000;

    private bool _clickThrough = true;
    private bool _hiding;
    private bool _progressRunning;

    public event Action? ActionClicked;
    public event Action? CloseClicked;

    public LaunchPopupWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyExtendedStyle();
        SizeChanged += (_, _) => PositionNearTray();
        ProgressTrack.SizeChanged += (_, _) =>
        {
            _progressRunning = false;
            StartProgress();
        };
    }

    public void Render(LaunchPopupContent content)
    {
        var accent = (Brush)FindResource(content.Kind switch
        {
            LaunchPopupKind.Failed => "BrushDanger",
            LaunchPopupKind.Notice => "BrushWarning",
            _ => "BrushAccent",
        });
        var secondary = (Brush)FindResource("BrushTextSecondary");

        Card.BorderBrush = accent;
        NameText.Text = content.Name;
        StatusText.Text = content.Status;
        StatusText.Foreground = content.IsInteractive ? accent : secondary;

        // Progress shows the game's icon; a failure or notice shows a warning sign instead.
        GameIcon.Source = content.ShowsProgress ? content.Icon : null;
        IconGlyph.Text = content.IsInteractive ? "" : "";
        IconGlyph.Foreground = content.IsInteractive ? accent : secondary;
        IconGlyph.Visibility = GameIcon.Source == null ? Visibility.Visible : Visibility.Collapsed;

        DetailText.Text = content.Detail ?? string.Empty;
        DetailText.Visibility = string.IsNullOrWhiteSpace(content.Detail) ? Visibility.Collapsed : Visibility.Visible;
        ActionButton.Content = content.ActionText;
        ActionButton.Visibility = string.IsNullOrWhiteSpace(content.ActionText) ? Visibility.Collapsed : Visibility.Visible;
        CloseButton.Visibility = content.IsInteractive ? Visibility.Visible : Visibility.Collapsed;

        ProgressTrack.Visibility = content.ShowsProgress ? Visibility.Visible : Visibility.Collapsed;
        if (content.ShowsProgress) StartProgress();
        else StopProgress();

        // Only a popup with nothing to click lets clicks through to the game underneath.
        _clickThrough = !content.IsInteractive;
        ApplyExtendedStyle();
    }

    public void FadeIn()
    {
        _hiding = false;
        if (!IsVisible) Show();
        PositionNearTray();
        // With Windows' Animation effects off the card simply appears (Views/Motion.cs).
        BeginAnimation(OpacityProperty, Motion.IsEnabled ? new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)) : null);
        if (!Motion.IsEnabled) Opacity = 1;
    }

    public void FadeOutAndHide()
    {
        if (!IsVisible || _hiding) return;
        _hiding = true;
        if (!Motion.IsEnabled)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 0;
            Hide();
            StopProgress();
            return;
        }
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) =>
        {
            if (!_hiding) return;
            Hide();
            StopProgress();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Bottom-right of the primary monitor's work area, where the tray is. The card's own
    /// margin keeps it off the taskbar and the screen edge.</summary>
    private void PositionNearTray()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth;
        Top = area.Bottom - ActualHeight;
    }

    private void StartProgress()
    {
        double width = ProgressTrack.ActualWidth;
        if (_progressRunning || width <= 0 || ProgressTrack.Visibility != Visibility.Visible) return;
        _progressRunning = true;
        var sweep = new DoubleAnimation(-ProgressBar.Width, width, TimeSpan.FromSeconds(1.4))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        ProgressShift.BeginAnimation(TranslateTransform.XProperty, sweep);
    }

    private void StopProgress()
    {
        _progressRunning = false;
        ProgressShift.BeginAnimation(TranslateTransform.XProperty, null);
    }

    private void ApplyExtendedStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        try
        {
            long style = GetExStyle(hwnd) | WsExNoActivate | WsExToolWindow;
            style = _clickThrough ? style | WsExTransparent : style & ~WsExTransparent;
            SetExStyle(hwnd, style);
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("LaunchPopup", $"Couldn't set the popup's window style: {ex.Message}");
        }
    }

    private void OnActionClick(object sender, RoutedEventArgs e) => ActionClicked?.Invoke();

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseClicked?.Invoke();

    private static long GetExStyle(IntPtr hwnd) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, GwlExStyle).ToInt64() : GetWindowLong32(hwnd, GwlExStyle);

    private static void SetExStyle(IntPtr hwnd, long style)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, GwlExStyle, new IntPtr(style));
        else SetWindowLong32(hwnd, GwlExStyle, unchecked((int)style));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);
}

/// <summary>Creates the popup window on first use, so a session that never launches from the tray
/// or a hotkey never builds it.</summary>
public sealed class LaunchPopupHost : ILaunchPopupView
{
    private LaunchPopupWindow? _window;

    public event Action? ActionClicked;
    public event Action? CloseClicked;

    public void Show(LaunchPopupContent content)
    {
        try
        {
            if (_window == null)
            {
                _window = new LaunchPopupWindow();
                _window.ActionClicked += () => ActionClicked?.Invoke();
                _window.CloseClicked += () => CloseClicked?.Invoke();
            }
            _window.Render(content);
            _window.FadeIn();
        }
        catch (Exception ex)
        {
            LoggingService.Warn("LaunchPopup", $"Couldn't show the launch popup: {ex.Message}");
        }
    }

    public void Hide()
    {
        try { _window?.FadeOutAndHide(); }
        catch (Exception ex) { LoggingService.Verbose("LaunchPopup", $"Couldn't hide the launch popup: {ex.Message}"); }
    }

    public void Dispose()
    {
        try { _window?.Close(); } catch { }
        _window = null;
    }
}
