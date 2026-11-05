using Avalonia.Threading;

namespace Forge.App.Approval;

/// <summary>
/// The seam between the approval queue (which belongs to the UI thread) and everything that produces approval work
/// (agent loops, host events, background reads). The app uses <see cref="UiThreadDispatcher"/>; a test can pass an inline
/// implementation so the queue, the handler and the services are exercised without Avalonia at all.
/// </summary>
internal interface IApprovalDispatcher
{
    /// <summary>True when the caller already runs on the UI thread (or when the process has no UI thread at all).</summary>
    bool CheckAccess();

    Task InvokeAsync(Action action);

    Task<T> InvokeAsync<T>(Func<T> func);

    /// <summary>
    /// Queues work without waiting for it. Used on teardown paths where no dispatcher loop may be running any more, so the
    /// caller must not block on the UI thread it is taking down.
    /// </summary>
    void Post(Action action);
}

/// <summary>
/// The default dispatcher: Avalonia's UI thread. A dispatcher that cannot be resolved - a unit test process without
/// Avalonia, or a thread that is not running a loop any more - is treated as "the caller is the UI thread", so a queue
/// operation degrades to an inline call instead of deadlocking on a thread nobody pumps.
/// </summary>
internal sealed class UiThreadDispatcher : IApprovalDispatcher
{
    public static UiThreadDispatcher Instance { get; } = new();

    private UiThreadDispatcher()
    {
    }

    public bool CheckAccess()
    {
        try
        {
            return Dispatcher.UIThread.CheckAccess();
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or PlatformNotSupportedException)
        {
            return true;
        }
    }

    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return CheckAccess() ? Run(action) : Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        return CheckAccess() ? Task.FromResult(func()) : Dispatcher.UIThread.InvokeAsync(func).GetTask();
    }

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (CheckAccess())
        {
            _ = Run(action);
            return;
        }

        try
        {
            Dispatcher.UIThread.Post(action);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or PlatformNotSupportedException)
        {
            _ = Run(action);
        }
    }

    private static Task Run(Action action)
    {
        try
        {
            action();
            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }
}
