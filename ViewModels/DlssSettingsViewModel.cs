using System;
using System.Threading.Tasks;
using System.Windows.Input;
using TrayTrigger.Services;
using TrayTrigger.Views;

namespace TrayTrigger.ViewModels;

/// <summary>
/// The NVIDIA DLSS card in Settings: the two things about DLSS that are not per game. The override
/// itself is switched on in Edit Game; here is the way to take every game's back out at once, and
/// NVIDIA's on-screen indicator, which is one registry value for the whole PC.
/// </summary>
public sealed class DlssSettingsViewModel : ViewModelBase
{
    private readonly SystemTweaksService _tweaks;
    private readonly Func<int> _overrideGameCount;
    private readonly Func<string> _restoreAll;

    private bool _indicatorOn;
    private bool _isBusy;
    private string? _status;

    public DlssSettingsViewModel(SystemTweaksService tweaks, Func<int> overrideGameCount, Func<string> restoreAll)
    {
        _tweaks = tweaks;
        _overrideGameCount = overrideGameCount;
        _restoreAll = restoreAll;
        IsAvailable = SystemTweaksService.HasNvidiaNgx();
        _indicatorOn = IsAvailable && SystemTweaksService.IsDlssIndicatorOn();

        RestoreAllCommand = new RelayCommand(RestoreAll, () => !IsBusy && _overrideGameCount() > 0);
    }

    /// <summary>False on a PC with no NVIDIA driver, where the card is hidden.</summary>
    public bool IsAvailable { get; }

    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    /// <summary>NVIDIA's overlay, for every DLSS game. Setting it does the work, behind a UAC prompt.</summary>
    public bool IndicatorOn
    {
        get => _indicatorOn;
        set
        {
            if (value == _indicatorOn || IsBusy) return;
            _ = SetIndicatorAsync(value);
        }
    }

    public string OverrideCountText => _overrideGameCount() switch
    {
        0 => "Not on for any game",
        1 => "On for 1 game",
        int n => $"On for {n} games"
    };

    public ICommand RestoreAllCommand { get; }

    /// <summary>Why the last change did not work. Null otherwise: the controls say the rest.</summary>
    public string? Status
    {
        get => _status;
        private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(HasStatus)); }
    }
    public bool HasStatus => !string.IsNullOrEmpty(_status);

    /// <summary>The count changes in Edit Game, so the card re-reads when its page is shown.</summary>
    public void Refresh()
    {
        if (IsAvailable && !IsBusy) _indicatorOn = SystemTweaksService.IsDlssIndicatorOn();
        OnPropertyChanged(nameof(IndicatorOn));
        OnPropertyChanged(nameof(OverrideCountText));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task SetIndicatorAsync(bool show)
    {
        IsBusy = true;
        try
        {
            // Off the UI thread: the write is an elevated reg import behind a UAC prompt. The
            // registry is read back rather than trusting the return value, as a slow prompt can
            // report failure for a write that then lands.
            _indicatorOn = await Task.Run(() =>
            {
                _tweaks.SetDlssIndicator(show);
                return SystemTweaksService.IsDlssIndicatorOn();
            }).ConfigureAwait(true);

            Status = _indicatorOn == show
                ? null
                : "The DLSS Indicator was not changed. The administrator prompt was cancelled or the change was rejected.";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IndicatorOn));
        }
    }

    private void RestoreAll()
    {
        if (!ModernDialog.Confirm(null, "Restore All DLSS Overrides",
                "This turns the DLSS Override off for every game and puts NVIDIA's settings back the way they were.",
                "Games will run on their own DLSS files again. You can switch it back on per game in Edit Game.",
                confirmText: "Restore All", cancelText: "Cancel"))
        {
            return;
        }

        string outcome = _restoreAll();
        Status = _overrideGameCount() > 0 ? outcome : null;
        Refresh();
    }
}
