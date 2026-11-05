using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Ipc;
using Xunit;

namespace Forge.HostClient.Tests;

internal static class TestLimit
{
    public static readonly TimeSpan Span = TimeSpan.FromSeconds(15);

    public static CancellationTokenSource Create() => new(Span);

    public static Task Bounded(this Task task) => task.WaitAsync(Span);

    public static Task<T> Bounded<T>(this Task<T> task) => task.WaitAsync(Span);
}

internal static class Json
{
    public static JsonElement Element(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}

/// <summary>Records events in arrival order and lets a test await a condition over the recorded sequence.</summary>
internal sealed class EventLog<T>
{
    private readonly Lock gate = new();
    private readonly List<T> items = [];
    private TaskCompletionSource changed = NewSignal();

    public void Add(T item)
    {
        TaskCompletionSource previous;
        lock (gate)
        {
            items.Add(item);
            previous = changed;
            changed = NewSignal();
        }

        previous.TrySetResult();
    }

    public IReadOnlyList<T> Snapshot()
    {
        lock (gate)
        {
            return items.ToArray();
        }
    }

    public async Task<IReadOnlyList<T>> UntilAsync(Func<IReadOnlyList<T>, bool> condition)
    {
        using var limit = TestLimit.Create();
        while (true)
        {
            Task signal;
            lock (gate)
            {
                var snapshot = items.ToArray();
                if (condition(snapshot))
                {
                    return snapshot;
                }

                signal = changed.Task;
            }

            await signal.WaitAsync(limit.Token).ConfigureAwait(false);
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>An in-process <see cref="ITool"/> that counts invocations and runs an optional body.</summary>
internal sealed class StubTool(
    string name,
    ToolSideEffect sideEffect = ToolSideEffect.ReadOnly,
    Func<ToolCall, ToolContext, CancellationToken, ValueTask<ToolResult>>? body = null) : ITool
{
    private readonly ConcurrentQueue<(ToolCall Call, ToolContext Context)> invocations = new();

    public string Name { get; } = name;

    public ToolDefinition Definition { get; } = new(name, "Stub tool " + name, "{}");

    public ToolSideEffect SideEffect { get; } = sideEffect;

    public int InvocationCount => invocations.Count;

    public IReadOnlyList<(ToolCall Call, ToolContext Context)> Invocations => invocations.ToArray();

    public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        invocations.Enqueue((call, context));
        return body is null
            ? ValueTask.FromResult(new ToolResult("ran " + Name))
            : body(call, context, ct);
    }
}

internal static class ClientFactory
{
    public static HostConnection Connection(FakeHost host, TimeSpan? startTimeout = null) => new()
    {
        PipeName = host.PipeName,
        StartTimeout = startTimeout ?? TimeSpan.FromSeconds(10),
    };

    public static ExtensionHostClient Create(
        FakeHost host,
        Func<string, ITool?>? resolver = null,
        Func<ApprovalRequest, CancellationToken, ValueTask<ApprovalDecision>>? approval = null,
        ExtensionHostClientOptions? options = null,
        TimeSpan? startTimeout = null) =>
        new(Connection(host, startTimeout), resolver, approval, options);

    /// <summary>Starts <paramref name="client"/> against <paramref name="host"/> and returns the accepted server peer.</summary>
    public static async Task<IpcPeer> StartConnectedAsync(this ExtensionHostClient client, FakeHost host)
    {
        var accepting = host.AcceptAsync();
        using var limit = TestLimit.Create();
        await client.StartAsync(limit.Token).ConfigureAwait(false);
        return await accepting.Bounded().ConfigureAwait(false);
    }
}

internal static class ToolFixtures
{
    /// <summary>Registers plugin "alpha" with one read-only tool "echo" and waits until the client has processed it.</summary>
    public static async Task<ExtensionHostTool> RegisterEchoAsync(ExtensionHostClient client, FakeHost host)
    {
        await host.NotifyClientAsync("tools.register",
            """{"pluginId":"alpha","tools":[{"name":"echo","description":"Echoes","inputSchemaJson":"{}","sideEffect":0}]}""");
        await host.SyncAsync();
        return Assert.IsType<ExtensionHostTool>(Assert.Single(client.Tools));
    }
}

/// <summary>A self-started "host" that speaks no protocol and stays alive until it is killed, so tests can tell a
/// torn-down generation from a leaked one by its process id.</summary>
internal static class SleeperHost
{
    public static string NewDirectory() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "forge-hc-proc-" + Guid.NewGuid().ToString("N")[..10]))
            .FullName;

