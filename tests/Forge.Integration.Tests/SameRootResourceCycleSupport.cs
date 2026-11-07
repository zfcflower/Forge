using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Integration.Tests;

/// <summary>One completed inner call of <see cref="SameRootResourceCycleRelay"/>.</summary>
internal sealed record SameRootResourceCycleOutcome(
    string From,
    string Target,
    string Text,
    bool IsError,
    string Content,
    TimeSpan At);

/// <summary>Main-program relay for the same-root cycle tests; modes: fork, cross, hop, sleepThenEcho, sleep.</summary>
internal sealed class SameRootResourceCycleRelay(E2eFixture fx) : ITool
{
    public const string Name = "cycleRelay";

    private static readonly TimeSpan BarrierLimit = TestBudgets.Condition;

    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly TaskCompletionSource crossBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<SameRootResourceCycleOutcome> outcomes = new();
    private int crossArrivals;
    private TimeSpan crossReleasedAt;

    public ToolDefinition Definition { get; } = new(Name, "Same-root wait-cycle test relay", "{}");

    public ToolSideEffect SideEffect => ToolSideEffect.Execute;

    /// <summary>Inner calls completed so far, ordered by completion time.</summary>
    public IReadOnlyList<SameRootResourceCycleOutcome> Outcomes =>
        outcomes.OrderBy(outcome => outcome.At).ToArray();

    /// <summary>When the cross barrier opened, on the relay's clock; meaningful once both cross arrivals happened.</summary>
    public TimeSpan CrossReleasedAt => crossReleasedAt;

    /// <summary>Wakes a barrier waiter that a failed test left behind.</summary>
    public void ReleaseBarrier() => crossBarrier.TrySetCanceled();

    public async ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(call.ArgumentsJson);
        var args = document.RootElement;
        var mode = args.GetProperty("mode").GetString();
        switch (mode)
        {
            case "fork":
            {
                var plugin = args.GetProperty("plugin").GetString()!;
                var other = args.GetProperty("other").GetString()!;
                var inner = args.GetProperty("inner").GetString()!;
                var text = args.GetProperty("text").GetString()!;
                var outer = await CallAsync(
                    plugin,
                    PluginSources.CallHostTool,
                    JsonSerializer.Serialize(new { target = Name, args = new { mode = inner, from = plugin, plugin = other, text } }),
                    ct).ConfigureAwait(false);
                return new ToolResult("fork:" + Render(outer), IsError: false);
            }

            case "cross":
            case "hop":
            {
                var from = args.GetProperty("from").GetString()!;
                var target = args.GetProperty("plugin").GetString()!;
                var text = args.GetProperty("text").GetString()!;
                if (mode == "cross")
                {
                    if (Interlocked.Increment(ref crossArrivals) == 2)
                    {
                        crossReleasedAt = clock.Elapsed;
                        crossBarrier.TrySetResult();
                    }

                    await crossBarrier.Task.WaitAsync(BarrierLimit, ct).ConfigureAwait(false);
                }

                var echoed = await CallAsync(target, PluginSources.EchoTool, JsonSerializer.Serialize(new { text }), ct)
                    .ConfigureAwait(false);
                outcomes.Enqueue(new SameRootResourceCycleOutcome(from, target, text, echoed.IsError, echoed.Content, clock.Elapsed));
                return new ToolResult(Render(echoed), IsError: false);
            }

            case "sleepThenEcho":
            {
                var plugin = args.GetProperty("plugin").GetString()!;
                var milliseconds = args.GetProperty("ms").GetInt32();
                var text = args.GetProperty("text").GetString()!;
                var slept = await CallAsync(plugin, PluginSources.SleepTool, JsonSerializer.Serialize(new { ms = milliseconds }), ct)
                    .ConfigureAwait(false);
                if (slept.IsError)
                {
                    return new ToolResult(Render(slept), IsError: false);
                }

                var echoed = await CallAsync(plugin, PluginSources.EchoTool, JsonSerializer.Serialize(new { text }), ct)
                    .ConfigureAwait(false);
                return new ToolResult(Render(echoed), IsError: false);
            }

            case "sleep":
            {
                var plugin = args.GetProperty("plugin").GetString()!;
                var milliseconds = args.GetProperty("ms").GetInt32();
                var marker = args.GetProperty("marker").GetString()!;
                var slept = await CallAsync(
                    plugin,
                    PluginSources.SleepTool,
                    JsonSerializer.Serialize(new { ms = milliseconds, marker }),
                    ct).ConfigureAwait(false);
                return new ToolResult(Render(slept), IsError: false);
            }

            default:
                return new ToolResult($"unknown relay mode '{mode}'", IsError: true);
        }
    }

    /// <summary>Pb's tools are prefixed (<c>q_</c>) so they do not clash with pa's same-named Standard tools; the host
    /// refuses a tool name that another plugin already publishes. Everything else uses the plain names.</summary>
    private static string ToolNameOf(string pluginId, string tool) =>
        string.Equals(pluginId, "pb", StringComparison.Ordinal) ? "q_" + tool : tool;

    private Task<ToolResult> CallAsync(string pluginId, string tool, string argumentsJson, CancellationToken ct)
    {
        var toolName = ToolNameOf(pluginId, tool);
        return fx.ToolOf(pluginId, toolName).InvokeAsync(
            new ToolCall(Guid.NewGuid().ToString("N"), toolName, argumentsJson),
            new ToolContext("cycle-session", fx.Root),
            ct).AsTask();
    }

    /// <summary>The outcome text a caller sees: <c>ok:</c> or <c>error:</c> plus the exact content of the inner result.</summary>
    internal static string Render(ToolResult result) => (result.IsError ? "error:" : "ok:") + result.Content;
}
