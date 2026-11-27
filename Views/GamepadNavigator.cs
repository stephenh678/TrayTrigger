using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Views;

/// <summary>
/// Settings > Library &amp; Art "Use a game controller in the Library" (off by default). While the Library page,
/// Game Details or a window either of them opened (Change Category, Collections, a prompt) is the window in front,
/// a controller moves around them:
/// <list type="bullet">
/// <item>D-pad or left stick: the arrow keys in the Library's views; in Game Details, to the next button that way.</item>
/// <item>A: Enter on a game (Game Details), or presses the focused button. B: Esc (back, close, clear).</item>
/// <item>X: plays the focused game. Menu (Start): its right-click menu. LB / RB: the previous or next category tab.</item>
/// </list>
/// Xbox controllers and handhelds are read through XInput, PlayStation controllers (DualShock 4, DualSense) through
/// HID. Nothing is read while any other window (a game included) is in front, and the keys only ever go to
/// TrayTrigger's own windows: nothing is sent to Windows or to another program.
/// </summary>
public sealed class GamepadNavigator
{
    private static readonly TimeSpan ActiveInterval = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMilliseconds(250);

    private readonly MainViewModel _main;
    private readonly Func<PadState?> _read;
    private readonly GamepadInput _input = new();
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _reading;
    // The game focus was last on in the Library, to go back to when focus comes back without one (Game Details
    // closing hands it to the window, and a tab change can take the card away).
    private GameCardViewModel? _lastGame;
    // The control last used in the bar above the games, to go back to when Up leaves the games again.
    private IInputElement? _lastTop;

