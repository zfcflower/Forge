using System.Security.Cryptography;
using System.Text.Json;
using Forge.Approval;
using Forge.Ipc;
using Forge.Plugin.Sdk;
using Forge.Scripting;

namespace Forge.ExtensionHost;

/// <summary>
/// The resident host. It listens on the main-program pipe, discovers and compiles plugins, gates them on
/// <see cref="ApprovalEngine"/>, starts one worker process per plugin, publishes <c>tools.register</c> and routes
/// execution and plugin-to-main-program tool calls.
/// </summary>
public sealed class ExtensionHost : IAsyncDisposable
{
    private readonly HostOptions options;
    private readonly ApprovalStore approvals;
    private readonly PluginCatalog catalog;
    private readonly Dictionary<string, PluginEntry> plugins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkerSlot> workers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> registrations = new(StringComparer.Ordinal);
    private readonly List<PluginIssue> issues = [];
    private readonly Lock sync = new();
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IIpcPeer? mainPeer;
    private int stopRequested;
    private int disposed;

    public ExtensionHost(HostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
        Directory.CreateDirectory(options.DataDirectory);
        approvals = new ApprovalStore(options.DataDirectory);
        catalog = new PluginCatalog(options.PluginsDirectory);
    }

    /// <summary>Every issue the last discovery reported, newest last.</summary>
    internal IReadOnlyList<PluginIssue> Issues
    {
        get
        {
            lock (sync)
            {
                return [.. issues];
            }
        }
    }

    /// <summary>Runs the host until <c>host.stop</c> or the main-program pipe closes.</summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);

        var transport = new NamedPipeServerTransport(options.PipeName, new IpcPipeOptions { ConnectTimeout = options.ConnectTimeout });
        try
        {
            await using var peer = new IpcPeer(transport, new IpcPeerOptions { DefaultTimeout = options.RequestTimeout });
            mainPeer = peer;
            peer.RequestReceived += OnRequestAsync;
            peer.ProtocolError += error => Console.Error.WriteLine($"forge-extension-host: dropped an inbound frame: {error.Message}");
            peer.Closed += _ => stopped.TrySetResult();
            try
            {
                await peer.StartAsync(linked.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or IpcTimeoutException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
            {
                Console.Error.WriteLine($"forge-extension-host: pipe '{options.PipeName}' could not be opened: {exception.Message}");
                return 1;
            }

            try
            {
                await RefreshAsync(linked.Token).ConfigureAwait(false);
                await stopped.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            await ShutdownWorkersAsync().ConfigureAwait(false);
        }
        finally
        {
            mainPeer = null;
            await transport.DisposeAsync().ConfigureAwait(false);
        }

        return 0;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await lifetime.CancelAsync().ConfigureAwait(false);
        await ShutdownWorkersAsync().ConfigureAwait(false);
    }

    private async Task OnRequestAsync(IpcMessage message, CancellationToken ct)
    {
        if (mainPeer is not { } peer)
        {
            return;
        }

        JsonElement result;
        try
        {
            result = await DispatchAsync(message, ct).ConfigureAwait(false);
        }
        catch (HostOperationException exception)
        {
            await RespondErrorAsync(peer, message.Id, exception.Code, exception.Message).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            await RespondErrorAsync(peer, message.Id, "cancelled", $"'{message.Operation}' was cancelled.").ConfigureAwait(false);
            return;
        }
        catch (Exception exception)
        {
            await RespondErrorAsync(peer, message.Id, "handler_error", exception.Message).ConfigureAwait(false);
            return;
        }

        try
        {
            await peer.RespondAsync(message.Id, result, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or IpcConnectionClosedException)
        {
        }

        if (Interlocked.Exchange(ref stopRequested, 0) != 0)
        {
            stopped.TrySetResult();
        }
    }

    private Task<JsonElement> DispatchAsync(IpcMessage message, CancellationToken ct)
    {
        switch (message.Operation)
        {
            case ExtensionProtocol.PluginExecute:
                return ExecuteAsync(Read<PluginExecuteRequest>(message), ct);
            case ExtensionProtocol.PluginApprove:
                return ApproveAsync(Read<PluginApproveRequest>(message), ct);
            case ExtensionProtocol.PluginRevoke:
                return RevokeAsync(Read<PluginRevokeRequest>(message), ct);
            case ExtensionProtocol.PluginsRefresh:
                return RefreshAsync(ct);
            case ExtensionProtocol.HostStop:
                Interlocked.Exchange(ref stopRequested, 1);
                return Task.FromResult(HostJson.EmptyObject);
            default:
                throw new HostOperationException("unsupported_operation", $"The extension host does not serve '{message.Operation}'.");
        }
    }

    private async Task<JsonElement> ExecuteAsync(PluginExecuteRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PluginId) || string.IsNullOrWhiteSpace(request.Tool))
        {
            throw new HostOperationException("invalid_request", "pluginId and tool are required.");
        }

        var entry = Find(request.PluginId) ??
            throw new HostOperationException("unknown_plugin", $"No plugin '{request.PluginId}' was discovered.");

        var decision = await EvaluateAsync(entry, ct).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            throw new HostOperationException("plugin_not_approved", $"Plugin '{entry.Id}' {entry.Version} must not run: {decision.Reason}.");
        }

        if (entry.AssemblyPath is not { } assemblyPath)
        {
            throw new HostOperationException("plugin_not_loadable", $"Plugin '{entry.Id}' {entry.Version} has no loadable assembly.");
        }

        var slot = GetWorker(entry);
        if (await slot.EnsureLoadedAsync(entry.Version, assemblyPath, ct).ConfigureAwait(false))
        {
            entry.Tools = slot.Tools;
        }

        await RegisterToolsAsync(entry, slot.Tools, ct).ConfigureAwait(false);
        if (!Contains(slot.Tools, request.Tool))
        {
            throw new HostOperationException("unknown_tool", $"Plugin '{entry.Id}' does not declare a tool named '{request.Tool}'.");
        }

        var result = await slot.ExecuteAsync(request, options.PipeName, ct).ConfigureAwait(false);
        return HostJson.ToElement(new PluginExecuteResponse(result.Content, result.IsError));
    }

