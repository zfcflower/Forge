using System.Collections.Concurrent;
using System.Text.Json;
using Forge.Plugin.Sdk;

namespace Forge.Workflows.Tests.Support;

internal sealed record HostCall(string Tool, JsonElement Arguments)
{
    public string ArgumentsText => Arguments.GetRawText();
}

/// <summary>
/// Fake <see cref="IHostFacade"/> that records every call in start order and tracks how many calls overlap.
/// Tools are registered per name; an unregistered tool fails the call loudly.
/// </summary>
internal sealed class RecordingHost : IHostFacade
{
    private readonly ConcurrentDictionary<string, Func<HostCall, CancellationToken, Task<HostToolResult>>> _tools = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<HostCall> _calls = new();
    private readonly object _sync = new();
    private int _inFlight;
    private int _maxInFlight;

    public RecordingHost Respond(string tool, string content, bool isError = false, TimeSpan? delay = null)
    {
        var wait = delay ?? TimeSpan.Zero;
        return On(tool, async (_, ct) =>
        {
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            return new HostToolResult(content, isError);
        });
    }

    public RecordingHost On(string tool, Func<HostCall, CancellationToken, Task<HostToolResult>> handler)
    {
        _tools[tool] = handler;
        return this;
    }

    public IReadOnlyList<HostCall> Calls => _calls.ToArray();

    public IReadOnlyList<string> CalledTools => _calls.Select(static call => call.Tool).ToArray();

    public int CallCount(string tool) => _calls.Count(call => string.Equals(call.Tool, tool, StringComparison.Ordinal));

    public int MaxInFlight
    {
        get
        {
            lock (_sync)
            {
                return _maxInFlight;
            }
        }
    }

    public async Task<HostToolResult> CallToolAsync(string name, JsonElement arguments, CancellationToken ct)
    {
        _calls.Enqueue(new HostCall(name, arguments.Clone()));
        lock (_sync)
        {
            _inFlight++;
            _maxInFlight = Math.Max(_maxInFlight, _inFlight);
        }

        try
        {
            if (!_tools.TryGetValue(name, out var handler))
            {
                throw new InvalidOperationException($"no fake registered for tool '{name}'");
            }

            return await handler(new HostCall(name, arguments.Clone()), ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                _inFlight--;
            }
        }
    }
}

/// <summary>Releases every arriving party once <c>parties</c> have arrived, so tests can force overlap deterministically.</summary>
internal sealed class Rendezvous(int parties, TimeSpan timeout)
{
    private int _arrived;
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task ArriveAsync(CancellationToken ct)
    {
        if (Interlocked.Increment(ref _arrived) >= parties)
        {
            _released.TrySetResult();
        }

        return _released.Task.WaitAsync(timeout, ct);
    }
}
