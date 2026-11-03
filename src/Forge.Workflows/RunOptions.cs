namespace Forge.Workflows;

/// <summary>Per-run knobs. All values are optional; the defaults are the ones documented in <c>README.md</c>.</summary>
public sealed record RunOptions
{
    /// <summary>Maximum number of tool calls in flight at once.</summary>
    public const int DefaultMaxConcurrency = 8;

    /// <summary>Default options: concurrency 8, no timeout, system clock.</summary>
    public static RunOptions Default { get; } = new();

    /// <summary>Upper bound on concurrently running steps. Must be at least 1.</summary>
    public int MaxConcurrency { get; init; } = DefaultMaxConcurrency;

    /// <summary>Whole-run budget. When it elapses, scheduling stops and the run reports <see cref="WorkflowRunStatus.TimedOut"/>.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Clock and timer source. Inject a fake to control timestamps and timeouts in tests.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// How long to wait for already running steps to observe cancellation before the run returns anyway.
    /// A host tool that ignores its token cannot be stopped from here.
    /// </summary>
    public TimeSpan CancellationDrainTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
