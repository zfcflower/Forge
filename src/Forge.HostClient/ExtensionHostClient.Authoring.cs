using Forge.Ipc;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.HostClient;

/// <summary>
/// The authoring surface (<see cref="IExtensionAuthoringClient"/>) of <see cref="ExtensionHostClient"/>, kept in its own
/// file so the request dispatch beside it changes on its own. Arguments are validated before anything reaches the wire;
/// staging and committing may compile C# in the host, so those two calls get the larger of
/// <see cref="ExtensionHostClientOptions.RequestTimeout"/> and five minutes while everything else keeps the normal
/// per-call budget. Every answer is validated field by field: an unusable one is diagnosed and thrown as
/// <see cref="InvalidDataException"/> instead of being turned into an empty list or a default value, and host business
/// errors (<see cref="IpcRemoteException"/>, carrying codes such as <c>approval_mismatch</c>) reach the caller unchanged.
/// </summary>
public sealed partial class ExtensionHostClient
{
    /// <summary>Staging and committing run the host's compile/validation path, so they are never cut short by the
    /// two-minute request default.</summary>
    private static readonly TimeSpan CompileBudgetFloor = TimeSpan.FromMinutes(5);

    /// <summary>Writes a draft into the host's pending area (replacing an earlier draft of the same id) and returns what
    /// the approval card shows, diagnostics included. Nothing staged is loaded or run by this call.</summary>
    public async Task<Sdk.StagedExtension> StageExtensionAsync(Sdk.ExtensionStageRequest request, CancellationToken ct)
    {
        ValidateStageRequest(request);
        var active = RequireRunning();
        var response = await SendAuthoringAsync(active, Sdk.ExtensionProtocol.ExtensionsStage, request, CompileBudget, ct)
            .ConfigureAwait(false);
        if (!TryReadStagedAnswer(response, out var staged, out var problem))
        {
            throw UnusableAnswer(Sdk.ExtensionProtocol.ExtensionsStage, problem);
        }

        return staged;
    }

    /// <summary>Every staged draft, so pending approvals survive a restart; the list is only as empty as the host
    /// reports it. One unusable item makes the whole answer unusable rather than shortening the queue.</summary>
    public async Task<IReadOnlyList<Sdk.StagedExtension>> GetPendingExtensionsAsync(CancellationToken ct)
    {
        var active = RequireRunning();
        var response = await SendAuthoringAsync(
                active, Sdk.ExtensionProtocol.ExtensionsPending, IpcEmpty.Instance, RequestBudget, ct)
            .ConfigureAwait(false);
        if (!TryReadPendingExtensions(response, out var pending, out var problem))
        {
            throw UnusableAnswer(Sdk.ExtensionProtocol.ExtensionsPending, problem);
        }

        return pending;
    }

    /// <summary>Swaps the staged draft in and approves exactly the hashes the user saw. The live version keeps running
    /// until this call succeeds; a hash mismatch is the host's <c>approval_mismatch</c> and changes nothing.</summary>
    public async Task<Sdk.ExtensionCommitResult> CommitExtensionAsync(
        Sdk.ExtensionCommitRequest request, CancellationToken ct)
    {
        ValidateCommitRequest(request);
        var active = RequireRunning();
        var response = await SendAuthoringAsync(active, Sdk.ExtensionProtocol.ExtensionsCommit, request, CompileBudget, ct)
            .ConfigureAwait(false);
        if (!TryReadCommitResult(response, out var result, out var problem))
        {
            throw UnusableAnswer(Sdk.ExtensionProtocol.ExtensionsCommit, problem);
        }

        return result;
    }

