using System.Collections.Concurrent;
using System.Text.Json;
using Forge.Ipc;
using Xunit;

namespace Forge.HostClient.Tests;

/// <summary>An Extension Host stand-in: a named-pipe server that speaks the ExtensionProtocol through <see cref="IpcPeer"/>.
/// Requests from the client are recorded and answered by per-operation handlers (default: an empty object).</summary>
internal sealed class FakeHost : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Func<IpcMessage, CancellationToken, Task<JsonElement>>> handlers =
        new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<IpcMessage> requests = new();
    private readonly ConcurrentQueue<IpcMessage> notifications = new();
    private readonly EventLog<IpcMessage> requestLog = new();
    private readonly EventLog<IpcMessage> notificationLog = new();
    private readonly List<(string Operation, string ArgsJson)> onAccept = [];
    private readonly EventLog<int> accepts = new();
    private readonly CancellationTokenSource lifetime = new();
    private IpcPeer? peer;
    private NamedPipeServerTransport? transport;

    public FakeHost()
    {
        PipeName = "forge-hc-" + Guid.NewGuid().ToString("N");
    }

    public string PipeName { get; }

    /// <summary>Client requests received so far for <paramref name="operation"/>, in arrival order.</summary>
    public IReadOnlyList<IpcMessage> Requests(string operation) =>
        requests.Where(message => string.Equals(message.Operation, operation, StringComparison.Ordinal)).ToArray();

    /// <summary>Client notifications received so far for <paramref name="operation"/>.</summary>
    public IReadOnlyList<IpcMessage> Notifications(string operation) =>
        notifications.Where(message => string.Equals(message.Operation, operation, StringComparison.Ordinal)).ToArray();

    /// <summary>Returns once the host has received <paramref name="count"/> requests for
    /// <paramref name="operation"/> (client to host); lets a test await input/ack frames without sleeps.</summary>
    public async Task<IReadOnlyList<IpcMessage>> WaitForRequestsAsync(string operation, int count)
    {
        var seen = await requestLog.UntilAsync(
            events => events.Count(message => string.Equals(message.Operation, operation, StringComparison.Ordinal)) >= count)
            .ConfigureAwait(false);
        return seen.Where(message => string.Equals(message.Operation, operation, StringComparison.Ordinal)).ToArray();
    }

    /// <summary>Returns once the host has received <paramref name="count"/> notifications for
    /// <paramref name="operation"/> (client to host); lets a test await input/ack frames without sleeps.</summary>
    public async Task<IReadOnlyList<IpcMessage>> WaitForNotificationsAsync(string operation, int count)
    {
        var seen = await notificationLog.UntilAsync(
            events => events.Count(message => string.Equals(message.Operation, operation, StringComparison.Ordinal)) >= count)
            .ConfigureAwait(false);
        return seen.Where(message => string.Equals(message.Operation, operation, StringComparison.Ordinal)).ToArray();
    }

    /// <summary>Replaces the answer to <paramref name="operation"/>. A handler that throws <see cref="FakeHostError"/>
    /// answers with an IPC error envelope; one that never completes simulates a host that does not answer.</summary>
    public void On(string operation, Func<IpcMessage, CancellationToken, Task<JsonElement>> handler) =>
        handlers[operation] = handler;

    /// <summary>Queues a notification the host emits the moment it accepts the next connection, before the test
    /// resumes. It lands in the connect-to-Running window, which is what recovery-generation tests need.</summary>
    public void NotifyOnAccept(string operation, string argsJson)
    {
        lock (onAccept)
        {
            onAccept.Add((operation, argsJson));
        }
    }

    /// <summary>Completes once the host has accepted <paramref name="count"/> connections and sent every queued
    /// on-accept notification of the latest one; lets a test hold the client until those frames are in flight.</summary>
    public Task WhenAcceptedAsync(int count) => accepts.UntilAsync(seen => seen.Count >= count);

    /// <summary>Creates the pipe and waits for the client. Call before the client starts so it can connect.</summary>
    public async Task<IpcPeer> AcceptAsync()
    {
        var server = await IpcPipe.CreateServer(PipeName, lifetime.Token).ConfigureAwait(false);
        var created = new IpcPeer(server, new IpcPeerOptions { DefaultTimeout = TestLimit.Span });
        created.RequestReceived += (message, ct) => OnRequestAsync(created, message, ct);
        created.NotificationReceived += message =>
        {
            notifications.Enqueue(message);
            notificationLog.Add(message);
        };
        await created.StartAsync(CancellationToken.None).ConfigureAwait(false);
        transport = server;
        peer = created;
        List<(string Operation, string ArgsJson)> pending;
        lock (onAccept)
        {
            pending = [.. onAccept];
        }

        foreach (var (operation, argsJson) in pending)
        {
            await NotifyClientAsync(operation, argsJson).ConfigureAwait(false);
        }

        accepts.Add(1);
        return created;
    }

    /// <summary>Sends a request from the host to the client and returns its response.</summary>
    public Task<IpcMessage> CallClientAsync(string operation, string? argsJson, CancellationToken ct) =>
        Connected.SendRequestAsync(operation, argsJson is null ? null : Json.Element(argsJson), ct);

    /// <summary>Sends a notification from the host to the client.</summary>
    public Task NotifyClientAsync(string operation, string argsJson) =>
        Connected.SendNotificationAsync(operation, Json.Element(argsJson), CancellationToken.None);

    /// <summary>Returns once the client has processed every frame the host sent before this call: the client
    /// answers unknown operations with an error, and inbound frames are handled in order.</summary>
    public async Task SyncAsync()
    {
        using var limit = TestLimit.Create();
        var error = await Assert.ThrowsAsync<IpcRemoteException>(() => CallClientAsync("sync", null, limit.Token));
        Assert.Equal("unsupported", error.Code);
    }

    /// <summary>Sends one notification and returns once the client has processed it (see <see cref="SyncAsync"/>),
    /// which is how a test publishes a registration without sleeping.</summary>
    public async Task NotifyAndSyncAsync(string operation, string argsJson)
    {
        await NotifyClientAsync(operation, argsJson).ConfigureAwait(false);
        await SyncAsync().ConfigureAwait(false);
    }

    /// <summary>Closes the host side of the pipe without a stop request, as a crashed host would.</summary>
    public async Task DropConnectionAsync()
    {
        await Connected.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (peer is not null)
        {
            await peer.DisposeAsync().ConfigureAwait(false);
        }
        else if (transport is not null)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }

        lifetime.Dispose();
    }

    private IpcPeer Connected => peer ?? throw new InvalidOperationException("The fake host has not accepted a client.");

    private async Task OnRequestAsync(IpcPeer active, IpcMessage message, CancellationToken ct)
    {
        requests.Enqueue(message);
        requestLog.Add(message);
        var handler = handlers.TryGetValue(message.Operation, out var registered) ? registered : DefaultHandler;
        try
        {
            var result = await handler(message, ct).ConfigureAwait(false);
            await active.RespondAsync(message.Id, result, CancellationToken.None).ConfigureAwait(false);
        }
        catch (FakeHostError error)
        {
            await active.RespondErrorAsync(message.Id, new IpcError(error.Code, error.Message), CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    /// <summary>A handler for an operation the host never answers; it ends only when the connection closes.</summary>
    public static async Task<JsonElement> NeverAnswer(IpcMessage message, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        return IpcJsonTypes.EmptyObject;
    }

    private static Task<JsonElement> DefaultHandler(IpcMessage message, CancellationToken ct) =>
        Task.FromResult(IpcJsonTypes.EmptyObject);
}

/// <summary>Raised by a fake host handler to answer with an IPC error envelope carrying <see cref="Code"/>.</summary>
internal sealed class FakeHostError(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
