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
    // The window read last, to see Game Details come to the front.
    private Window? _lastWindow;

    /// <summary>
    /// A button the controller passes over: one that opens Windows' own file or folder picker (Browse..., Choose
    /// image, New script), which a controller can't work or close. It still clicks, and Tab still reaches it.
    /// </summary>
    public static readonly DependencyProperty SkipProperty = DependencyProperty.RegisterAttached(
        "Skip", typeof(bool), typeof(GamepadNavigator), new PropertyMetadata(false));

    public static bool GetSkip(DependencyObject d) => (bool)d.GetValue(SkipProperty);
    public static void SetSkip(DependencyObject d, bool value) => d.SetValue(SkipProperty, value);

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
        // Every reading is counted, a press acted on or not: a press that opened a modal window drops the rest of its
        // reading once any reading has been taken inside that window (see PerformAll), however it was closed.
        int reading = ++_readings;
        var window = TargetWindow();
        var state = window != null ? _read() : null;
        if (window == null || state == null)
        {
            _lastWindow = window;
            _reading = false;
            _timer.Interval = IdleInterval;
            return;
        }
        _timer.Interval = ActiveInterval;
        // Game Details just came to the front with nothing in it focused (opened with A, or from the keyboard):
        // Play, so A again plays. Not when focus is already inside it (it came back to the front, or a prompt closed).
        if (window != _lastWindow)
        {
            _lastWindow = window;
            if (window is GameDetailsDialog details && !(Keyboard.FocusedElement is DependencyObject f && IsInside(f, details)))
            {
                MarkKeyboardAsLastInput();
                details.FocusPlay();
            }
        }
        long now = _clock.ElapsedMilliseconds;
        if (!_reading)
        {
            _input.Reset(state.Value, now);
            _reading = true;
            return;
        }
        var actions = _input.Update(state.Value, now);
        if (actions.Count == 0) return;
        bool repeated = _input.LastDirectionRepeated;
        // Acted on just after this reading, not inside it: a DispatcherTimer doesn't fire again until its Tick has
        // returned, and A on a game opens Game Details modally, so a press acted on in here would leave the
        // controller unread for as long as Game Details stayed open.
        window.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => PerformAll(actions, window, reading, repeated));
    }

    private void PerformAll(IReadOnlyList<PadAction> actions, Window window, int reading, bool repeated)
    {
        if (TargetWindow() != window) return; // another window came to the front in between
        foreach (var action in actions)
        {
            try
            {
                Perform(action, window, repeated && action is PadAction.Up or PadAction.Down or PadAction.Left or PadAction.Right);
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

    /// <param name="repeat">A direction repeated because it is held, not pressed again: it doesn't leave the games
    /// for the bar above them, nor move between that bar's rows.</param>
    private void Perform(PadAction action, Window window, bool repeat = false)
    {
        MarkKeyboardAsLastInput();
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
                        // Up on the top row goes nowhere in the games: a new press goes on into the bar above them
                        // (filters, sort, views). Up held to scroll to the top stops there.
                        if (action == PadAction.Up && !repeat && ReferenceEquals(Keyboard.FocusedElement, focused)) library.FocusTopBar(_lastTop);
                    }
                    else if (onTop)
                    {
                        // Held, Left and Right run along the bar; Up and Down move a row only when pressed again.
                        if (!(repeat && action is PadAction.Up or PadAction.Down)) MoveInTopBar(action, (UIElement)focused!, library);
                    }
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
                SkipPast(request.FocusNavigationDirection, inside ? focused : null);
                break;
            case PadAction.Accept:
                if (focused is ButtonBase { } skipped && GetSkip(skipped)) break; // reached with Tab or the mouse: not pressed
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

    /// <summary>
    /// After a move: on past any button marked <see cref="SkipProperty"/>, the same way. If there is nothing past it,
    /// focus goes back to where it was (or, with nowhere to go back to, stays put).
    /// </summary>
    private static void SkipPast(FocusNavigationDirection direction, DependencyObject? from)
    {
        for (int i = 0; i < 10 && Keyboard.FocusedElement is UIElement now && GetSkip(now); i++)
        {
            if (now.MoveFocus(new TraversalRequest(direction)) && !ReferenceEquals(Keyboard.FocusedElement, now)) continue;
            if (from is IInputElement back) Keyboard.Focus(back);
            return;
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

    // InputManager.MostRecentInputDevice has an internal setter only; null if a later WPF drops it.
    private static readonly System.Reflection.MethodInfo? SetMostRecentInputDevice =
        typeof(InputManager).GetProperty(nameof(InputManager.MostRecentInputDevice))?.GetSetMethod(nonPublic: true);
    private static bool _ringUnavailableLogged;

    /// <summary>
    /// WPF draws the focus ring only while the keyboard was the last input, and keys sent through its input manager
    /// don't count as that, so focus moved from the controller showed no ring and you couldn't see where you were.
    /// The keyboard is made the last input before each press, as the controller stands in for it; the mouse, used
    /// again, hides the ring as it always does.
    /// </summary>
    private static void MarkKeyboardAsLastInput()
    {
        if (InputManager.Current.MostRecentInputDevice is KeyboardDevice) return;
        if (SetMostRecentInputDevice == null)
        {
            if (!_ringUnavailableLogged) LoggingService.Info("Controller", "Can't show the focus ring for the controller in this WPF version");
            _ringUnavailableLogged = true;
            return;
        }
        SetMostRecentInputDevice.Invoke(InputManager.Current, new object[] { Keyboard.PrimaryDevice });
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
