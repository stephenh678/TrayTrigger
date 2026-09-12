using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// The tweak row's Optimize/Revert button must stay enabled while its own click is being handled.
///
/// It used to disable itself for the duration, which cost two things at once. WPF moves keyboard
/// focus off an element the moment it is disabled and scrolls whatever it moves to into view, so
/// the tweak list jumped to a different row on every toggle. And CanExecuteChanged rides on
/// CommandManager.RequerySuggested, which fires on user input rather than on "an await finished",
/// so the button stayed greyed after the work was done until a stray click woke the CommandManager
/// up - which is why the next toggle appeared to need two clicks.
/// </summary>
public class SystemTweakToggleTests
{
    private static SystemTweakViewModel Row(bool canToggle = true, bool available = true) =>
        new(new SystemTweakItem
        {
            Id = "mouse_accel",
            Name = "Disable Mouse Acceleration (Enhanced Pointer Precision)",
            CanToggle = canToggle,
            IsAvailable = available
        },
        new SystemTweaksService(() => new AppSettings(), () => { }),
        _ => { });

    [Fact]
    public void BusyRow_KeepsItsButtonEnabled()
    {
        var row = Row();
        Assert.True(row.CanExecuteToggle);
        Assert.True(row.ToggleCommand.CanExecute(null));

        row.IsBusy = true;

        Assert.True(row.CanExecuteToggle);
        Assert.True(row.ToggleCommand.CanExecute(null));
    }

    /// <summary>The label is what tells the user it is working, now that the button does not grey out.</summary>
    [Fact]
    public void BusyRow_SaysSoInItsLabel()
    {
        var row = Row();
        string idle = row.ActionButtonText;

        row.IsBusy = true;
        Assert.Equal("Working...", row.ActionButtonText);

        row.IsBusy = false;
        Assert.Equal(idle, row.ActionButtonText);
    }

    [Fact]
    public void IsBusy_RaisesTheLabelChangeNotification()
    {
        var row = Row();
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.IsBusy = true;

        Assert.Contains(nameof(SystemTweakViewModel.ActionButtonText), changed);
    }

    /// <summary>A row that genuinely cannot be toggled is still disabled - that part was never the bug.</summary>
    [Fact]
    public void UnavailableOrInformationalRows_StayDisabled()
    {
        Assert.False(Row(canToggle: false).CanExecuteToggle);
        Assert.False(Row(available: false).CanExecuteToggle);
    }
}
