namespace Forge.ExtensionHost;

/// <summary>
/// The host's shutdown fence. Stopping is made irrevocable exactly once, and every path that could still create a worker
/// - a plugin execution taking the plugin gate, an approval or refresh applying a load, a crashed worker recovering, a
/// script call taking a pool slot - rechecks it under the gate or lock that serializes that path. A path that saw
/// "running" has already registered its worker where the shutdown snapshot will find it, so no worker started after the
/// fence can escape the shutdown drain.
/// <para>
/// The token is cancelled the moment stopping begins, so a spawn or a load that is already in flight aborts instead of
/// waiting out its connect or load deadline; the process it created is disposed by the code that started it.
/// </para>
/// </summary>
internal sealed class HostStopSignal
{
    private readonly CancellationTokenSource source = new();

    private int stopping;

    /// <summary>Cancelled once stopping begins; linked into in-flight spawns and loads.</summary>
    public CancellationToken Token => source.Token;

    /// <summary>True from the moment stopping was declared; never goes back to false.</summary>
    public bool IsStopping => Volatile.Read(ref stopping) != 0;

    /// <summary>
    /// Declares stopping and cancels <see cref="Token"/>. Idempotent, and callers serialize it with the state it guards
    /// (for example the host's registry lock) so a path that checks <see cref="IsStopping"/> under that lock either sees
    /// the fence or finished registering before it.
    /// </summary>
    public void Begin()
    {
        if (Interlocked.Exchange(ref stopping, 1) != 0)
        {
            return;
        }

        source.Cancel();
    }

    /// <summary>Refuses work that would start a worker once stopping has begun.</summary>
    public void ThrowIfStopping(string what)
    {
        if (IsStopping)
        {
            throw new HostOperationException(
                "host_stopping",
                $"The extension host is stopping; {what} was not started.");
        }
    }
}
