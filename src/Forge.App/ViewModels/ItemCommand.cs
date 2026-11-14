using System.Windows.Input;

namespace Forge.App.ViewModels;

/// <summary>An <see cref="ICommand"/> that passes its parameter to the delegate; no reflection, AOT-clean.</summary>
internal sealed class ItemCommand<T> : ICommand where T : class
{
    private readonly Action<T?> execute;

    public ItemCommand(Action<T?> execute)
    {
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    public event EventHandler? CanExecuteChanged
    {
        add
        {
        }

        remove
        {
        }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute(parameter as T);
}
