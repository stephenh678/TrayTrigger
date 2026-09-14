using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Threading;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

public enum LaunchPopupKind
{
    /// <summary>Dispatched; the game hasn't started yet.</summary>
    Launching,
    /// <summary>Still not started after <see cref="LaunchPopupCoordinator.WaitingAfter"/>: usually a launcher signing in or updating.</summary>
    Waiting,
    /// <summary>The launch failed. Stays until closed.</summary>
    Failed,
    /// <summary>A launch that needs the user (<see cref="ProcessLauncherService.LaunchNotice"/>). Stays until closed.</summary>
    Notice,
}

public sealed record LaunchPopupContent(
    LaunchPopupKind Kind,
    string GameName,
    string Status,
    string? Detail,
    ImageSource? Icon,
    string? ActionText)
{
    /// <summary>Has a close button (and maybe an action), so it can't be click-through.</summary>
    public bool IsInteractive => Kind is LaunchPopupKind.Failed or LaunchPopupKind.Notice;
    public bool ShowsProgress => Kind is LaunchPopupKind.Launching or LaunchPopupKind.Waiting;
}

/// <summary>The popup window, behind an interface so the coordinator's rules are testable without WPF windows.</summary>
public interface ILaunchPopupView : IDisposable
{
    event Action? ActionClicked;
    event Action? CloseClicked;
    void Show(LaunchPopupContent content);
    void Hide();
}

/// <summary>What the library calls around a launch. Every method runs on the UI thread.</summary>
public interface ILaunchPopup
{
    /// <summary>Shows the popup for a launch about to be dispatched. False when the setting is off, or
    /// TrayTrigger is in front and the popup isn't wanted there (App's showWhileAppInFront: the
    /// every-launch option, or the window about to hide on launch); the in-window launch toast is used then.</summary>
    bool TryBeginLaunch(GameEntry game);

    /// <summary>The launch call returned successfully.</summary>
    void LaunchDispatched(string gameId);

    /// <summary>Shows a launch failure in the popup instead of a dialog. False means show the dialog.
    /// <paramref name="action"/> runs after the window is brought up.</summary>
    bool TryShowFailure(GameEntry game, string message, string? actionText, Action? action);

    /// <summary>Shows a launcher notice in the popup instead of a tray notification. False means use the tray.</summary>
    bool TryShowNotice(GameEntry game, string message);
}

/// <summary>
/// Decides when the launch popup shows, what it says, and when it goes away. A game started from
/// a hotkey or the tray menu while the window was hidden used to give no sign anything happened:
/// the "Launching..." toast lives inside the window, and a failure's dialog had no window to sit
/// on. The popup covers the time between the key press and the game appearing, which for a
/// launcher still signing in can be a minute or more.
/// </summary>
public sealed class LaunchPopupCoordinator : ILaunchPopup, IDisposable
{
    /// <summary>A game that starts instantly would otherwise flash the popup for a frame.</summary>
    internal static readonly TimeSpan MinVisible = TimeSpan.FromSeconds(1.5);

    /// <summary>After this long without the game starting, the popup says what it's waiting for.</summary>
    internal static readonly TimeSpan WaitingAfter = TimeSpan.FromSeconds(6);

    /// <summary>Longest a progress popup stays up. Covers Battle.net's 90-second resend window, after
    /// which a <see cref="ProcessLauncherService.LaunchNotice"/> takes over if the game still hasn't started.</summary>
    internal static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(100);

    private readonly Func<bool> _isEnabled;
    private readonly Func<bool> _showWhileAppInFront;
    private readonly Func<bool> _isAppInFront;
    private readonly Func<string, bool> _isWaitingForGame;
    private readonly Action _showMainWindow;
    private readonly Func<GameEntry, ImageSource?> _iconFor;
    private readonly ILaunchPopupView _view;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private readonly Func<DateTime> _utcNow;

    private GameEntry? _game;
    private LaunchPopupKind _kind;
    private string? _platform;
    private string? _message;
    private string? _actionText;
    private Action? _action;
    private DateTime _shownAtUtc;
    private int _token;
    private IDisposable? _waitTimer;
    private IDisposable? _capTimer;
    private IDisposable? _closeTimer;

    public LaunchPopupCoordinator(
        Func<bool> isEnabled,
        Func<bool> showWhileAppInFront,
        Func<bool> isAppInFront,
        Func<string, bool> isWaitingForGame,
        Action showMainWindow,
        Func<GameEntry, ImageSource?> iconFor,
        ILaunchPopupView view,
        Func<TimeSpan, Action, IDisposable>? schedule = null,
        Func<DateTime>? utcNow = null)
    {
        _isEnabled = isEnabled;
        _showWhileAppInFront = showWhileAppInFront;
        _isAppInFront = isAppInFront;
        _isWaitingForGame = isWaitingForGame;
        _showMainWindow = showMainWindow;
        _iconFor = iconFor;
        _view = view;
        _schedule = schedule ?? ScheduleOnDispatcher;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);