    /// <summary>Drops a staged draft; the live version (when there is one) is untouched.</summary>
    public async Task DiscardExtensionAsync(string pluginId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        var active = RequireRunning();
        await SendAuthoringAsync(
                active, Sdk.ExtensionProtocol.ExtensionsDiscard, new Sdk.ExtensionIdRequest(pluginId), RequestBudget, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Disables a version: the host unregisters its tools and stops its worker.</summary>
    public Task DisablePluginAsync(string pluginId, string version, CancellationToken ct) =>
        SendPluginVersionAsync(Sdk.ExtensionProtocol.PluginDisable, pluginId, version, ct);

    /// <summary>Re-enables a disabled version; it runs again only if its approved hashes still match the bytes on disk.</summary>
    public Task EnablePluginAsync(string pluginId, string version, CancellationToken ct) =>
        SendPluginVersionAsync(Sdk.ExtensionProtocol.PluginEnable, pluginId, version, ct);

    /// <summary>Uninstalls a version for good (the same id and version stay blocked) and removes its directory.</summary>
    public Task DeletePluginAsync(string pluginId, string version, CancellationToken ct) =>
        SendPluginVersionAsync(Sdk.ExtensionProtocol.PluginDelete, pluginId, version, ct);

    /// <summary>The budget of a call that can trigger a host-side compile: the configured request timeout when it is
    /// longer than the five-minute floor, the floor otherwise, and no deadline at all when the client is configured
    /// without one (<see cref="ExtensionHostClientOptions.RequestTimeout"/> null or non-positive).</summary>
    private TimeSpan? CompileBudget =>
        RequestBudget is not { } timeout ? null : timeout > CompileBudgetFloor ? timeout : CompileBudgetFloor;

    private async Task SendPluginVersionAsync(string operation, string pluginId, string version, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var active = RequireRunning();
        await SendAuthoringAsync(active, operation, new Sdk.PluginVersionRequest(pluginId, version), RequestBudget, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Sends one authoring request with this call's own budget. An expired budget is recorded in
    /// <see cref="Diagnostics"/> before <see cref="IpcTimeoutException"/> reaches the caller; host business errors and
    /// caller cancellation are never touched here.</summary>
    private async Task<IpcMessage> SendAuthoringAsync<TArgs>(
        IpcPeer active, string operation, TArgs payload, TimeSpan? budget, CancellationToken ct)
    {
        try
        {
            return await SendAsync(active, operation, payload, budget, ct).ConfigureAwait(false);
        }
        catch (IpcTimeoutException ex)
        {
            Diagnose($"{operation} did not answer within its budget: {ex.Message}");
            throw;
        }
    }

    /// <summary>Builds the failure for an answer the client refuses to interpret, after recording why. The message
    /// names the operation and the exact field so both the caller and the service log can locate the bad answer.</summary>
    private InvalidDataException UnusableAnswer(string operation, string problem)
    {
        Diagnose($"{operation} returned unusable data: {problem}");
        return new InvalidDataException($"{operation} returned a response that cannot be decoded: {problem}");
    }

    private static void ValidateStageRequest(Sdk.ExtensionStageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Version);
        if (!IsKnownExtensionKind(request.Kind))
        {
            throw new ArgumentException(
                $"The extension kind '{request.Kind}' cannot be staged; use '{Sdk.ExtensionKinds.Plugin}' or " +
                $"'{Sdk.ExtensionKinds.Workflow}'.",
                nameof(request));
        }

        ArgumentNullException.ThrowIfNull(request.Files);
        ArgumentNullException.ThrowIfNull(request.Permissions);
        ArgumentNullException.ThrowIfNull(request.Dependencies);
        foreach (var file in request.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Key))
            {
                throw new ArgumentException("The staged files carry a blank relative path.", nameof(request));
            }

            if (file.Value is null)
            {
                throw new ArgumentException($"The staged file '{file.Key}' has no text.", nameof(request));
            }
        }

        foreach (var permission in request.Permissions)
        {
            if (permission is null)
            {
                throw new ArgumentException("The staged permissions carry a null entry.", nameof(request));
            }
        }

        foreach (var dependency in request.Dependencies)
        {
            if (dependency is null)
            {
                throw new ArgumentException("The staged dependencies carry a null entry.", nameof(request));
            }
        }
    }

    private static void ValidateCommitRequest(Sdk.ExtensionCommitRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DependencyHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SdkVersion);
    }