    public static HostConnection Connection(FakeHost host, string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(directory, "sleeper.cmd");
            File.WriteAllText(script, "@echo off" + Environment.NewLine + "ping -n 60 127.0.0.1 > NUL" + Environment.NewLine);
            return new HostConnection
            {
                PipeName = host.PipeName,
                HostExecutablePath = "cmd.exe",
                RuntimePath = string.Empty,
                HostArguments = ["/c", script],
                StartTimeout = TimeSpan.FromSeconds(10),
            };
        }

        var shell = Path.Combine(directory, "sleeper.sh");
        File.WriteAllText(shell, "#!/bin/sh" + Environment.NewLine + "sleep 60" + Environment.NewLine);
        File.SetUnixFileMode(shell, UnixFileMode.UserExecute | UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return new HostConnection
        {
            PipeName = host.PipeName,
            HostExecutablePath = shell,
            RuntimePath = string.Empty,
            StartTimeout = TimeSpan.FromSeconds(10),
        };
    }

    public static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static async Task WaitForExitAsync(int processId)
    {
        using var limit = TestLimit.Create();
        while (IsRunning(processId))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), limit.Token).ConfigureAwait(false);
        }
    }

    public static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Wire helpers for the <c>terminal.*</c> family: request construction, host-side frame JSON and
/// channel draining. All waits are TCS/poll bounded by <see cref="TestLimit"/>; no sleeps.</summary>
internal static class TerminalFixtures
{
    public static TerminalStartRequest Request(
        string? sessionId = "sess-term",
        string? workingDirectory = null,
        int columns = 80,
        int rows = 24,
        string? shell = null,
        IReadOnlyList<string>? args = null,
        IReadOnlyDictionary<string, string>? environment = null) =>
        new(
            sessionId ?? "sess-term",
            workingDirectory ?? BoundRoots.AbsoluteDir("term"),
            new TerminalSize(columns, rows),
            shell,
            args,
            environment);

    /// <summary>Answers <c>terminal.start</c> with a fixed id; returns the id for the test to address.</summary>
    public static string AnswerStart(FakeHost host, string terminalId, long processId = 4242, string? shell = "sh")
    {
        host.On(TerminalOps.Start, (_, _) => Task.FromResult(Json.Element(StartResultJson(terminalId, processId, shell))));
        return terminalId;
    }

    public static string StartResultJson(string terminalId, long processId = 4242, string? shell = "sh") =>
        "{\"terminalId\":\"" + terminalId + "\",\"processId\":" + processId + ",\"shell\":\"" + shell + "\"}";

    public static string OutputJson(string terminalId, long seq, byte[] data) =>
        "{\"terminalId\":\"" + terminalId + "\",\"seq\":" + seq + ",\"data\":\"" + Convert.ToBase64String(data) + "\"}";

    public static string OutputJson(string terminalId, long seq, string text) =>
        OutputJson(terminalId, seq, System.Text.Encoding.UTF8.GetBytes(text));

    public static string ExitJson(string terminalId, int exitCode) =>
        "{\"terminalId\":\"" + terminalId + "\",\"exitCode\":" + exitCode + "}";

    /// <summary>Starts one terminal against a connected client; the host must already answer start.</summary>
    public static async Task<ITerminalSession> StartTerminalAsync(
        ExtensionHostClient client, TerminalStartRequest request, CancellationToken ct) =>
        await client.StartAsync(request, ct).ConfigureAwait(false);

    /// <summary>Decodes the base64 <c>data</c> of a captured <c>terminal.input</c>/<c>terminal.output</c> frame.</summary>
    public static byte[] DecodeData(IpcMessage message) =>
        Convert.FromBase64String(message.Arguments!.Value.GetProperty("data").GetString()!);

    /// <summary>Reads <see cref="ITerminalSession.Output"/> to normal completion and returns every byte.
    /// Faults with the session's own error when the stream ends abnormally.</summary>
    public static async Task<byte[]> DrainAsync(ITerminalSession session)
    {
        var collected = new List<byte>();
        using var limit = TestLimit.Create();
        var reader = session.Output;
        while (await reader.WaitToReadAsync(limit.Token).ConfigureAwait(false))
        {
            while (reader.TryRead(out var chunk))
            {
                collected.AddRange(chunk.ToArray());
            }
        }

        await reader.Completion.WaitAsync(limit.Token).ConfigureAwait(false);
        return collected.ToArray();
    }

    /// <summary>Sums the <c>bytes</c> of captured <c>terminal.ack</c> notifications.</summary>
    public static long AckTotal(IReadOnlyList<IpcMessage> acks)
    {
        long total = 0;
        foreach (var ack in acks)
        {
            total += ack.Arguments!.Value.GetProperty("bytes").GetInt64();
        }

        return total;
    }
}
