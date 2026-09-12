using System;
using System.Threading.Tasks;
using System.Windows.Input;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute != null ? _ => canExecute() : null)
    {
    }

    public event EventHandler? CanExecuteChanged
    {
        add
        {
            if (_canExecute != null)
                CommandManager.RequerySuggested += value;
        }
        remove
        {
            if (_canExecute != null)
                CommandManager.RequerySuggested -= value;
        }
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public void RaiseCanExecuteChanged()
    {
        CommandManager.InvalidateRequerySuggested();
    }
}

/// <summary>
/// An ICommand for async handlers. Plain `new RelayCommand(async () => await X())` compiles
/// to `async void`, which lets any exception escape straight to the process-wide unhandled
/// exception handler instead of being observable/catchable at the call site; this awaits the
/// handler inside a try/catch and logs instead. See L-07.
/// </summary>
public class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Predicate<object?>? _canExecute;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute != null ? _ => canExecute() : null;
    }

    public event EventHandler? CanExecuteChanged
    {
        add
        {
            if (_canExecute != null)
                CommandManager.RequerySuggested += value;
        }
        remove
        {
            if (_canExecute != null)
                CommandManager.RequerySuggested -= value;
        }
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public async void Execute(object? parameter)
    {
        try
        {
            await _execute();
        }
        catch (Exception ex)
        {
            LoggingService.Error("AsyncRelayCommand", $"Unhandled exception in async command: {ex.Message}", ex);
        }
        finally
        {
            // CanExecuteChanged rides on CommandManager.RequerySuggested, which fires on user
            // input - a key up, a mouse up, a focus change - never on "an await finished". A
            // command that reports CanExecute false while it runs therefore leaves its button
            // greyed out after the work is done, until some unrelated input happens to poke the
            // CommandManager. The user's first click on the button is that poke and does nothing
            // else, so the action appears to need two clicks. Requery once here instead.
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public void RaiseCanExecuteChanged()
    {
        CommandManager.InvalidateRequerySuggested();
    }
}