    private static bool IsKnownExtensionKind(string? kind) =>
        string.Equals(kind, Sdk.ExtensionKinds.Plugin, StringComparison.Ordinal) ||
        string.Equals(kind, Sdk.ExtensionKinds.Workflow, StringComparison.Ordinal);

    /// <summary>Reads an <c>extensions.pending</c> answer. Every item is validated on its own and one unusable item
    /// makes the whole answer unusable instead of hiding the item that needs review.</summary>
    private static bool TryReadPendingExtensions(
        IpcMessage response, out IReadOnlyList<Sdk.StagedExtension> pending, out string problem)
    {
        pending = [];
        if (!HostAnswerRules.TryRead<Sdk.StagedExtensionList>(response, out var list, out problem) ||
            list.Items is not { } items)
        {
            problem = "The Extension Host returned no pending extension list.";
            return false;
        }

        var staged = new Sdk.StagedExtension[items.Count];
        for (var index = 0; index < items.Count; index++)
        {
            if (!TryReadStagedExtension(items[index], $"items[{index}]", out staged[index], out problem))
            {
                return false;
            }
        }

        pending = staged;
        return true;
    }

    private static bool TryReadStagedAnswer(IpcMessage response, out Sdk.StagedExtension staged, out string problem)
    {
        const string Label = "The staged extension";
        staged = null!;
        if (!response.HasPayload)
        {
            problem = $"{Label} is missing from the response.";
            return false;
        }

        if (!HostAnswerRules.TryRead<Sdk.StagedExtension>(response, out var decoded, out var detail))
        {
            problem = $"{Label} cannot be decoded: {detail}";
            return false;
        }

        return TryReadStagedExtension(decoded, Label, out staged, out problem);
    }