    public GamepadNavigator(MainViewModel main, Func<PadState?>? read = null)
    {
        _main = main;
        _read = read ?? Combine(new XInputReader().Read, new PlayStationReader().Read);
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = IdleInterval };
        _timer.Tick += (_, _) => Tick();
        main.SettingsVM.PropertyChanged += OnSettingChanged;
        Apply();
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(SettingsViewModel.ControllerNavigation)) Apply();
    }

    private void Apply()
    {
        _reading = false;
        if (_main.SettingsVM.ControllerNavigation) _timer.Start();
        else _timer.Stop();
    }

    /// <summary>
    /// Xbox controllers (and handhelds) through XInput and PlayStation controllers through HID, as one: the buttons
    /// held on either, and the stick of the first that has one pushed. Null only when neither has a controller.
    /// </summary>
    internal static Func<PadState?> Combine(Func<PadState?> first, Func<PadState?> second) => () =>
    {
        PadState? a = first(), b = second();
        if (a is not { } x) return b;
        if (b is not { } y) return x;
        bool xStick = x.LeftX != 0 || x.LeftY != 0;
        return new PadState((ushort)(x.Buttons | y.Buttons), xStick ? x.LeftX : y.LeftX, xStick ? x.LeftY : y.LeftY);
    };

    /// <summary>Runs the navigator's own timer whatever the setting says, for the diagnostics.</summary>
    internal void RunTimerForTest(bool run)
    {
        _reading = false;
        if (run) _timer.Start();
        else _timer.Stop();
    }

    /// <summary>One reading; public so the diagnostics can step it with readings of their own.</summary>
    public void Tick()
    {
        var window = TargetWindow();
        var state = window != null ? _read() : null;
        if (window == null || state == null)
        {
            _reading = false;
            _timer.Interval = IdleInterval;
            return;
        }
        _timer.Interval = ActiveInterval;
        long now = _clock.ElapsedMilliseconds;
        if (!_reading)
        {
            _input.Reset(state.Value, now);
            _reading = true;
            return;
        }
        var actions = _input.Update(state.Value, now);
        if (actions.Count == 0) return;
        // Acted on just after this reading, not inside it: a DispatcherTimer doesn't fire again until its Tick has
        // returned, and A on a game opens Game Details modally, so a press acted on in here would leave the
        // controller unread for as long as Game Details stayed open.
        window.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => PerformAll(actions, window));
    }

    private void PerformAll(IReadOnlyList<PadAction> actions, Window window)
    {
        if (TargetWindow() != window) return; // another window came to the front in between
        int reading = ++_readings;
        foreach (var action in actions)
        {
            try
            {
                Perform(action, window);
            }
            catch (Exception ex)
            {
                LoggingService.Info("Controller", $"{action} failed: {ex.Message}");
            }
            // A press that opened a modal window (A on a game opens Game Details) returns only once that window
            // closes, and the timer reads the controller inside it meanwhile. What else this old reading held is
            // stale by then, so it is dropped rather than acted on.
            if (_readings != reading) break;
        }
    }

    private int _readings;

    /// <summary>
    /// The window in front when it is one the controller works in: the main window on the Library page, Game Details
    /// and a prompt it raises, or a window the Library opened (Change Category, Collections, Change Match, a prompt).
    /// </summary>
    private Window? TargetWindow()
    {
        if (Application.Current == null) return null;
        var active = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        return active switch
        {
            null or LaunchPopupWindow => null,
            MainWindow => _main.CurrentSection == NavSection.Library ? active : null,
            GameDetailsDialog or ModernDialog { Owner: GameDetailsDialog } => active,
            // Opened from the Library (its windows are owned by the main window, so the page it is on decides).
            _ => _main.CurrentSection == NavSection.Library ? active : null,
        };
    }

    private void Perform(PadAction action, Window window)
    {
        var focused = Keyboard.FocusedElement as DependencyObject;

        // An open menu (the right-click menu on a game, a drop-down) takes the keys as it would from the keyboard.
        if (IsInMenu(focused) || focused is ComboBox { IsDropDownOpen: true })
        {
            Key? key = action switch
            {
                PadAction.Up => Key.Up, PadAction.Down => Key.Down, PadAction.Left => Key.Left, PadAction.Right => Key.Right,
                PadAction.Accept => Key.Enter, PadAction.Back or PadAction.Menu => Key.Escape,
                _ => null,
            };
            if (key is { } k) SendKey(k);
            return;
        }

        // The filter list, a popup over the main window: move through it, A ticks or presses, B closes it.
        if (window is MainWindow && focused != null && PresentationSource.FromDependencyObject(focused)?.RootVisual is { } root && root != window)
        {
            PerformInPopup(action, focused);
            return;
        }

        if (window is MainWindow main)
        {
            var library = main.LibraryPage;
            bool onGame = library.IsGameFocused(focused);
            bool onTop = !onGame && library.IsInTopBar(focused);
            switch (action)
            {
                case PadAction.Up or PadAction.Down or PadAction.Left or PadAction.Right:
                    if (onGame)
                    {
                        SendKey(ArrowFor(action));
                        // Up on the top row goes nowhere in the games: on into the bar above them (filters, sort, views).
                        if (action == PadAction.Up && ReferenceEquals(Keyboard.FocusedElement, focused)) library.FocusTopBar(_lastTop);
                    }
                    else if (onTop) MoveInTopBar(action, (UIElement)focused!, library);
                    else FocusGames(library);
                    break;
                case PadAction.Accept:
                    if (focused is ButtonBase button) Press(button);
                    else if (focused is ComboBox combo) combo.IsDropDownOpen = true;
                    else if (onGame) SendKey(Key.Enter);
                    else if (!onTop) FocusGames(library);
                    break;
                case PadAction.Back:
                    // In the bar above the games, B goes back down to them; in the games it is Esc (clear the selection, then the search).
                    if (onTop) FocusGames(library);
                    else SendKey(Key.Escape);
                    break;
                case PadAction.Play:
                    if (onGame && (focused as FrameworkElement)?.DataContext is GameCardViewModel game && game.LaunchCommand.CanExecute(null))
                        game.LaunchCommand.Execute(null);
                    break;
                case PadAction.Menu:
                    if (onGame) SendKey(Key.Apps);
                    break;
                case PadAction.PreviousTab or PadAction.NextTab:
                    _main.Library.StepCategoryTab(action == PadAction.NextTab ? 1 : -1);
                    // The tab's games are laid out first, then the first gets focus (the card that had it may be gone).
                    // Returns here: the card still focused until then must not be remembered over the reset.
                    _lastGame = null;
                    library.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FocusGames(library));
                    return;
            }
            if (Keyboard.FocusedElement is FrameworkElement { DataContext: GameCardViewModel game2 } now && library.IsGameFocused(now)) _lastGame = game2;
            else if (Keyboard.FocusedElement is DependencyObject top && library.IsInTopBar(top)) _lastTop = Keyboard.FocusedElement;
            return;
        }

        // Game Details, its prompts and the Library's dialogs: move between their buttons and links, press one, or close.
        bool inside = focused != null && IsInside(focused, window);
        switch (action)
        {
            case PadAction.Up or PadAction.Down or PadAction.Left or PadAction.Right:
                var request = new TraversalRequest(DirectionFor(action));
                // A link in text (Data from RAWG) is a ContentElement, not a UIElement: it moves on the same way.
                if (inside && focused is UIElement element) element.MoveFocus(request);
                else if (inside && focused is ContentElement content) content.MoveFocus(request);
                else window.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                break;
            case PadAction.Accept:
                if (focused is ButtonBase button) Press(button);
                else if (inside && focused is Hyperlink) SendKey(Key.Enter); // a link opens on Enter
                else if (!inside) window.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                break;
            case PadAction.Back:
                SendKey(Key.Escape);
                break;
            case PadAction.Menu:
                if (inside) SendKey(Key.Apps);
                break;
        }
    }

    /// <summary>
    /// The D-pad in the bar above the games: to the next control that way. Down past the bar goes back to the games;
    /// any other way out of it (the sidebar, another page) is not taken, and focus stays where it was.
    /// </summary>
    private void MoveInTopBar(PadAction action, UIElement from, LibraryView library)
    {
        from.MoveFocus(new TraversalRequest(DirectionFor(action)));
        var now = Keyboard.FocusedElement as DependencyObject;
        if (library.IsInTopBar(now) || library.IsGameFocused(now)) return;
        if (action == PadAction.Down) FocusGames(library);
        else from.Focus();
    }

    /// <summary>The filter list: the D-pad moves between its controls, A ticks a filter or presses a button, B closes it (Esc).</summary>
    private static void PerformInPopup(PadAction action, DependencyObject focused)
    {
        switch (action)
        {
            case PadAction.Up or PadAction.Down or PadAction.Left or PadAction.Right:
                if (focused is not UIElement element) break;
                // The panel itself holds focus when the list opens (so Esc works); the first press goes to its first control.
                if (!KeyboardNavigation.GetIsTabStop(element)) element.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                else element.MoveFocus(new TraversalRequest(DirectionFor(action)));
                break;
            case PadAction.Accept:
                if (focused is ButtonBase button) Press(button);
                break;
            case PadAction.Back or PadAction.Menu:
                SendKey(Key.Escape);
                break;
        }
    }

    /// <summary>Back to the game focus was last on, if it is still in the view, else the view's first game.</summary>
    private void FocusGames(LibraryView library)
    {
        library.FocusGames(_lastGame);
    }

    private static Key ArrowFor(PadAction a) => a switch { PadAction.Up => Key.Up, PadAction.Down => Key.Down, PadAction.Left => Key.Left, _ => Key.Right };

    private static FocusNavigationDirection DirectionFor(PadAction a) => a switch
    {
        PadAction.Up => FocusNavigationDirection.Up, PadAction.Down => FocusNavigationDirection.Down,
        PadAction.Left => FocusNavigationDirection.Left, _ => FocusNavigationDirection.Right,
    };

    private static bool IsInside(DependencyObject element, Window window) => Window.GetWindow(element) == window && element != window;

    private static bool IsInMenu(DependencyObject? element)
    {
        for (var d = element; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is MenuItem or ContextMenu or ComboBoxItem) return true;
        return false;
    }

    /// <summary>Presses a button, ticks a check box or flips a switch, as a click or Space would.</summary>
    private static void Press(ButtonBase button)
    {
        if (!button.IsEnabled) return;
        var peer = UIElementAutomationPeer.CreatePeerForElement(button);
        if (peer?.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle) toggle.Toggle();
        else if (peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke) invoke.Invoke();
    }

    /// <summary>A key press and release sent to the focused element through WPF's input, as the keyboard would send it.</summary>
    private static void SendKey(Key key)
    {
        var target = Keyboard.FocusedElement as DependencyObject;
        var source = target != null ? PresentationSource.FromDependencyObject(target) : null;
        source ??= Application.Current.Windows.OfType<Window>().Where(w => w.IsActive).Select(PresentationSource.FromVisual).FirstOrDefault();
        if (source == null) return;
        foreach (var routed in new[] { Keyboard.PreviewKeyDownEvent, Keyboard.PreviewKeyUpEvent })
            InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = routed });
    }
}
