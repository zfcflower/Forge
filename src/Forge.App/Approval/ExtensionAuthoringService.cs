using System.Diagnostics;
using Forge.Abstractions;
using Forge.Core.Tools;
using Forge.HostClient;
using Forge.Ipc;
using Forge.Plugin.Sdk;

namespace Forge.App.Approval;

/// <summary>One installed or staged extension as the extensions settings section lists it.</summary>
internal sealed record ExtensionRow(PluginStatusInfo Status, IReadOnlyList<string> ToolNames);

/// <summary>
/// What the settings section read from the host: the registry rows, the staged drafts and, when the read failed, the
/// sentence that says why. A failed read is never an empty successful answer.
/// </summary>
internal sealed record ExtensionSnapshot(
    IReadOnlyList<ExtensionRow> Rows,
    IReadOnlyList<StagedExtension> Pending,
    string? Problem)
{
    public bool IsAvailable => Problem is null;

    public static ExtensionSnapshot Unavailable(string problem) => new([], [], problem);
}

/// <summary>
/// The administration surface the extensions settings section drives: read the registry, enable/disable a version, delete
/// one. Nothing here reports failure as text - the view model turns the exception into its own localized sentence.
/// </summary>
internal interface IExtensionsAdmin
{
    Task<ExtensionSnapshot> ReadAsync(CancellationToken ct);

    Task<StagedExtension?> FindPendingAsync(string pluginId, CancellationToken ct);

    Task SetEnabledAsync(string pluginId, string version, bool enabled, CancellationToken ct);

    Task DeleteAsync(string pluginId, string version, CancellationToken ct);
}

/// <summary>
/// The app's implementation of Core's <see cref="IExtensionAuthoring"/>: the only place that can turn an agent's draft into
/// a live extension, and only through the user. <see cref="SubmitAsync"/> validates and stages the draft, puts a plugin
/// approval card in the queue and waits; approving commits exactly the hashes the card showed, rejecting discards the
/// draft, cancelling withdraws the card and leaves the draft staged for the settings section.
/// <para>What the settings section needs (the registry, enable/disable/delete, staged drafts) is also here. Host failures
/// become model-facing text for the agent tools and exceptions for the UI.</para>
/// </summary>
internal sealed class ExtensionAuthoringService : IExtensionAuthoring, IExtensionsAdmin
{
    private const string PendingStatus = "Pending";
    private const string UninstalledStatus = "Uninstalled";
    private const string UnknownStatus = "Unavailable";

    private readonly IExtensionAuthoringClient authoring;
    private readonly IExtensionContributionClient contributions;
    private readonly Func<string, IReadOnlyList<string>> pluginToolNames;
    private readonly ApprovalQueue queue;
    private readonly ApprovalRules rules;
    private readonly ApprovalSessionDirectory sessions;
    private readonly Lock gate = new();
    private readonly HashSet<string> inFlight = new(StringComparer.Ordinal);