    /// <summary>Validates one staged extension field by field. A draft that does not compile legitimately carries no
    /// hashes as long as it carries diagnostics; everything else must be complete, because the approval card and the
    /// later commit depend on it.</summary>
    private static bool TryReadStagedExtension(
        Sdk.StagedExtension? decoded, string label, out Sdk.StagedExtension staged, out string problem)
    {
        staged = null!;
        problem = string.Empty;
        if (decoded is null)
        {
            problem = $"{label} is null instead of a staged extension object.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(decoded.PluginId))
        {
            problem = $"{label} has no plugin id.";
            return false;
        }

        var identity = $"{label} ('{decoded.PluginId}')";
        if (string.IsNullOrWhiteSpace(decoded.Version))
        {
            problem = $"{identity} has no version.";
            return false;
        }

        if (!IsKnownExtensionKind(decoded.Kind))
        {
            problem = string.IsNullOrWhiteSpace(decoded.Kind)
                ? $"{identity} has no kind."
                : $"{identity} has the kind '{decoded.Kind}'; only '{Sdk.ExtensionKinds.Plugin}' and " +
                  $"'{Sdk.ExtensionKinds.Workflow}' can be authored.";
            return false;
        }

        if (!TryReadStagedFiles(decoded, identity, out problem) ||
            !HostAnswerRules.TryReadPermissions(decoded.Permissions, identity, out problem) ||
            !TryReadStagedDependencies(decoded, identity, out problem) ||
            !TryReadStagedDiagnostics(decoded, identity, out problem) ||
            !TryReadStagedHashes(decoded, identity, out problem))
        {
            return false;
        }

        // A nil hash binds as null although the record declares it non-nullable (a draft that could not compile).
        // Only nulls are replaced; a value the host supplied is kept verbatim.
        staged = decoded with
        {
            SourceHash = decoded.SourceHash ?? string.Empty,
            DependencyHash = decoded.DependencyHash ?? string.Empty,
            SdkVersion = decoded.SdkVersion ?? string.Empty,
        };
        return true;
    }

    private static bool TryReadStagedFiles(Sdk.StagedExtension staged, string identity, out string problem)
    {
        problem = string.Empty;
        if (staged.Files is not { } files)
        {
            problem = $"{identity} has no file list.";
            return false;
        }

        for (var position = 0; position < files.Count; position++)
        {
            var file = files[position];
            if (file is null)
            {
                problem = $"{identity} has an unusable file at files[{position}].";
                return false;
            }

            if (string.IsNullOrWhiteSpace(file.Path))
            {
                problem = $"{identity} has a file without a path at files[{position}].";
                return false;
            }

            if (file.Text is null)
            {
                problem = $"{identity} has no text for files[{position}] ('{file.Path}').";
                return false;
            }
        }

        return true;
    }

    private static bool TryReadStagedDependencies(Sdk.StagedExtension staged, string identity, out string problem)
    {
        problem = string.Empty;
        if (staged.Dependencies is not { } dependencies)
        {
            problem = $"{identity} has no dependency list.";
            return false;
        }

        for (var position = 0; position < dependencies.Count; position++)
        {
            if (dependencies[position] is null)
            {
                problem = $"{identity} has an unusable dependency at dependencies[{position}].";
                return false;
            }
        }

        return true;
    }

    private static bool TryReadStagedDiagnostics(Sdk.StagedExtension staged, string identity, out string problem)
    {
        problem = string.Empty;
        if (staged.Diagnostics is not { } diagnostics)
        {
            problem = $"{identity} has no diagnostic list.";
            return false;
        }

        for (var position = 0; position < diagnostics.Count; position++)
        {
            if (diagnostics[position] is null)
            {
                problem = $"{identity} has an unusable diagnostic at diagnostics[{position}].";
                return false;
            }
        }

        return true;
    }

    private static bool TryReadStagedHashes(Sdk.StagedExtension staged, string identity, out string problem)
    {
        problem = string.Empty;
        if (staged.Diagnostics is { Count: > 0 })
        {
            // Nothing compiled, so there are no hashes to approve yet; the diagnostics are the reason.
            return true;
        }

        if (string.IsNullOrWhiteSpace(staged.SourceHash))
        {
            problem = $"{identity} has no source hash while reporting no diagnostics.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(staged.DependencyHash))
        {
            problem = $"{identity} has no dependency hash while reporting no diagnostics.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(staged.SdkVersion))
        {
            problem = $"{identity} has no SDK version while reporting no diagnostics.";
            return false;
        }

        return true;
    }

    /// <summary>Reads an <c>extensions.commit</c> answer: the plugin status goes through the <c>plugins.status</c> row
    /// rules unchanged and the registered tool names must be a list of non-blank names.</summary>
    private static bool TryReadCommitResult(
        IpcMessage response, out Sdk.ExtensionCommitResult result, out string problem)
    {
        result = null!;
        if (!response.HasPayload)
        {
            problem = "The Extension Host returned no commit result.";
            return false;
        }

        if (!HostAnswerRules.TryRead<Sdk.ExtensionCommitResult>(response, out var decoded, out var detail))
        {
            problem = $"The commit result cannot be decoded: {detail}";
            return false;
        }

        if (decoded.Status is null)
        {
            problem = "The commit result carries no plugin status.";
            return false;
        }

        if (!HostAnswerRules.TryReadPluginStatus(decoded.Status, "status", out var status, out problem))
        {
            return false;
        }

        if (decoded.ToolNames is not { } toolNames)
        {
            problem = "The commit result carries no tool name list.";
            return false;
        }

        for (var position = 0; position < toolNames.Count; position++)
        {
            if (string.IsNullOrWhiteSpace(toolNames[position]))
            {
                problem = $"The commit result has an unusable tool name at toolNames[{position}].";
                return false;
            }
        }

        result = decoded with { Status = status };
        return true;
    }
}
