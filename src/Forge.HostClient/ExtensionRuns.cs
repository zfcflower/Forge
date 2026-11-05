using Forge.Abstractions;

namespace Forge.HostClient;

/// <summary>
/// One execution the main program started and is waiting for: a plugin tool (<c>plugin.execute</c>), a plugin
/// command, a prompt contributor, or a script (<c>code.execute</c>). It owns the identity and trusted context a
/// callback of that execution is matched against, plus the cancellation lifetime and callback gate of the run.
/// </summary>
internal sealed class ExtensionRun
{
    private readonly CancellationTokenSource source;
    private readonly Lock state = new();
    private int ended;
    private int leases = 1;
    private bool cancelling;
    private bool disposed;

    internal ExtensionRun(
        string runId,
        string rootRunId,
        long generation,
        string pluginId,
        IReadOnlyList<string> chain,
        ToolContext context,
        int depth,
        bool isCode,
        IToolRegistry? registry,
        int maxConcurrency,
        int maxInnerResultChars,
        CancellationTokenSource lifetime)
    {
        RunId = runId;
        RootRunId = rootRunId;
        Generation = generation;
        PluginId = pluginId;
        Chain = chain;
        Context = context;
        Depth = depth;
        IsCode = isCode;
        Registry = registry;
        Gate = new SemaphoreSlim(Math.Max(1, maxConcurrency), Math.Max(1, maxConcurrency));
        MaxInnerResultChars = Math.Max(1, maxInnerResultChars);
        source = lifetime;
        Token = lifetime.Token;
    }

    internal string RunId { get; }

    /// <summary>Identity of the top-level invocation this run belongs to: equal to <see cref="RunId"/> for a
    /// top-level execution and inherited unchanged from the trusted parent record by every nested one. It is never a
    /// plugin name or the script identity, so two independently started executions of the same plugin never share it.</summary>
    internal string RootRunId { get; }

    /// <summary>Connection generation the run belongs to; a callback from another connection never matches.</summary>
    internal long Generation { get; }

    /// <summary>Identity the callback must claim: the plugin id, or <c>$run_code</c> for a script.</summary>
    internal string PluginId { get; }

    /// <summary>The complete trusted chain, this execution included. Ancestors are what the next nested call sends.</summary>
    internal IReadOnlyList<string> Chain { get; }

    /// <summary>The main-program context the execution was started with (session, working directory, depth).</summary>
    internal ToolContext Context { get; }

    /// <summary>Depth reported to the host for this execution; its callbacks resolve at <see cref="Depth"/> + 1.</summary>
    internal int Depth { get; }

    internal bool IsCode { get; }

    /// <summary>Registry a script's callbacks resolve against; null for plugin executions, which use the
    /// constructor-supplied resolver.</summary>
    internal IToolRegistry? Registry { get; }

    /// <summary>Per-run callback cap; a script may not exceed its <c>MaxConcurrentToolCalls</c>.</summary>
    internal SemaphoreSlim Gate { get; }

    internal int MaxInnerResultChars { get; }

    /// <summary>Cancelled when the run ends, so nested children cannot outlive their parent. Captured once at
    /// construction, so a caller never reads a token source that may already be disposed.</summary>
    internal CancellationToken Token { get; }

    internal bool IsActive => Volatile.Read(ref ended) == 0 && !Token.IsCancellationRequested;

    /// <summary>
    /// Takes one in-flight lease for a callback of this run. While any lease is held the token source and
    /// <see cref="Gate"/> stay usable, so a holder may read the token and link a child to it. False once the run ended.
    /// </summary>
    internal bool TryLease()
    {
        lock (state)
        {
            if (disposed || Volatile.Read(ref ended) != 0)
            {
                return false;
            }

            leases++;
            return true;
        }
    }

    /// <summary>Returns a lease; the last one after <see cref="End"/> releases the token source and the gate.</summary>
    internal void ReleaseLease()
    {
        bool release;
        lock (state)
        {
            if (leases > 0)
            {
                leases--;
            }

            release = leases == 0 && Volatile.Read(ref ended) != 0 && !cancelling;
        }

        if (release)
        {
            DisposeResources();
        }
    }

    /// <summary>Marks the run over: later callbacks are refused and in-flight children are cancelled. Resources are
    /// released by the last lease, or immediately when no callback holds one.</summary>
    internal void End()
    {
        lock (state)
        {
            if (Volatile.Read(ref ended) != 0)
            {
                return;
            }

            Volatile.Write(ref ended, 1);
            cancelling = true;
        }

        try
        {
            source.Cancel();
        }
        finally
        {
            bool release;
            lock (state)
            {
                cancelling = false;
                release = leases == 0 && !disposed;
            }

            if (release)
            {
                DisposeResources();
            }
        }
    }

    private void DisposeResources()
    {
        lock (state)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        source.Dispose();
        Gate.Dispose();
    }
}

/// <summary>A trusted nested-call context, set only by this client while it runs a callback of an active run.
/// It is the sole source of the plugin chain and depth a nested proxy forwards; nothing read from the wire feeds it.</summary>
internal readonly record struct TrustedCall(string RunId, long Generation, IReadOnlyList<string> Chain, int ChildDepth);