        _view.ActionClicked += OnActionClicked;
        _view.CloseClicked += Hide;
    }

    public bool TryBeginLaunch(GameEntry game)
    {
        if (!_isEnabled()) return false;
        bool appInFront = _isAppInFront();
        if (appInFront && !_showWhileAppInFront()) return false;

        Start(game, LaunchPopupKind.Launching, message: null, actionText: null, action: null);
        _shownAtUtc = _utcNow();

        int token = _token;
        _waitTimer = _schedule(WaitingAfter, () =>
        {
            if (token == _token && _kind == LaunchPopupKind.Launching) Render(LaunchPopupKind.Waiting);
        });
        _capTimer = _schedule(MaxWait, () =>
        {
            if (token != _token || !IsProgress) return;
            LoggingService.Verbose("LaunchPopup", $"'{game.Name}' still hadn't started after {MaxWait.TotalSeconds:0}s; closed the launch popup.");
            Hide();
        });

        LoggingService.Verbose("LaunchPopup", $"Showing the launch popup for '{game.Name}' ({(appInFront ? "launched from the TrayTrigger window" : "TrayTrigger window not in front")}).");
        return true;
    }

    /// <summary>The launcher registered a session; its platform label names what the game waits on.</summary>
    public void OnSessionStarted(string gameId, string platformLabel)
    {
        if (!IsProgressFor(gameId)) return;
        _platform = platformLabel;
        Render(_kind);
    }

    /// <summary>The game's own process is running.</summary>
    public void OnGameStarted(string gameId)
    {
        if (IsProgressFor(gameId)) CloseSoon();
    }

    /// <summary>The session ended, with or without the game having run.</summary>
    public void OnSessionEnded(string gameId)
    {
        if (IsProgressFor(gameId)) CloseSoon();
    }

    public void LaunchDispatched(string gameId)
    {
        // Nothing to wait for: no session (an untracked game that was already running got focus),
        // or a tracked session whose game is already running, so SessionGameStarted won't come again.
        if (IsProgressFor(gameId) && !_isWaitingForGame(gameId)) CloseSoon();
    }

    public bool TryShowFailure(GameEntry game, string message, string? actionText, Action? action)
    {
        if (!_isEnabled() || _isAppInFront())
        {
            // With the window in front, a dialog on it is the clearer place for an error. A progress
            // popup for this launch (the every-launch option) makes way for it.
            if (IsProgressFor(game.Id)) Hide();
            return false;
        }
        Start(game, LaunchPopupKind.Failed, message, actionText, action);
        LoggingService.Verbose("LaunchPopup", $"Showing a launch failure for '{game.Name}' in the popup.");
        return true;
    }

    public bool TryShowNotice(GameEntry game, string message)
    {
        // With the window in front the tray notification is used, as before the popup existed.
        if (!_isEnabled() || _isAppInFront()) return false;
        Start(game, LaunchPopupKind.Notice, message, actionText: null, action: null);
        return true;
    }

    public void Dispose()
    {
        CancelTimers();
        _view.ActionClicked -= OnActionClicked;
        _view.CloseClicked -= Hide;
        _view.Dispose();
    }

    private bool IsProgress => _game != null && _kind is LaunchPopupKind.Launching or LaunchPopupKind.Waiting;

    private bool IsProgressFor(string gameId) => IsProgress && _game!.Id == gameId;

    private void Start(GameEntry game, LaunchPopupKind kind, string? message, string? actionText, Action? action)
    {
        CancelTimers();
        _token++;
        _game = game;
        _platform = null;
        _message = message;
        _actionText = actionText;
        _action = action;
        Render(kind);
    }

    private void Render(LaunchPopupKind kind)
    {
        if (_game == null) return;
        _kind = kind;

        string status = kind switch
        {
            LaunchPopupKind.Launching => "Launching",
            LaunchPopupKind.Waiting => IsLauncher(_platform) ? $"Waiting for {_platform}" : "Still starting",
            LaunchPopupKind.Failed => "Couldn't launch",
            _ => "Needs your attention",
        };

        string? detail = kind switch
        {
            LaunchPopupKind.Launching => LaunchingDetail(_game, _platform),
            LaunchPopupKind.Waiting => WaitingDetail(_platform),
            _ => _message,
        };

        _view.Show(new LaunchPopupContent(kind, _game.Name, status, detail, _iconFor(_game), _actionText));
    }

    internal static string? LaunchingDetail(GameEntry game, string? platform)
    {
        var parts = new List<string>(2);
        if (IsLauncher(platform)) parts.Add($"Starting through {platform}");
        if (game.PerformanceProfile != PerformanceProfileMode.Off) parts.Add($"{game.PerformanceProfile} profile");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    internal static string WaitingDetail(string? platform) => platform switch
    {
        "Battle.net" => "Battle.net may be signing in or updating.",
        _ when IsLauncher(platform) => $"{platform} can take a moment to start the game.",
        _ => "Some games take a while to open.",
    };

    /// <summary>A client the game waits on, as opposed to an exe TrayTrigger starts itself.</summary>
    private static bool IsLauncher(string? platform) =>
        !string.IsNullOrEmpty(platform) && platform is not ("Local" or "link");

    private void CloseSoon()
    {
        if (_closeTimer != null) return;
        TimeSpan remaining = MinVisible - (_utcNow() - _shownAtUtc);
        if (remaining <= TimeSpan.Zero)
        {
            Hide();
            return;
        }

        int token = _token;
        _closeTimer = _schedule(remaining, () =>
        {
            if (token == _token) Hide();
        });
    }

    private void Hide()
    {
        CancelTimers();
        _token++;
        _game = null;
        _action = null;
        _view.Hide();
    }

    private void OnActionClicked()
    {
        var action = _action;
        Hide();
        try
        {
            _showMainWindow();
            action?.Invoke();
        }
        catch (Exception ex)
        {
            LoggingService.Warn("LaunchPopup", $"Launch popup action failed: {ex.Message}");
        }
    }

    private void CancelTimers()
    {
        _waitTimer?.Dispose();
        _capTimer?.Dispose();
        _closeTimer?.Dispose();
        _waitTimer = _capTimer = _closeTimer = null;
    }

    private static IDisposable ScheduleOnDispatcher(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
        return new TimerHandle(timer);
    }

    private sealed class TimerHandle(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}
