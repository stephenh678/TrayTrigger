using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

public enum ApiKeyCheckState
{
    /// <summary>Nothing to say: no key, or the key was just edited and hasn't been checked.</summary>
    None,
    Checking,
    Valid,
    Rejected,
    Unreachable
}

/// <summary>
/// The line under a SteamGridDB or RAWG key box that says whether the key works. A key is checked
/// on its own shortly after it is pasted or typed (<see cref="ScheduleCheck"/>), and again from
/// the Check button. Shared by Settings and the Welcome dialog, which bind the same instance
/// through <see cref="SettingsViewModel"/>, so a key checked in one shows its result in the other.
/// </summary>
public sealed class ApiKeyCheckViewModel : ViewModelBase
{
    /// <summary>Long enough that typing a key by hand doesn't send a request per character.</summary>
    internal static TimeSpan AutoCheckDelay { get; set; } = TimeSpan.FromMilliseconds(700);

    private readonly string _serviceName;
    private readonly Func<string> _getKey;
    private readonly Func<string, CancellationToken, Task<ApiKeyCheckResult>> _check;
    private CancellationTokenSource? _cts;

    public ApiKeyCheckViewModel(string serviceName, Func<string> getKey, Func<string, CancellationToken, Task<ApiKeyCheckResult>> check)
    {
        _serviceName = serviceName;
        _getKey = getKey;
        _check = check;
        CheckCommand = new AsyncRelayCommand(() => CheckNowAsync());
    }

    /// <summary>Raised when a check finds the key works, with the key that was checked.</summary>
    public event Action<string>? KeyAccepted;

    public ICommand CheckCommand { get; }

    private ApiKeyCheckState _state;
    public ApiKeyCheckState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(Message));
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => State != ApiKeyCheckState.None;

    public string Message => State switch
    {
        ApiKeyCheckState.Checking => $"Checking the key with {_serviceName}...",
        ApiKeyCheckState.Valid => "✓ Key works.",
        ApiKeyCheckState.Rejected => $"✗ {_serviceName} didn't accept this key. Copy it again from the key page; it's one long run of letters and numbers.",
        ApiKeyCheckState.Unreachable => $"Couldn't reach {_serviceName} to check the key. It's saved; press Check again when you're online.",
        _ => string.Empty
    };

    /// <summary>The key was edited: drop the old verdict and check the new key once typing pauses.
    /// A cleared key is simply not checked.</summary>
    public void ScheduleCheck()
    {
        CancelPending();
        State = ApiKeyCheckState.None;
        if (string.IsNullOrWhiteSpace(_getKey()))
            return;

        var cts = _cts = new CancellationTokenSource();
        _ = RunAfterDelayAsync(cts.Token);
    }

    private async Task RunAfterDelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(AutoCheckDelay, ct);
            await RunCheckAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // A newer edit or check took over.
        }
    }

    /// <summary>Checks the key now - the Check button.</summary>
    public async Task CheckNowAsync()
    {
        CancelPending();
        if (string.IsNullOrWhiteSpace(_getKey()))
        {
            State = ApiKeyCheckState.None;
            return;
        }

        var cts = _cts = new CancellationTokenSource();
        try
        {
            await RunCheckAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // A newer edit or check took over, and reports its own result.
        }
    }

    private async Task RunCheckAsync(CancellationToken ct)
    {
        string key = _getKey();
        State = ApiKeyCheckState.Checking;
        var result = await _check(key, ct);
        ct.ThrowIfCancellationRequested();

        State = result switch
        {
            ApiKeyCheckResult.Valid => ApiKeyCheckState.Valid,
            ApiKeyCheckResult.Rejected => ApiKeyCheckState.Rejected,
            _ => ApiKeyCheckState.Unreachable
        };
        if (result == ApiKeyCheckResult.Valid)
            KeyAccepted?.Invoke(key);
    }

    private void CancelPending()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
