using System.Collections.Concurrent;
using System.Text.Json;
using Forge.Plugin.Sdk;

namespace Forge.Scripting.Tests;

/// <summary>Shared scaffolding for the <c>ScriptCode*</c> suite: request factory plus a recording
/// <see cref="IHostFacade"/> (no local tool registry, no approval store).</summary>
internal static class ScriptCodeSupport
{
    public static ExtensionCallContext Context(string runId = "run-1") =>
        new(runId, "session-1", "/work", 0, []);

    public static CodeExecutionRequest Request(
        string code,
        IReadOnlyList<ToolDeclaration>? tools = null,
        CodeExecutionOptions? options = null,
        int timeoutMilliseconds = 10_000,
        string runId = "run-1") =>
        new(code, Context(runId), timeoutMilliseconds, tools ?? [], options ?? new CodeExecutionOptions());

    public static ToolDeclaration Tool(string name, ToolSideEffect sideEffect = ToolSideEffect.ReadOnly) =>
        new(name, name + " tool", "{\"type\":\"object\"}", sideEffect);

    /// <summary>Bounds every run: a hung engine fails the test instead of hanging the runner.</summary>
    public static Task<CodeExecutionResult> ExecuteAsync(
        ScriptCodeRunner runner,
        CodeExecutionRequest request,
        IHostFacade host,
        CancellationToken ct) =>
        runner.ExecuteAsync(request, host, ct).WaitAsync(TimeSpan.FromSeconds(30));
}

/// <summary>Test-only host: records every inner call and answers from a per-test handler.
/// Never resolves tools in-process and never decides approval.</summary>
internal sealed class RecordingHostFacade : IHostFacade
{
    private int calls;
    private int inFlight;
    private int maxConcurrency;

    public ConcurrentQueue<string> Names { get; } = new();

    public ConcurrentQueue<string> ArgumentsJson { get; } = new();

    public int CallCount => Volatile.Read(ref calls);

    public int MaxObservedConcurrency => Volatile.Read(ref maxConcurrency);

    public int InFlight => Volatile.Read(ref inFlight);

    public Func<string, JsonElement, CancellationToken, Task<HostToolResult>>? Handler { get; set; }

    public async Task<HostToolResult> CallToolAsync(string name, JsonElement arguments, CancellationToken ct)
    {
        Interlocked.Increment(ref calls);
        Names.Enqueue(name);
        ArgumentsJson.Enqueue(arguments.GetRawText());
        var current = Interlocked.Increment(ref inFlight);
        try
        {
            int previous;
            do
            {
                previous = Volatile.Read(ref maxConcurrency);
                if (current <= previous)
                {
                    break;
                }
            }
            while (Interlocked.CompareExchange(ref maxConcurrency, current, previous) != previous);

            if (Handler is not null)
            {
                return await Handler(name, arguments, ct).ConfigureAwait(false);
            }

            return new HostToolResult("{}");
        }
        finally
        {
            Interlocked.Decrement(ref inFlight);
        }
    }
}
