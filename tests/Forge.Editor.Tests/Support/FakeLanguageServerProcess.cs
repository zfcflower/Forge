using System.Text.Json;
using Forge.Editor.Lsp;

namespace Forge.Editor.Tests.Support;

/// <summary>
/// A <see cref="LanguageServerProcess"/> whose "child process" is an in-process <see cref="FakeLanguageServer"/>: it
/// answers the handshake, records every message the client sends and can be crashed or held mid-write.
/// </summary>
internal sealed class FakeLanguageServerProcess : LanguageServerProcess
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly List<string> _received = [];
    private readonly SemaphoreSlim _arrived = new(0);
    private readonly SemaphoreSlim _writes = new(1, 1);
    private TaskCompletionSource _writeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private FakeLanguageServer? _server;
    private CancellationTokenSource? _lifetime;
    private Task? _serve;
    private bool _running;

    public FakeLanguageServerProcess(RestartPolicy? policy = null)
        : base(new LanguageServerLaunch("fake-language-server"), policy ?? new RestartPolicy())
    {
    }

    public override event EventHandler<LanguageServerExitedEventArgs>? Exited;

    public override bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    public IReadOnlyList<string> Received
    {
        get
        {
            lock (_gate)
            {
                return [.. _received];
            }
        }
    }

    public override ProcessStreams Start()
    {
        FakeLanguageServer server;
        CancellationTokenSource lifetime;
        lock (_gate)
        {
            if (_running)
            {
                throw new InvalidOperationException("The fake language server is already running.");
            }

            server = new FakeLanguageServer();
            lifetime = new CancellationTokenSource();
            _server = server;
            _lifetime = lifetime;
            _running = true;
            _serve = Task.Run(() => ServeAsync(server, lifetime.Token));
        }

        return new ProcessStreams(new GatedStream(server.Streams.Input, _writes, SignalWriteStarted), server.Streams.Output);
    }

    public override async ValueTask StopAsync(CancellationToken ct = default)
    {
        var (server, lifetime, serve) = Detach();
        if (server is null)
        {
            return;
        }

        lifetime!.Cancel();
        try
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }

        if (serve is not null)
        {
            try
            {
                await serve.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        lifetime.Dispose();
        Exited?.Invoke(this, new LanguageServerExitedEventArgs(0, true));
    }

    public override async Task<ProcessStreams> RestartAsync(CancellationToken ct = default)
    {
        await StopAsync(ct).ConfigureAwait(false);
        await Task.Delay(RestartPolicy.InitialDelay, ct).ConfigureAwait(false);
        return Start();
    }

    public override async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _writes.Dispose();
        _arrived.Dispose();
    }

    /// <summary>Simulates a crash: a frame the client cannot parse closes its connection, the pipes stay open.</summary>
    public async Task CrashAsync()
    {
        FakeLanguageServer? server;
        lock (_gate)
        {
            server = _server;
        }

        if (server is not null)
        {
            await server.SendRawAsync(FakeLanguageServer.Frame("{")).ConfigureAwait(false);
        }
    }

    /// <summary>Holds every client write until <see cref="ReleaseWrites"/>, so a sync batch in flight is observable.</summary>
    public void HoldWrites()
    {
        Interlocked.Exchange(ref _writeStarted, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        _writes.Wait(Timeout);
    }

    public void ReleaseWrites() => _writes.Release();

    public async Task WaitForWriteAsync()
    {
        using var timeout = new CancellationTokenSource(Timeout);
        await Volatile.Read(ref _writeStarted).Task.WaitAsync(timeout.Token).ConfigureAwait(false);
    }

    public async Task<JsonElement> WaitForMethodAsync(string method, int occurrence = 1)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (true)
        {
            var matches = Received.Where(message => MethodOf(message) == method).ToArray();
            if (matches.Length >= occurrence)
            {
                return JsonDocument.Parse(matches[occurrence - 1]).RootElement.Clone();
            }

            await _arrived.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
    }

    public int CountMethod(string method) => Received.Count(message => MethodOf(message) == method);

    private void SignalWriteStarted() => Volatile.Read(ref _writeStarted).TrySetResult();

    private static string? MethodOf(string message)
    {
        using var document = JsonDocument.Parse(message);
        return document.RootElement.TryGetProperty("method", out var method) ? method.GetString() : null;
    }

    private (FakeLanguageServer? Server, CancellationTokenSource? Lifetime, Task? Serve) Detach()
    {
        lock (_gate)
        {
            var detached = (_server, _lifetime, _serve);
            _server = null;
            _lifetime = null;
            _serve = null;
            _running = false;
            return detached;
        }
    }

    private async Task ServeAsync(FakeLanguageServer server, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            JsonDocument message;
            try
            {
                message = await server.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
            {
                return;
            }

            try
            {
                var root = message.RootElement;
                lock (_gate)
                {
                    _received.Add(root.GetRawText());
                }

                _arrived.Release();
                if (!root.TryGetProperty("method", out var method) || !root.TryGetProperty("id", out var id))
                {
                    continue;
                }

                var result = method.GetString() == "initialize"
                    ? """{"capabilities":{"textDocumentSync":2}}"""
                    : "null";
                await server.RespondAsync(id, result).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            finally
            {
                message.Dispose();
            }
        }
    }
}
