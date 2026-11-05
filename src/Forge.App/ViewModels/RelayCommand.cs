using System.Windows.Input;

namespace Forge.App.ViewModels;

/// <summary>A synchronous <see cref="ICommand"/> over a delegate with an explicit can-execute predicate.</summary>
internal sealed class RelayCommand : ICommand
{
    private readonly Action execute;
    private readonly Func<bool>? canExecute;

    public RelayCommand(Action onExecute, Func<bool>? canExecute = null)
    {
        execute = onExecute ?? throw new ArgumentNullException(nameof(onExecute));
        this.canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            execute();
        }
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// An asynchronous <see cref="ICommand"/>: the button is disabled while the body runs, and an exception is reported
/// through <c>onError</c> instead of escaping into the dispatcher as an unhandled <c>async void</c> fault.
/// </summary>
internal sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<CancellationToken, Task> execute;
    private readonly Func<bool>? canExecute;
    private readonly Action<Exception>? onError;
    private bool running;

    public AsyncRelayCommand(
        Func<CancellationToken, Task> onExecute,
        Func<bool>? canExecute = null,
        Action<Exception>? onError = null)
    {
        execute = onExecute ?? throw new ArgumentNullException(nameof(onExecute));
        this.canExecute = canExecute;
        this.onError = onError;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !running && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        running = true;
        NotifyCanExecuteChanged();
        try
        {
            await execute(CancellationToken.None).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            onError?.Invoke(exception);
        }
        finally
        {
            running = false;
            NotifyCanExecuteChanged();
        }
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
