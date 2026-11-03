namespace Forge.ExtensionHost;

/// <summary>
/// Per-plugin state that outlives any single worker process. The gate serializes approval changes
/// (approve/revoke/refresh) against the approval-check-through-dispatch section of an execution, so a revoke can never
/// overlap the moment a call decides to run and start a worker. Executions register a lease before they leave that
/// section, which is what lets a revoke wait until nothing is in flight any more.
/// </summary>
internal sealed class PluginState
{
    /// <summary>Abnormal worker events a plugin may accumulate before it is refused until <c>plugins.refresh</c>.</summary>
    public const int FailureBudget = 4;

    private readonly SemaphoreSlim gate = new(1, 1);

    private readonly Lock sync = new();

    private int inFlight;

    private int failures;

    private TaskCompletionSource? idle;

    /// <summary>Abnormal worker events charged since the last refresh or completed tool call.</summary>
    public int Failures => Volatile.Read(ref failures);

    public bool IsExhausted => Volatile.Read(ref failures) >= FailureBudget;

    public Task EnterAsync(CancellationToken ct) => gate.WaitAsync(ct);

    public void Exit() => gate.Release();

    /// <summary>Charges one abnormal worker event and returns the new total.</summary>
    public int ChargeFailure() => Interlocked.Increment(ref failures);

    /// <summary>A tool call that actually completed clears the budget; a successful start or load does not.</summary>
    public void RecordSuccess() => Interlocked.Exchange(ref failures, 0);

    public void ResetFailures() => Interlocked.Exchange(ref failures, 0);

    /// <summary>Marks one dispatched execution; dispose the result when the call ends.</summary>
    public Lease BeginExecution()
    {
        lock (sync)
        {
            inFlight++;
        }

        return new Lease(this);
    }

    /// <summary>Waits until no execution registered by <see cref="BeginExecution"/> is running, or the bound elapses.</summary>
    public async Task WaitForIdleAsync(TimeSpan bound, CancellationToken ct)
    {
        Task pending;
        lock (sync)
        {
            if (inFlight == 0)
            {
                return;
            }

            idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = idle.Task;
        }

        await Task.WhenAny(pending, Task.Delay(bound, ct)).ConfigureAwait(false);
    }

    private void EndExecution()
    {
        TaskCompletionSource? complete = null;
        lock (sync)
        {
            inFlight--;
            if (inFlight == 0 && idle is { } waiting)
            {
                complete = waiting;
                idle = null;
            }
        }

        complete?.TrySetResult();
    }

    /// <summary>One dispatched execution; disposing it takes the execution out of the in-flight set.</summary>
    internal readonly struct Lease(PluginState state) : IDisposable
    {
        public void Dispose() => state.EndExecution();
    }
}
