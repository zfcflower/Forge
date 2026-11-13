using System.Net;

namespace Forge.Core.Net;

/// <summary>
/// The handler <see cref="NetworkStack.CreateHandler"/> returns. It forwards every request to a current inner
/// <see cref="SocketsHttpHandler"/> and replaces that inner when the stack's network settings change, so the connection
/// pool of the old route (proxy / DNS) is not reused by new requests.
/// <list type="bullet">
/// <item>Request path: lock-free. A volatile read of the current generation, an increment of its in-flight count (CAS,
/// refused once the generation is retired, in which case the current one is re-read), then
/// <see cref="HttpMessageInvoker.SendAsync"/>; the count drops when the call returns (headers received).</item>
/// <item>Swap: under a lock (rare). The new generation is published first, then the old one is retired. A retired
/// generation is disposed exactly once, by whoever sees its in-flight count at zero: the retirer when nothing is in
/// flight, otherwise the last request to finish. No timer and no grace period are involved, so nothing leaks and a request
/// never meets a disposed inner. A response body still streaming after headers is not counted: disposing a
/// <see cref="SocketsHttpHandler"/> closes its idle pooled connections and lets connections in use finish, then be closed.</item>
/// <item>Dispose: disposes the current generation, unsubscribes from the stack (the event must not keep the wrapper alive),
/// and later requests throw <see cref="ObjectDisposedException"/>.</item>
/// </list>
/// </summary>
public sealed class SwappableNetworkHandler : HttpMessageHandler
{
    private readonly NetworkStack owner;
    private readonly Func<SocketsHttpHandler> factory;
    private readonly EventHandler subscription;
    private readonly object swapLock = new();
    private Generation? current;
    private int seenVersion;
    private int swaps;
    private int retired;
    private int disposedInners;

    internal SwappableNetworkHandler(NetworkStack owner, Func<SocketsHttpHandler> factory)
    {
        this.owner = owner;
        this.factory = factory;
        seenVersion = owner.RouteVersion;
        current = new Generation(this, factory());
        subscription = OnSettingsChanged;
        owner.SettingsChanged += subscription;
        // A change between reading the version and subscribing would otherwise be missed.
        OnSettingsChanged(owner, EventArgs.Empty);
    }

    /// <summary>How many times the inner handler has been replaced.</summary>
    public int SwapCount => Volatile.Read(ref swaps);

    /// <summary>How many inner handlers have been disposed so far (retired ones, plus the last one after the wrapper is disposed).</summary>
    public int DisposedInnerCount => Volatile.Read(ref disposedInners);

    /// <summary>Replaced inner handlers (whether or not already disposed).</summary>
    public int RetiredInnerCount => Volatile.Read(ref retired);

    /// <summary>The current inner handler's identity, for tests.</summary>
    public object? CurrentInner => Volatile.Read(ref current)?.Handler;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var generation = Acquire();
        try
        {
            return await generation.Invoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            generation.Release();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            owner.SettingsChanged -= subscription;
            Generation? last;
            lock (swapLock)
            {
                last = Interlocked.Exchange(ref current, null);
            }

            last?.Retire();
        }

        base.Dispose(disposing);
    }

    private Generation Acquire()
    {
        var spin = new SpinWait();
        while (true)
        {
            var generation = Volatile.Read(ref current) ?? throw new ObjectDisposedException(nameof(SwappableNetworkHandler));
            if (generation.TryAcquire())
            {
                return generation;
            }

            // Retired between the read and the increment: a newer generation (or null after dispose) is published.
            spin.SpinOnce();
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        Generation? old;
        lock (swapLock)
        {
            var version = owner.RouteVersion;
            if (current is null || version == seenVersion)
            {
                return;
            }

            seenVersion = version;
            old = current;
            Volatile.Write(ref current, new Generation(this, factory()));
            Interlocked.Increment(ref swaps);
            Interlocked.Increment(ref retired);
        }

        old.Retire();
    }

    private sealed class Generation
    {
        // Low bits: requests in flight. The sign bit: retired (no new request may start on it).
        private const int RetiredFlag = int.MinValue;
        private readonly SwappableNetworkHandler parent;
        private int state;

        public Generation(SwappableNetworkHandler parent, SocketsHttpHandler handler)
        {
            this.parent = parent;
            Handler = handler;
            Invoker = new HttpMessageInvoker(handler, disposeHandler: true);
        }

        public SocketsHttpHandler Handler { get; }

        public HttpMessageInvoker Invoker { get; }

        public bool TryAcquire()
        {
            var seen = Volatile.Read(ref state);
            while (seen >= 0)
            {
                var previous = Interlocked.CompareExchange(ref state, seen + 1, seen);
                if (previous == seen)
                {
                    return true;
                }

                seen = previous;
            }

            return false;
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref state) == RetiredFlag)
            {
                Close();
            }
        }

        public void Retire()
        {
            if ((Interlocked.Or(ref state, RetiredFlag) & int.MaxValue) == 0)
            {
                Close();
            }
        }

        private void Close()
        {
            Invoker.Dispose();
            Interlocked.Increment(ref parent.disposedInners);
        }
    }
}
