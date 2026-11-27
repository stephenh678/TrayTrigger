using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using TrayTrigger.Services;
using TrayTrigger.Services.Theming;
using TrayTrigger.ViewModels;

namespace TrayTrigger;

/// <summary>
/// The full-screen Library (<see cref="MainViewModel.IsFullScreenLibrary"/>): the window fills the screen with no title
/// bar, the sidebar and the Library's header are hidden, the games show as Extra Large posters, text is at least
/// <see cref="FullScreenTextScale"/>, and the controller is read. Everything is put back as it was when it is left:
/// the window's size and place, the view and the text size, none of which is saved while it is on.
/// </summary>
public partial class MainWindow
{
    /// <summary>The smallest text size in the full-screen Library: 130%, the largest every layout is checked clean at.</summary>
    internal const double FullScreenTextScale = 1.3;

    private (WindowState State, WindowStyle Style, ResizeMode Resize)? _beforeFullScreen;
    private string? _viewModeBeforeFullScreen;

    /// <summary>Whether the window is in the full-screen Library: its placement is not recorded meanwhile.</summary>
    internal bool IsFullScreen => _beforeFullScreen != null;

    private void WireFullScreen()
    {
        _viewModel.PropertyChanged += OnFullScreenChanged;
        Closed += (_, _) => _viewModel.PropertyChanged -= OnFullScreenChanged;
    }

    private void OnFullScreenChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsFullScreenLibrary)) return;
        if (_viewModel.IsFullScreenLibrary) EnterFullScreen();
        else LeaveFullScreen();
    }

    private void EnterFullScreen()
    {
        if (IsFullScreen) return;
        CaptureWindowPlacement(); // the window as it is, kept to go back to
        _beforeFullScreen = (WindowState == WindowState.Minimized ? WindowState.Normal : WindowState, WindowStyle, ResizeMode);

        var settings = _viewModel.SettingsVM;
        if (settings.LibraryViewMode != SettingsViewModel.ViewModeExtraLarge)
        {
            _viewModeBeforeFullScreen = settings.LibraryViewMode;
            settings.LibraryViewMode = SettingsViewModel.ViewModeExtraLarge;
        }
        AppTypography.FullScreenMinimumScale = FullScreenTextScale;
        AppTypography.ApplyForTheme(ThemeService.Instance.Active.TextSize);

        // With no frame, a maximized window covers the taskbar too. Maximized from Normal, so a window that was
        // already maximized (with its frame) is maximized again without it.
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;
        LoggingService.Info("MainWindow", "Full-screen Library on");

        // On a game, for the controller, once the bigger posters are laid out.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (IsFullScreen) LibraryPage.FocusGames(); });
    }

    private void LeaveFullScreen()
    {
        if (_beforeFullScreen is not { } before) return;
        WindowState = WindowState.Normal;
        WindowStyle = before.Style;
        ResizeMode = before.Resize;
        _beforeFullScreen = null;
        WindowState = before.State;

        // The view goes back unless it was changed while full screen (then the change stands).
        var settings = _viewModel.SettingsVM;
        if (_viewModeBeforeFullScreen is { } mode && settings.LibraryViewMode == SettingsViewModel.ViewModeExtraLarge)
            settings.LibraryViewMode = mode;
        _viewModeBeforeFullScreen = null;
        AppTypography.FullScreenMinimumScale = null;
        AppTypography.ApplyForTheme(ThemeService.Instance.Active.TextSize);
        LoggingService.Info("MainWindow", "Full-screen Library off");
    }
}
