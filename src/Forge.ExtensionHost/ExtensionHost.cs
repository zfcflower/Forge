using System.Text.Json;
using Forge.Approval;
using Forge.Ipc;
using Forge.Plugin.Sdk;

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
    private readonly Dictionary<string, PluginState> states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> registrations = new(StringComparer.Ordinal);
    private readonly List<PluginIssue> issues = [];
    private readonly Lock sync = new();
    private readonly SemaphoreSlim refreshGate = new(1, 1);
#if DEBUG
    private readonly IHostTestHook? testHook;
#endif
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
#if DEBUG
        testHook = FileHostTestHook.Activate(options.PipeName);
#endif
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
        approvals.Dispose();
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

        var captured = Find(request.PluginId) ??
            throw new HostOperationException("unknown_plugin", $"No plugin '{request.PluginId}' was discovered.");
        await CaptureAsync(captured.Id, ct).ConfigureAwait(false);

        var state = StateOf(captured.Id);
        WorkerSlot slot;
        PluginState.Lease lease;
        await state.EnterAsync(ct).ConfigureAwait(false);
        try
        {
            var entry = Current(captured);
            var evaluation = await EvaluateAsync(entry, ct).ConfigureAwait(false);
            RequireAllowed(entry, evaluation.Decision);
            if (evaluation.Image is not { } image)
            {
                throw new HostOperationException("plugin_not_loadable", $"Plugin '{entry.Id}' {entry.Version} has no loadable assembly.");
            }

            slot = GetWorker(entry);
            if (await slot.EnsureLoadedAsync(entry.Version, image, ct).ConfigureAwait(false))
            {
                entry.Tools = slot.Tools;
            }

            var rechecked = ApprovalEngine.Evaluate(Candidate(entry, evaluation), approvals);
            entry.Decision = rechecked;
            RequireAllowed(entry, rechecked);

            await RegisterToolsAsync(entry.Id, slot.Tools, ct).ConfigureAwait(false);
            if (!Contains(slot.Tools, request.Tool))
            {
                throw new HostOperationException("unknown_tool", $"Plugin '{entry.Id}' does not declare a tool named '{request.Tool}'.");
            }

            lease = state.BeginExecution();
        }
        finally
        {
            state.Exit();
        }

        using (lease)
        {
            var result = await slot.ExecuteAsync(request, options.PipeName, ct).ConfigureAwait(false);
            return HostJson.ToElement(new PluginExecuteResponse(result.Content, result.IsError));
        }
    }

    private async Task<JsonElement> ApproveAsync(PluginApproveRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PluginId))
        {
            throw new HostOperationException("invalid_request", "pluginId is required.");
        }

        var captured = Find(request.PluginId) ??
            throw new HostOperationException("unknown_plugin", $"No plugin '{request.PluginId}' was discovered.");

        var state = StateOf(captured.Id);
        await state.EnterAsync(ct).ConfigureAwait(false);
        try
        {
            var entry = Current(captured);
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
            if (await RefusalReasonAsync(entry, sourceHash, dependencyHash, sdkVersion, ct).ConfigureAwait(false) is { } refusal)
            {
                WithdrawUnusableApproval(entry.Id, version);
                throw new HostOperationException(
                    "approval_mismatch",
                    $"The approval does not cover the current content of '{entry.Id}': {refusal}.");
            }

            approvals.SetApproved(entry.Id, version, sourceHash, dependencyHash, sdkVersion);
            await ApplyCoreAsync(entry, ct).ConfigureAwait(false);

            if (!entry.Decision.IsAllowed)
            {
                throw new HostOperationException(
                    "approval_mismatch",
                    $"The approval does not cover the current content of '{entry.Id}': {entry.Decision.Reason}.");
            }
        }
        finally
        {
            state.Exit();
        }

        return HostJson.EmptyObject;
    }

    /// <summary>
    /// Compares the approval the main program sent with the plugin's content on disk now. An approval may only be
    /// recorded when its hashes describe exactly the bytes a run would load, so this runs before anything is persisted.
    /// Returns the reason the approval does not cover the content, or <c>null</c> when it does.
    /// </summary>
    private async Task<string?> RefusalReasonAsync(
        PluginEntry entry,
        string sourceHash,
        string dependencyHash,
        string sdkVersion,
        CancellationToken ct)
    {
        if (approvals.Get(entry.Id, entry.Version) is { Status: PluginStatus.Disabled or PluginStatus.Uninstalled } blocked)
        {
            return $"plugin {entry.Id} {entry.Version} is {blocked.Status.ToString().ToLowerInvariant()}";
        }

        PluginContent content;
        try
        {
            content = await PluginCatalog.SnapshotAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"the content of {entry.Id} {entry.Version} could not be read: {exception.Message}";
        }

        if (!string.Equals(content.SourceHash, sourceHash, StringComparison.Ordinal))
        {
            return "the source hash does not match the content on disk";
        }

        if (!string.Equals(content.DependencyHash, dependencyHash, StringComparison.Ordinal))
        {
            return "the dependency hash does not match the files on disk";
        }

        if (!string.Equals(entry.SdkVersion, sdkVersion, StringComparison.Ordinal))
        {
            return $"the plugin is installed against SDK '{entry.SdkVersion}', not '{sdkVersion}'";
        }

        return null;
    }

    /// <summary>Returns a record whose status claims approval for content its own hashes no longer describe to
    /// <see cref="PluginStatus.Draft"/>; a record that still holds a usable approval is left untouched.</summary>
    private void WithdrawUnusableApproval(string pluginId, string version)
    {
        if (approvals.Get(pluginId, version) is not { Status: PluginStatus.Approved } record)
        {
            return;
        }

        var decision = ApprovalEngine.Evaluate(
            new PluginRecord
            {
                PluginId = record.PluginId,
                Version = record.Version,
                SourceHash = record.SourceHash,
                DependencyHash = record.DependencyHash,
                SdkVersion = record.SdkVersion,
            },
            record);
        if (!decision.IsAllowed)
        {
            approvals.Revoke(pluginId, version);
        }
    }

    private async Task<JsonElement> RevokeAsync(PluginRevokeRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PluginId))
        {
            throw new HostOperationException("invalid_request", "pluginId is required.");
        }

        var captured = Find(request.PluginId) ??
            throw new HostOperationException("unknown_plugin", $"No plugin '{request.PluginId}' was discovered.");

        var state = StateOf(captured.Id);
        await state.EnterAsync(ct).ConfigureAwait(false);
        try
        {
            var entry = Current(captured);
            var version = string.IsNullOrWhiteSpace(request.Version) ? entry.Version : request.Version;
            if (!string.Equals(version, entry.Version, StringComparison.Ordinal))
            {
                throw new HostOperationException(
                    "version_mismatch",
                    $"Plugin '{entry.Id}' is installed at version '{entry.Version}', not '{version}'.");
            }

            approvals.Revoke(entry.Id, version);
            await UnregisterToolsAsync(entry.Id, CancellationToken.None).ConfigureAwait(false);

            // Killing the worker is what ends the executions already in flight; draining them afterwards guarantees
            // that nothing this plugin dispatched is still running when the revoke returns, so no worker of a revoked
            // plugin can be left behind and no later call can be recovered into a new one.
            await StopWorkerAsync(entry.Id).ConfigureAwait(false);
            await state.WaitForIdleAsync(RevokeDrainWindow, ct).ConfigureAwait(false);
            await ApplyCoreAsync(entry, ct).ConfigureAwait(false);
        }
        finally
        {
            state.Exit();
        }

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
                var state = StateOf(entry.Id);
                await state.EnterAsync(ct).ConfigureAwait(false);
                try
                {
                    SyncRecord(entry);
                    lock (sync)
                    {
                        plugins[entry.Id] = entry;
                    }

                    await ApplyLockedAsync(entry, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    entry.Issues.Add(new PluginIssue(entry.Id, PluginIssueKind.InvalidPackage, exception.Message, entry.Directory));
                }
                finally
                {
                    state.Exit();
                }
            }

            foreach (var id in KnownPluginIds())
            {
                if (present.Contains(id))
                {
                    continue;
                }

                var state = StateOf(id);
                await state.EnterAsync(ct).ConfigureAwait(false);
                try
                {
                    lock (sync)
                    {
                        plugins.Remove(id);
                    }

                    await UnregisterToolsAsync(id, ct).ConfigureAwait(false);
                    await StopWorkerAsync(id).ConfigureAwait(false);
                }
                finally
                {
                    state.Exit();
                }
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

    /// <summary>Re-evaluates approval for one plugin and brings its worker and tool registration in line with the result,
    /// serialized against the plugin's executions and its other approval changes. A refresh also lifts an exhausted
    /// failure budget: a disabled slot and its published tools are dropped first, so the reload starts a fresh worker
    /// and registers its tools again. The caller holds the plugin's gate.</summary>
    private async Task ApplyLockedAsync(PluginEntry entry, CancellationToken ct)
    {
        var state = StateOf(entry.Id);
        if (state.IsExhausted)
        {
            ForgetRegistration(entry.Id);
            await StopWorkerAsync(entry.Id).ConfigureAwait(false);
        }

        state.ResetFailures();
        await ApplyCoreAsync(entry, ct).ConfigureAwait(false);
    }

    private async Task ApplyCoreAsync(PluginEntry entry, CancellationToken ct)
    {
        var evaluation = await EvaluateAsync(entry, ct).ConfigureAwait(false);
        if (!evaluation.Decision.IsAllowed || evaluation.Image is not { } image)
        {
            await UnregisterToolsAsync(entry.Id, ct).ConfigureAwait(false);
            await StopWorkerAsync(entry.Id).ConfigureAwait(false);
            return;
        }

        try
        {
            var slot = GetWorker(entry);
            if (await slot.EnsureLoadedAsync(entry.Version, image, ct).ConfigureAwait(false))
            {
                entry.Tools = slot.Tools;
            }

            await RegisterToolsAsync(entry.Id, slot.Tools, ct).ConfigureAwait(false);
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
    /// Builds the plugin's approval candidate from the bytes on disk now. The image is re-hashed and all of its file
    /// digests are folded into the dependency hash, so an approval only holds for exactly the files a call would load:
    /// a replaced artifact, a replaced private dependency or an edited source all need a refresh and a new approval.
    /// </summary>
    private async Task<PluginEvaluation> EvaluateAsync(PluginEntry entry, CancellationToken ct)
    {
        if (!entry.HasContent)
        {
            var blocked = ApprovalDecision.Block($"plugin {entry.Id} {entry.Version} has no loadable content");
            entry.Decision = blocked;
            return new PluginEvaluation(blocked, null, entry.SourceHash, entry.DependencyHash);
        }

        PluginContent content;
        try
        {
            content = await PluginCatalog.SnapshotAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var unreadable = ApprovalDecision.Require(
                $"the content of {entry.Id} {entry.Version} could not be read: {exception.Message}");
            entry.Decision = unreadable;
            return new PluginEvaluation(unreadable, null, entry.SourceHash, entry.DependencyHash);
        }

        var decision = string.Equals(content.SourceHash, entry.SourceHash, StringComparison.Ordinal)
            ? ApprovalEngine.Evaluate(
                new PluginRecord
                {
                    PluginId = entry.Id,
                    Version = entry.Version,
                    SourceHash = content.SourceHash,
                    DependencyHash = content.DependencyHash,
                    SdkVersion = entry.SdkVersion,
                },
                approvals)
            : ApprovalDecision.Require($"the content of {entry.Id} {entry.Version} changed on disk; refresh before running it");

        entry.Decision = decision;
        return new PluginEvaluation(decision, content.Image, content.SourceHash, content.DependencyHash);
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

    private async Task RegisterToolsAsync(string pluginId, IReadOnlyList<ToolDeclaration> tools, CancellationToken ct)
    {
        var payload = HostJson.ToElement(tools).GetRawText();
        lock (sync)
        {
            if (registrations.TryGetValue(pluginId, out var previous) && string.Equals(previous, payload, StringComparison.Ordinal))
            {
                return;
            }

            registrations[pluginId] = payload;
        }

        if (mainPeer is not { } peer)
        {
            return;
        }

        try
        {
            await peer.SendNotificationAsync(
                ExtensionProtocol.ToolsRegister,
                HostJson.ToElement(new ToolsRegisterNotification(pluginId, tools)),
                ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or IpcConnectionClosedException or OperationCanceledException)
        {
        }
    }

    /// <summary>Drops what the main program was told about a plugin's tools, so the next registration is sent even when
    /// the declarations are unchanged.</summary>
    private void ForgetRegistration(string pluginId)
    {
        lock (sync)
        {
            registrations.Remove(pluginId);
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

    /// <summary>Bound on waiting for the executions a revoke just killed to unwind before it returns.</summary>
    private static readonly TimeSpan RevokeDrainWindow = TimeSpan.FromSeconds(10);

    private WorkerSlot GetWorker(PluginEntry entry)
    {
        var pluginId = entry.Id;
        lock (sync)
        {
            if (workers.TryGetValue(pluginId, out var existing))
            {
                return existing;
            }

            var slot = new WorkerSlot(pluginId, options, StateOf(pluginId), ForwardToolCallAsync)
            {
                Reloaded = (tools, token) => RegisterToolsAsync(pluginId, tools, token),
            };
            workers[pluginId] = slot;
            return slot;
        }
    }

    /// <summary>The per-plugin state the host keeps for the whole run of the host. It is never discarded, so the gate a
    /// request is waiting on stays the same one every approval change and every refresh takes for that plugin id.</summary>
    private PluginState StateOf(string pluginId)
    {
        lock (sync)
        {
            if (!states.TryGetValue(pluginId, out var state))
            {
                state = new PluginState();
                states[pluginId] = state;
            }

            return state;
        }
    }

    private static void RequireAllowed(PluginEntry entry, ApprovalDecision decision)
    {
        if (!decision.IsAllowed)
        {
            throw new HostOperationException(
                "plugin_not_approved",
                $"Plugin '{entry.Id}' {entry.Version} must not run: {decision.Reason}.");
        }
    }

    private static PluginRecord Candidate(PluginEntry entry, PluginEvaluation evaluation) => new()
    {
        PluginId = entry.Id,
        Version = entry.Version,
        SourceHash = evaluation.SourceHash,
        DependencyHash = evaluation.DependencyHash,
        SdkVersion = entry.SdkVersion,
    };

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

#if DEBUG
    private ValueTask CaptureAsync(string pluginId, CancellationToken ct) =>
        testHook is { } hook ? hook.AfterEntryCapturedAsync(pluginId, ct) : ValueTask.CompletedTask;
#else
    private static ValueTask CaptureAsync(string pluginId, CancellationToken ct) => ValueTask.CompletedTask;
#endif

    /// <summary>
    /// Resolves the entry a request must use once it holds the plugin's gate: a refresh that replaced or removed the
    /// plugin while the request was waiting invalidates the entry it captured before the gate, so the request fails
    /// instead of running bytes nobody approved any more.
    /// </summary>
    private PluginEntry Current(PluginEntry captured)
    {
        var current = Find(captured.Id);
        if (current is null)
        {
            throw new HostOperationException(
                "unknown_plugin",
                $"Plugin '{captured.Id}' was removed by a refresh; the request was dropped.");
        }

        if (!ReferenceEquals(current, captured))
        {
            throw new HostOperationException(
                "plugin_changed",
                $"Plugin '{captured.Id}' was replaced by a refresh ('{captured.Version}' -> '{current.Version}'); the request was dropped.");
        }

        return current;
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