    public ExtensionAuthoringService(
        IExtensionAuthoringClient authoring,
        IExtensionContributionClient contributions,
        Func<string, IReadOnlyList<string>> pluginToolNames,
        ApprovalQueue queue,
        ApprovalRules rules,
        ApprovalSessionDirectory sessions)
    {
        this.authoring = authoring ?? throw new ArgumentNullException(nameof(authoring));
        this.contributions = contributions ?? throw new ArgumentNullException(nameof(contributions));
        this.pluginToolNames = pluginToolNames ?? throw new ArgumentNullException(nameof(pluginToolNames));
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.rules = rules ?? throw new ArgumentNullException(nameof(rules));
        this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    // ---------------------------------------------------------------- agent-facing (IExtensionAuthoring)

    public async Task<IReadOnlyList<ExtensionSummary>> ListAsync(CancellationToken ct)
    {
        try
        {
            var statuses = await contributions.GetPluginStatusesAsync(ct).ConfigureAwait(false);
            var pending = await authoring.GetPendingExtensionsAsync(ct).ConfigureAwait(false);
            var pendingIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var staged in pending)
            {
                pendingIds.Add(staged.PluginId);
            }

            var summaries = new List<ExtensionSummary>(statuses.Count + pending.Count);
            var listed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var status in statuses)
            {
                listed.Add(status.PluginId);
                summaries.Add(new ExtensionSummary(
                    status.PluginId,
                    status.Version,
                    status.Status,
                    pluginToolNames(status.PluginId),
                    pendingIds.Contains(status.PluginId),
                    status.Error));
            }

            foreach (var staged in pending)
            {
                if (listed.Add(staged.PluginId))
                {
                    summaries.Add(new ExtensionSummary(staged.PluginId, staged.Version, PendingStatus, [], true));
                }
            }

            return summaries;
        }
        catch (Exception exception) when (!IsCallerCancellation(exception, ct))
        {
            // The tool gets one row that says the registry is unknown, never an empty list that reads as "no extensions".
            return [new ExtensionSummary("-", "-", UnknownStatus, [], false, Describe(exception))];
        }
    }

    public async Task<ExtensionSubmitResult> SubmitAsync(ExtensionDraft draft, ToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(context);

        if (!TryMap(draft, out var request, out var invalid))
        {
            return Invalid(invalid);
        }

        var pluginId = draft.Id;

        // The registry decides create vs update. An uninstalled id counts as absent: after a delete the same id and version
        // stay blocked, so a fresh version is submitted as a new extension.
        IReadOnlyList<PluginStatusInfo> statuses;
        try
        {
            statuses = await contributions.GetPluginStatusesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsCallerCancellation(exception, ct))
        {
            return Invalid("the plugin registry could not be read: " + Describe(exception));
        }

        var exists = false;
        foreach (var status in statuses)
        {
            if (string.Equals(status.PluginId, pluginId, StringComparison.Ordinal) &&
                !string.Equals(status.Status, UninstalledStatus, StringComparison.Ordinal))
            {
                exists = true;
                break;
            }
        }

        if (draft.IsUpdate && !exists)
        {
            return Invalid(
                $"extension '{pluginId}' is not installed, so there is nothing to update; submit it with extension_create.");
        }

        if (!draft.IsUpdate && exists)
        {
            return Invalid(
                $"extension '{pluginId}' already exists; change that one with extension_update.");
        }

        lock (gate)
        {
            if (!inFlight.Add(pluginId))
            {
                return Invalid($"another submission for extension '{pluginId}' is already waiting for the user.");
            }
        }

        try
        {
            StagedExtension staged;
            try
            {
                staged = await authoring.StageExtensionAsync(request, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (!IsCallerCancellation(exception, ct))
            {
                return Invalid("the draft could not be staged: " + Describe(exception));
            }

            if (staged.Diagnostics.Count > 0)
            {
                // A draft that does not compile never reaches the user; the agent gets the diagnostics and can fix it.
                return new ExtensionSubmitResult(
                    ExtensionSubmitOutcome.Invalid,
                    "the draft has diagnostics and was not shown to the user: " + JoinDiagnostics(staged.Diagnostics),
                    []);
            }

            var card = new ExtensionApprovalCardViewModel(
                staged,
                context.SessionId,
                sessions.TitleOf(context.SessionId),
                fromPreviousRun: false,
                queue.Dispatcher);

            // A card for the same plugin (typically one restored at startup) is superseded by this newer draft; it must not
            // discard the draft this submission just staged.
            if (await queue.FindAsync(card.Id).ConfigureAwait(false) is { } existing)
            {
                await queue
                    .WithdrawAsync(existing, new ApprovalCardOutcome(ApprovalCardDecision.Superseded))
                    .ConfigureAwait(false);
            }

            if (!await queue.EnqueueAsync(card, ct).ConfigureAwait(false))
            {
                card.Dispose();
                return Invalid($"another approval for extension '{pluginId}' is already on screen.");
            }

            try
            {
                var outcome = await card.Decision.WaitAsync(ct).ConfigureAwait(false);
                return outcome.Decision switch
                {
                    ApprovalCardDecision.Allow => await CommitAsync(staged).ConfigureAwait(false),
                    ApprovalCardDecision.Deny => await DiscardAsync(staged).ConfigureAwait(false),
                    ApprovalCardDecision.Withdrawn => Withdrawn(staged),
                    _ => Invalid("the approval card was replaced by a newer draft before the user decided; the newer draft is staged."),
                };
            }
            finally
            {
                // Cancellation lands here too: the card leaves the queue but the staged draft stays for the settings
                // section, and the caller sees the OperationCanceledException.
                await queue.WithdrawAsync(card).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (gate)
            {
                inFlight.Remove(pluginId);
            }
        }
    }

    public async Task<string> DisableAsync(string id, string version, CancellationToken ct)
    {
        try
        {
            await DisableCoreAsync(id, version, ct).ConfigureAwait(false);
            return $"disabled '{id}' {version}; its tools are no longer registered.";
        }
        catch (Exception exception) when (!IsCallerCancellation(exception, ct))
        {
            return $"'{id}' {version} could not be disabled: {Describe(exception)}";
        }
    }

    public async Task<string> DeleteAsync(string id, string version, CancellationToken ct)
    {
        try
        {
            await DeleteCoreAsync(id, version, ct).ConfigureAwait(false);
            return $"uninstalled '{id}' {version}. The same id and version stay blocked, so a new version number is needed.";
        }
        catch (Exception exception) when (!IsCallerCancellation(exception, ct))
        {
            return $"'{id}' {version} could not be deleted: {Describe(exception)}";
        }
    }

    // ---------------------------------------------------------------- extensions settings (IExtensionsAdmin)

    public async Task<ExtensionSnapshot> ReadAsync(CancellationToken ct)
    {
        try
        {
            var statuses = await contributions.GetPluginStatusesAsync(ct).ConfigureAwait(false);
            var pending = await authoring.GetPendingExtensionsAsync(ct).ConfigureAwait(false);
            var rows = new List<ExtensionRow>(statuses.Count);
            foreach (var status in statuses)
            {
                rows.Add(new ExtensionRow(status, pluginToolNames(status.PluginId)));
            }

            return new ExtensionSnapshot(rows, pending, null);
        }
        catch (Exception exception) when (!IsCallerCancellation(exception, ct))
        {
            return ExtensionSnapshot.Unavailable(Describe(exception));
        }
    }

    public async Task<StagedExtension?> FindPendingAsync(string pluginId, CancellationToken ct)
    {
        var pending = await authoring.GetPendingExtensionsAsync(ct).ConfigureAwait(false);
        foreach (var staged in pending)
        {
            if (string.Equals(staged.PluginId, pluginId, StringComparison.Ordinal))
            {
                return staged;
            }
        }

        return null;
    }

    public Task SetEnabledAsync(string pluginId, string version, bool enabled, CancellationToken ct) =>
        enabled ? EnableCoreAsync(pluginId, version, ct) : DisableCoreAsync(pluginId, version, ct);

    Task IExtensionsAdmin.DeleteAsync(string pluginId, string version, CancellationToken ct) =>
        DeleteCoreAsync(pluginId, version, ct);

    // ---------------------------------------------------------------- restored drafts

    /// <summary>
    /// Re-queues the drafts the host still has staged after a restart as "submitted before the restart" cards. They block
    /// no agent: approving commits exactly the shown hashes and rejecting discards the draft, both on a pool task.
    /// </summary>
    public async Task RestorePendingAsync(CancellationToken ct)
    {
        IReadOnlyList<StagedExtension> pending;
        try
        {
            pending = await authoring.GetPendingExtensionsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsCallerCancellation(exception, ct))
        {
            Trace.WriteLine("[approval] staged extensions could not be read: " + Describe(exception));
            return;
        }

        foreach (var staged in pending)
        {
            await EnqueueRestoredCardAsync(staged, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Puts one staged draft on screen as a restored card; false when it is already queued or being submitted.</summary>
    public async Task<bool> EnqueueRestoredCardAsync(StagedExtension staged, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(staged);
        var cardId = "extension:" + staged.PluginId;
        if (await queue.ContainsAsync(cardId).ConfigureAwait(false))
        {
            return false;
        }

        lock (gate)
        {
            if (inFlight.Contains(staged.PluginId))
            {
                return false;
            }
        }

        var card = new ExtensionApprovalCardViewModel(
            staged,
            sessionId: string.Empty,
            sessionTitle: string.Empty,
            fromPreviousRun: true,
            queue.Dispatcher);
        if (!await queue.EnqueueAsync(card, ct).ConfigureAwait(false))
        {
            card.Dispose();
            return false;
        }

        _ = CompleteRestoredCardAsync(card);
        return true;
    }

    /// <summary>Number of submissions currently waiting for the user; used by tests.</summary>
    internal int SubmissionsInFlight
    {
        get
        {
            lock (gate)
            {
                return inFlight.Count;
            }
        }
    }

    // ---------------------------------------------------------------- core steps

    /// <summary>Validates the draft into a stage request; a permission name that does not parse makes the draft invalid.</summary>
    public static bool TryMap(ExtensionDraft draft, out ExtensionStageRequest request, out string problem)
    {
        request = null!;
        problem = string.Empty;

        if (string.IsNullOrWhiteSpace(draft.Id))
        {
            problem = "the extension id is empty.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(draft.Version))
        {
            problem = "the extension version is empty.";
            return false;
        }

        var kind = draft.Kind switch
        {
            ExtensionDraftKinds.Plugin => ExtensionKinds.Plugin,
            ExtensionDraftKinds.Workflow => ExtensionKinds.Workflow,
            _ => string.Empty,
        };
        if (kind.Length == 0)
        {
            problem = $"'{draft.Kind}' is not a supported extension kind; use '{ExtensionDraftKinds.Plugin}' or '{ExtensionDraftKinds.Workflow}'.";
            return false;
        }

        var permissions = new List<Permission>(draft.Permissions.Count);
        foreach (var permission in draft.Permissions)
        {
            if (!Enum.TryParse<PermissionKind>(permission.Kind, ignoreCase: true, out var parsed) ||
                !Enum.IsDefined(parsed))
            {
                problem =
                    $"'{permission.Kind}' is not a known permission; use one of " +
                    string.Join(", ", Enum.GetNames<PermissionKind>()) + ".";
                return false;
            }

            permissions.Add(new Permission(parsed, string.IsNullOrWhiteSpace(permission.Scope) ? null : permission.Scope));
        }

        var dependencies = new List<ExtensionDependency>(draft.Dependencies.Count);
        foreach (var dependency in draft.Dependencies)
        {
            if (string.IsNullOrWhiteSpace(dependency.Name))
            {
                problem = "a dependency has no name.";
                return false;
            }

            dependencies.Add(new ExtensionDependency(dependency.Name, dependency.Version ?? string.Empty));
        }

        if (draft.Files.Count == 0)
        {
            problem = "the draft has no files.";
            return false;
        }

        ExtensionToolSpec? tool = null;
        if (draft.Tool is { } draftTool)
        {
            tool = new ExtensionToolSpec(draftTool.Name, draftTool.Description, draftTool.InputSchemaJson);
        }

        if (kind == ExtensionKinds.Workflow && tool is null)
        {
            problem = "a workflow extension must declare the one tool it exposes.";
            return false;
        }

        request = new ExtensionStageRequest(draft.Id, draft.Version, kind, draft.Files, permissions, dependencies, tool);
        return true;
    }

    /// <summary>Commits exactly the hashes the card showed and clears every remembered rule of the plugin's tool names.</summary>
    private async Task<ExtensionSubmitResult> CommitAsync(StagedExtension staged)
    {
        IReadOnlyList<string> previousTools = [];
        try
        {
            previousTools = pluginToolNames(staged.PluginId);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.WriteLine("[approval] the previous tool names could not be read: " + exception.Message);
        }

        // The approval is for this content; trust in the old code must not carry over to the new one. Rules for the old and
        // the declared new tool names are dropped BEFORE the commit: a commit that then fails (or whose result is lost)
        // leaves the rules cleared, never a live new version still covered by an old "always allow".
        var before = new HashSet<string>(previousTools, StringComparer.Ordinal);
        if (staged.Tool is { } declared && !string.IsNullOrWhiteSpace(declared.Name))
        {
            before.Add(declared.Name);
        }

        try
        {
            // Persisted rules merge in when the file read ends; clearing before that would let them come back.
            await rules.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.WriteLine("[approval] the remembered rules could not be loaded before the commit: " + exception.Message);
        }

        var cleared = before.Count == 0 ? 0 : rules.RemoveByToolNames(before);

        ExtensionCommitResult committed;
        try
        {
            committed = await authoring
                .CommitExtensionAsync(
                    new ExtensionCommitRequest(staged.PluginId, staged.SourceHash, staged.DependencyHash, staged.SdkVersion),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Invalid("the user approved, but the extension could not be committed: " + Describe(exception));
        }

        // Clear again with the names the host actually registered (a plugin's tool names are only known now).
        var names = new HashSet<string>(committed.ToolNames, StringComparer.Ordinal);
        foreach (var name in before)
        {
            names.Add(name);
        }

        cleared += rules.RemoveByToolNames(names);
        var tools = committed.ToolNames.Count == 0
            ? "it registered no tools"
            : "registered tools: " + string.Join(", ", committed.ToolNames);
        var note = cleared > 0 ? $"; {cleared} remembered rule(s) for its tools were cleared" : string.Empty;
        return new ExtensionSubmitResult(
            ExtensionSubmitOutcome.Approved,
            $"approved '{staged.PluginId}' {staged.Version} and it is live now; {tools}{note}.",
            committed.ToolNames);
    }

    /// <summary>Drops the staged draft; the live version (if any) is untouched.</summary>
    private async Task<ExtensionSubmitResult> DiscardAsync(StagedExtension staged)
    {
        try
        {
            await authoring.DiscardExtensionAsync(staged.PluginId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new ExtensionSubmitResult(
                ExtensionSubmitOutcome.Rejected,
                $"the user rejected '{staged.PluginId}' {staged.Version}; dropping the staged draft failed ({Describe(exception)}), " +
                "so it is still staged and can be handled in the extensions settings.",
                []);
        }

        return new ExtensionSubmitResult(
            ExtensionSubmitOutcome.Rejected,
            $"the user rejected '{staged.PluginId}' {staged.Version}; nothing changed.",
            []);
    }

    private async Task CompleteRestoredCardAsync(ExtensionApprovalCardViewModel card)
    {
        try
        {
            var outcome = await card.Decision.ConfigureAwait(false);
            if (outcome.IsUndecided)
            {
                // A newer submission replaced the staged draft, or the card was withdrawn without a decision (shutdown,
                // release): acting on it would discard a draft nobody rejected.
                return;
            }

            if (outcome.IsAllowed)
            {
                await CommitAsync(card.Staged).ConfigureAwait(false);
            }
            else
            {
                await DiscardAsync(card.Staged).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.WriteLine("[approval] a restored extension card failed: " + exception.Message);
        }
        finally
        {
            await queue.WithdrawAsync(card).ConfigureAwait(false);
        }
    }

    private Task EnableCoreAsync(string pluginId, string version, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        return authoring.EnablePluginAsync(pluginId, version, ct);
    }

    private Task DisableCoreAsync(string pluginId, string version, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        return authoring.DisablePluginAsync(pluginId, version, ct);
    }

    private Task DeleteCoreAsync(string pluginId, string version, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        return authoring.DeletePluginAsync(pluginId, version, ct);
    }

    /// <summary>
    /// The card went away without a user decision (session released, app closing). The draft is deliberately kept: it
    /// reappears as a "submitted before restart" card, so the agent must not report a rejection.
    /// </summary>
    private static ExtensionSubmitResult Withdrawn(StagedExtension staged) =>
        new(
            ExtensionSubmitOutcome.Withdrawn,
            $"the approval for '{staged.PluginId}' {staged.Version} was withdrawn before the user decided (the session closed or " +
            "the app is shutting down). This is not a rejection: the draft is kept pending and the user can still approve or " +
            "reject it from the extensions settings; do not resubmit it.",
            []);

    private static ExtensionSubmitResult Invalid(string problem) =>
        new(ExtensionSubmitOutcome.Invalid, problem, []);

    private static string JoinDiagnostics(IReadOnlyList<string> diagnostics) =>
        string.Join(Environment.NewLine, diagnostics);

    private static bool IsCallerCancellation(Exception exception, CancellationToken ct) =>
        exception is OperationCanceledException && ct.IsCancellationRequested;

    /// <summary>Model-facing text for a host failure; the host's error code keeps its documented meaning.</summary>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            IpcRemoteException remote => remote.Code switch
            {
                "approval_mismatch" =>
                    "the staged files no longer match what the approval card showed, so nothing was applied; submit the draft again",
                "extension_blocked" =>
                    "the host refuses this extension id and version (a deleted version stays blocked); submit a new version number",
                "invalid_extension" => remote.Message,
                _ => remote.Message,
            },
            IpcTimeoutException => "the extension host did not answer in time",
            IpcVersionMismatchException mismatch => mismatch.Message,
            OperationCanceledException => "the extension host cancelled the request",
            _ => exception.Message,
        };
    }
}
