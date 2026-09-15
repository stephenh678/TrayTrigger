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

/// <summary>
/// What the launch popup needs to know about the thing being launched. Games supply one through
/// <see cref="ForGame"/>; anything else TrayTrigger launches (the planned Tools section) builds its
/// own, so the popup never depends on <see cref="GameEntry"/>.
/// </summary>
/// <param name="Id">The entry's id: what the launcher's session events and <see cref="ILaunchPopup.LaunchDispatched"/> carry.</param>
/// <param name="Name">Shown as the popup's title.</param>
/// <param name="PerformanceProfile">Named in the "Launching" detail when it isn't Off.</param>
/// <param name="FallbackIconUri">Pack URI of the picture shown when the entry has no icon of its own (a launcher logo), or null.</param>
public sealed record LaunchTarget(
    string Id,
    string Name,
    PerformanceProfileMode PerformanceProfile = PerformanceProfileMode.Off,
    string? FallbackIconUri = null)
{
    public static LaunchTarget ForGame(GameEntry game) =>
        new(game.Id, game.Name, game.PerformanceProfile, LauncherLogos.PackUriFor(game));
}

public sealed record LaunchPopupContent(
    LaunchPopupKind Kind,
    string Name,
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
    bool TryBeginLaunch(LaunchTarget target);

    /// <summary>The launch call returned successfully.</summary>
    void LaunchDispatched(string id);

    /// <summary>Shows a launch failure in the popup instead of a dialog. False means show the dialog.
    /// <paramref name="action"/> runs after the window is brought up.</summary>
    bool TryShowFailure(LaunchTarget target, string message, string? actionText, Action? action);

    /// <summary>Shows a launcher notice in the popup instead of a tray notification. False means use the tray.</summary>
    bool TryShowNotice(LaunchTarget target, string message);
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
    private readonly Func<LaunchTarget, ImageSource?> _iconFor;
    private readonly ILaunchPopupView _view;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private readonly Func<DateTime> _utcNow;

    private LaunchTarget? _target;
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
        Func<LaunchTarget, ImageSource?> iconFor,
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

    public bool TryBeginLaunch(LaunchTarget target)
    {
        if (!_isEnabled()) return false;
        bool appInFront = _isAppInFront();
        if (appInFront && !_showWhileAppInFront()) return false;

        Start(target, LaunchPopupKind.Launching, message: null, actionText: null, action: null);
        _shownAtUtc = _utcNow();

        int token = _token;
        _waitTimer = _schedule(WaitingAfter, () =>
        {
            if (token == _token && _kind == LaunchPopupKind.Launching) Render(LaunchPopupKind.Waiting);
        });
        _capTimer = _schedule(MaxWait, () =>
        {
            if (token != _token || !IsProgress) return;
            LoggingService.Verbose("LaunchPopup", $"'{target.Name}' still hadn't started after {MaxWait.TotalSeconds:0}s; closed the launch popup.");
            Hide();
        });

        LoggingService.Verbose("LaunchPopup", $"Showing the launch popup for '{target.Name}' ({(appInFront ? "launched from the TrayTrigger window" : "TrayTrigger window not in front")}).");
        return true;
    }

    /// <summary>The launcher registered a session; its platform label names what the game waits on.</summary>
    public void OnSessionStarted(string id, string platformLabel)
    {
        if (!IsProgressFor(id)) return;
        _platform = platformLabel;
        Render(_kind);
    }

    /// <summary>The game's own process is running.</summary>
    public void OnGameStarted(string id)
    {
        if (IsProgressFor(id)) CloseSoon();
    }

    /// <summary>The session ended, with or without the game having run.</summary>
    public void OnSessionEnded(string id)
    {
        if (IsProgressFor(id)) CloseSoon();
    }

    public void LaunchDispatched(string id)
    {
        // Nothing to wait for: no session (an untracked game that was already running got focus),
        // or a tracked session whose game is already running, so SessionGameStarted won't come again.
        if (IsProgressFor(id) && !_isWaitingForGame(id)) CloseSoon();
    }

    public bool TryShowFailure(LaunchTarget target, string message, string? actionText, Action? action)
    {
        if (!_isEnabled() || _isAppInFront())
        {
            // With the window in front, a dialog on it is the clearer place for an error. A progress
            // popup for this launch (the every-launch option) makes way for it.
            if (IsProgressFor(target.Id)) Hide();
            return false;
        }
        Start(target, LaunchPopupKind.Failed, message, actionText, action);
        LoggingService.Verbose("LaunchPopup", $"Showing a launch failure for '{target.Name}' in the popup.");
        return true;
    }

    public bool TryShowNotice(LaunchTarget target, string message)
    {
        // With the window in front the tray notification is used, as before the popup existed.
        if (!_isEnabled() || _isAppInFront()) return false;
        Start(target, LaunchPopupKind.Notice, message, actionText: null, action: null);
        return true;
    }

    public void Dispose()
    {
        CancelTimers();
        _view.ActionClicked -= OnActionClicked;
        _view.CloseClicked -= Hide;
        _view.Dispose();
    }

    private bool IsProgress => _target != null && _kind is LaunchPopupKind.Launching or LaunchPopupKind.Waiting;

    private bool IsProgressFor(string id) => IsProgress && _target!.Id == id;

    private void Start(LaunchTarget target, LaunchPopupKind kind, string? message, string? actionText, Action? action)
    {
        CancelTimers();
        _token++;
        _target = target;
        _platform = null;
        _message = message;
        _actionText = actionText;
        _action = action;
        Render(kind);
    }

    private void Render(LaunchPopupKind kind)
    {
        if (_target == null) return;
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
            LaunchPopupKind.Launching => LaunchingDetail(_target, _platform),
            LaunchPopupKind.Waiting => WaitingDetail(_platform),
            _ => _message,
        };

        _view.Show(new LaunchPopupContent(kind, _target.Name, status, detail, _iconFor(_target), _actionText));
    }

    internal static string? LaunchingDetail(LaunchTarget target, string? platform)
    {
        var parts = new List<string>(2);
        if (IsLauncher(platform)) parts.Add($"Starting through {platform}");
        if (target.PerformanceProfile != PerformanceProfileMode.Off) parts.Add($"{target.PerformanceProfile} profile");
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
        _target = null;
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