    private async Task<JsonElement> ApproveAsync(PluginApproveRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PluginId))
        {
            throw new HostOperationException("invalid_request", "pluginId is required.");
        }

        var entry = Find(request.PluginId) ??
            throw new HostOperationException("unknown_plugin", $"No plugin '{request.PluginId}' was discovered.");

        var version = string.IsNullOrWhiteSpace(request.Version) ? entry.Version : request.Version;
        if (!string.Equals(version, entry.Version, StringComparison.Ordinal))
        {
            throw new HostOperationException(
                "version_mismatch",
                $"Plugin '{entry.Id}' is installed at version '{entry.Version}', not '{version}'.");
        }

        if (!entry.HasContent)
        {
            throw new HostOperationException(
                "plugin_not_loadable",
                $"Plugin '{entry.Id}' {entry.Version} has no compiled content to approve.");
        }

        var sourceHash = string.IsNullOrWhiteSpace(request.SourceHash) ? entry.SourceHash : request.SourceHash;
        var dependencyHash = string.IsNullOrWhiteSpace(request.DependencyHash) ? entry.DependencyHash : request.DependencyHash;
        var sdkVersion = string.IsNullOrWhiteSpace(request.SdkVersion) ? entry.SdkVersion : request.SdkVersion;
        approvals.SetApproved(entry.Id, version, sourceHash, dependencyHash, sdkVersion);
        await ApplyAsync(entry, ct).ConfigureAwait(false);

        if (!entry.Decision.IsAllowed)
        {
            throw new HostOperationException(
                "approval_mismatch",
                $"The approval does not cover the current content of '{entry.Id}': {entry.Decision.Reason}.");
        }

        return HostJson.EmptyObject;
    }

    private async Task<JsonElement> RevokeAsync(PluginRevokeRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PluginId))
        {
            throw new HostOperationException("invalid_request", "pluginId is required.");
        }

        var entry = Find(request.PluginId) ??
            throw new HostOperationException("unknown_plugin", $"No plugin '{request.PluginId}' was discovered.");

        var version = string.IsNullOrWhiteSpace(request.Version) ? entry.Version : request.Version;
        if (!string.Equals(version, entry.Version, StringComparison.Ordinal))
        {
            throw new HostOperationException(
                "version_mismatch",
                $"Plugin '{entry.Id}' is installed at version '{entry.Version}', not '{version}'.");
        }

        approvals.Revoke(entry.Id, version);
        await ApplyAsync(entry, ct).ConfigureAwait(false);
        return HostJson.EmptyObject;
    }

    private async Task<JsonElement> RefreshAsync(CancellationToken ct)
    {
        await refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var scan = await catalog.DiscoverAsync(ct).ConfigureAwait(false);
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in scan.Plugins)
            {
                present.Add(entry.Id);
                SyncRecord(entry);
                lock (sync)
                {
                    plugins[entry.Id] = entry;
                }

                await ApplyAsync(entry, ct).ConfigureAwait(false);
            }

            foreach (var id in KnownPluginIds())
            {
                if (present.Contains(id))
                {
                    continue;
                }

                lock (sync)
                {
                    plugins.Remove(id);
                }

                await UnregisterToolsAsync(id, ct).ConfigureAwait(false);
                await StopWorkerAsync(id).ConfigureAwait(false);
            }

            lock (sync)
            {
                issues.Clear();
                var reported = new HashSet<PluginIssue>();
                foreach (var issue in scan.Issues)
                {
                    if (reported.Add(issue))
                    {
                        issues.Add(issue);
                    }
                }

                foreach (var entry in scan.Plugins)
                {
                    foreach (var issue in entry.Issues)
                    {
                        if (reported.Add(issue))
                        {
                            issues.Add(issue);
                        }
                    }
                }
            }

            foreach (var issue in Issues)
            {
                Console.Error.WriteLine("forge-extension-host: " + issue);
            }

            return HostJson.EmptyObject;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    /// <summary>Re-evaluates approval for one plugin and brings its worker and tool registration in line with the result.</summary>
    private async Task ApplyAsync(PluginEntry entry, CancellationToken ct)
    {
        var decision = await EvaluateAsync(entry, ct).ConfigureAwait(false);
        if (!decision.IsAllowed || entry.AssemblyPath is null)
        {
            await UnregisterToolsAsync(entry.Id, ct).ConfigureAwait(false);
            await StopWorkerAsync(entry.Id).ConfigureAwait(false);
            return;
        }

        try
        {
            var slot = GetWorker(entry);
            if (await slot.EnsureLoadedAsync(entry.Version, entry.AssemblyPath, ct).ConfigureAwait(false))
            {
                entry.Tools = slot.Tools;
            }

            await RegisterToolsAsync(entry, slot.Tools, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HostOperationException or IpcTimeoutException or IpcConnectionClosedException or IOException)
        {
            entry.Issues.Add(new PluginIssue(entry.Id, PluginIssueKind.LoadFailed, exception.Message, entry.Directory));
            Console.Error.WriteLine($"forge-extension-host: {entry.Id} ({PluginIssueKind.LoadFailed}): {exception.Message}");
            await UnregisterToolsAsync(entry.Id, ct).ConfigureAwait(false);
            await StopWorkerAsync(entry.Id).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds the plugin's approval candidate from the hashes recorded at discovery, after confirming the content on
    /// disk still hashes to the same value; a plugin whose source or image changed on disk needs a refresh.
    /// </summary>
    private async Task<ApprovalDecision> EvaluateAsync(PluginEntry entry, CancellationToken ct)
    {
        ApprovalDecision decision;
        if (!entry.HasContent)
        {
            decision = ApprovalDecision.Block($"plugin {entry.Id} {entry.Version} has no loadable content");
        }
        else if (!await ContentUnchangedAsync(entry, ct).ConfigureAwait(false))
        {
            decision = ApprovalDecision.Require($"the content of {entry.Id} {entry.Version} changed on disk; refresh before running it");
        }
        else
        {
            var candidate = new PluginRecord
            {
                PluginId = entry.Id,
                Version = entry.Version,
                SourceHash = entry.SourceHash,
                DependencyHash = entry.DependencyHash,
                SdkVersion = entry.SdkVersion,
            };
            decision = ApprovalEngine.Evaluate(candidate, approvals);
        }

        entry.Decision = decision;
        return decision;
    }

    private static async Task<bool> ContentUnchangedAsync(PluginEntry entry, CancellationToken ct)
    {
        if (entry.ContentPath is not { } path)
        {
            return true;
        }

        try
        {
            string hash;
            if (entry.ContentIsBinary)
            {
                var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            }
            else
            {
                hash = Hashing.SourceHash(PluginCatalog.StripBom(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)));
            }

            return string.Equals(hash, entry.SourceHash, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Keeps the persisted record pointing at the content that is on disk now. A plugin the store does not know yet gets
    /// a <see cref="PluginStatus.Draft"/> record, which is how the main program learns the hashes it has to approve; an
    /// existing record keeps its status and <see cref="PluginRecord.ApprovedHash"/>, so a changed source or dependency
    /// hash makes the approval stop matching instead of silently re-approving it.
    /// </summary>
    private void SyncRecord(PluginEntry entry)
    {
        if (!entry.HasContent)
        {
            return;
        }

        var record = approvals.Get(entry.Id, entry.Version);
        if (record is null)
        {
            approvals.Upsert(new PluginRecord
            {
                PluginId = entry.Id,
                Version = entry.Version,
                SourceHash = entry.SourceHash,
                DependencyHash = entry.DependencyHash,
                SdkVersion = entry.SdkVersion,
                Status = PluginStatus.Draft,
            });
            return;
        }

        if (record.Status is PluginStatus.Disabled or PluginStatus.Uninstalled ||
            (string.Equals(record.SourceHash, entry.SourceHash, StringComparison.Ordinal) &&
             string.Equals(record.DependencyHash, entry.DependencyHash, StringComparison.Ordinal) &&
             string.Equals(record.SdkVersion, entry.SdkVersion, StringComparison.Ordinal)))
        {
            return;
        }

        approvals.Upsert(record with
        {
            SourceHash = entry.SourceHash,
            DependencyHash = entry.DependencyHash,
            SdkVersion = entry.SdkVersion,
        });
    }

    private async Task RegisterToolsAsync(PluginEntry entry, IReadOnlyList<ToolDeclaration> tools, CancellationToken ct)
    {
        var payload = HostJson.ToElement(tools).GetRawText();
        lock (sync)
        {
            if (registrations.TryGetValue(entry.Id, out var previous) && string.Equals(previous, payload, StringComparison.Ordinal))
            {
                return;
            }

            registrations[entry.Id] = payload;
        }

        if (mainPeer is not { } peer)
        {
            return;
        }

        try
        {
            await peer.SendNotificationAsync(
                ExtensionProtocol.ToolsRegister,
                HostJson.ToElement(new ToolsRegisterNotification(entry.Id, tools)),
                ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or IpcConnectionClosedException or OperationCanceledException)
        {
        }
    }

    private async Task UnregisterToolsAsync(string pluginId, CancellationToken ct)
    {
        lock (sync)
        {
            if (!registrations.Remove(pluginId))
            {
                return;
            }
        }

        if (mainPeer is not { } peer)
        {
            return;
        }

        try
        {
            await peer.SendNotificationAsync(
                ExtensionProtocol.ToolsUnregister,
                HostJson.ToElement(new ToolsUnregisterNotification(pluginId)),
                ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or IpcConnectionClosedException or OperationCanceledException)
        {
        }
    }

    private async Task StopWorkerAsync(string pluginId)
    {
        WorkerSlot? slot;
        lock (sync)
        {
            workers.Remove(pluginId, out slot);
        }

        if (slot is not null)
        {
            await slot.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ShutdownWorkersAsync()
    {
        WorkerSlot[] slots;
        lock (sync)
        {
            slots = [.. workers.Values];
            workers.Clear();
            registrations.Clear();
        }

        foreach (var slot in slots)
        {
            try
            {
                await slot.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
    }

    private WorkerSlot GetWorker(PluginEntry entry)
    {
        lock (sync)
        {
            if (workers.TryGetValue(entry.Id, out var existing))
            {
                return existing;
            }

            var slot = new WorkerSlot(entry.Id, options, ForwardToolCallAsync)
            {
                Reloaded = (tools, token) => RegisterToolsAsync(entry, tools, token),
            };
            workers[entry.Id] = slot;
            return slot;
        }
    }

    /// <summary>Relays a worker's <c>host.tool_call</c> to the main program, which owns permission checks and auditing.</summary>
    private Task<IpcMessage> ForwardToolCallAsync(IpcMessage message, CancellationToken ct)
    {
        if (mainPeer is not { } peer || peer.IsClosed)
        {
            throw new HostOperationException("main_disconnected", "The main program is not connected.");
        }

        return peer.SendRequestAsync(ExtensionProtocol.HostToolCall, message.Arguments, ct);
    }

    private PluginEntry? Find(string pluginId)
    {
        lock (sync)
        {
            return plugins.TryGetValue(pluginId, out var entry) ? entry : null;
        }
    }

    private string[] KnownPluginIds()
    {
        lock (sync)
        {
            return [.. plugins.Keys];
        }
    }

    private static bool Contains(IReadOnlyList<ToolDeclaration> tools, string name)
    {
        for (var i = 0; i < tools.Count; i++)
        {
            if (string.Equals(tools[i].Name, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static T Read<T>(IpcMessage message)
        where T : class
    {
        if (message.Arguments is not { ValueKind: JsonValueKind.Object } arguments)
        {
            throw new HostOperationException("invalid_request", $"'{message.Operation}' requires an arguments object.");
        }

        try
        {
            return arguments.Deserialize<T>(HostJson.Options) ??
                throw new HostOperationException("invalid_request", $"'{message.Operation}' requires an arguments object.");
        }
        catch (JsonException exception)
        {
            throw new HostOperationException("invalid_request", $"'{message.Operation}' has unreadable arguments: {exception.Message}");
        }
    }

    private static async Task RespondErrorAsync(IIpcPeer peer, long requestId, string code, string message)
    {
        try
        {
            await peer.RespondErrorAsync(requestId, new IpcError(code, message), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or IpcConnectionClosedException)
        {
        }
    }
}
