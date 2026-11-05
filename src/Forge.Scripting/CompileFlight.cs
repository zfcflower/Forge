namespace Forge.Scripting;

/// <summary>
/// One in-flight compile shared by every caller that arrives for the same key while the compile runs. Waiters are
/// reference-counted: one waiter cancelling never cancels the shared compile; only the last waiter leaving does, and
/// once a flight is abandoned no new waiter may attach to it.
/// </summary>
internal sealed class CompileFlight<T>
{
    private readonly Lock gate = new();
    private readonly CancellationTokenSource cancelSource = new();
    private readonly TaskCompletionSource<T> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int waiters;
    private bool abandoned;

    public CompileFlight()
    {
        // The creator is the first waiter; it starts at one so the shared compile lives until it detaches too.
        waiters = 1;
    }

    public Task<T> Task => completion.Task;

    public CancellationToken Token => cancelSource.Token;

    /// <summary>Registers one more waiter. Returns <c>false</c> once the flight is abandoned, so the caller starts a
    /// fresh compile instead of joining one that is already being cancelled.</summary>
    public bool TryAttach()
    {
        lock (gate)
        {
            if (abandoned)
            {
                return false;
            }

            waiters++;
            return true;
        }
    }

    /// <summary>Removes one waiter. When the last waiter leaves, the flight is abandoned and the shared compile token
    /// is cancelled.</summary>
    public void Detach()
    {
        var cancel = false;
        lock (gate)
        {
            if (waiters == 0)
            {
                return;
            }

            waiters--;
            if (waiters == 0 && !abandoned)
            {
                abandoned = true;
                cancel = true;
            }
        }

        if (cancel)
        {
            cancelSource.Cancel();
        }
    }

    public void Complete(T result) => completion.TrySetResult(result);

    public void Fail(Exception exception) => completion.TrySetException(exception);

    public void Cancel() => completion.TrySetCanceled(cancelSource.Token);
}
