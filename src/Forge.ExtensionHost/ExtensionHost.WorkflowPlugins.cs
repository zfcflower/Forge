using System.Text.Json;
using Forge.Approval;
using Forge.Ipc;
using Forge.Ipc.Protocol;
using Forge.Plugin.Sdk;
using Forge.Workflows;

namespace Forge.ExtensionHost;

/// <summary>
/// The workflow half of the host: a declarative package has no assembly and no worker process, so <c>plugin.execute</c>
/// runs its document in-process through <see cref="WorkflowEngine"/>. Everything that makes a call trustworthy is shared
/// with the worker path - the approval is recomputed from the bytes on disk, the dispatch gate and the in-flight lease
/// serialize a run against approve/revoke/disable, and every step's tool call travels to the main program as a bound
/// <c>host.tool_call</c> under this run's id through <see cref="IHostFacade"/>.
/// </summary>
public sealed partial class ExtensionHost
{
    /// <summary>
    /// Checks a <c>plugin.execute</c> against a workflow package and binds it for execution. Runs under the plugin's
    /// dispatch gate: the tool has to be the one the manifest declares, the approval is re-evaluated against the freshly
    /// hashed document, and the run is registered in the active-run table before the gate is released - exactly where a
    /// worker call is registered.
    /// </summary>
    private async Task<WorkflowCall> BindWorkflowCallAsync(
        PluginEntry entry,
        PluginExecuteRequest request,
        ExtensionCallContext context,
        PluginEvaluation evaluation,
        bool nested,
        CallDeadline budget,
        CancellationToken ct)
    {
        if (entry.Workflow is not { } definition)
        {
            throw new HostOperationException(
                "plugin_not_loadable",
                $"Workflow plugin '{entry.Id}' {entry.Version} has no valid workflow document; refresh and approve it again.");
        }

        var tool = entry.Manifest.Tool ??
            throw new HostOperationException(
                "plugin_not_loadable",
                $"Workflow plugin '{entry.Id}' {entry.Version} declares no tool.");
        if (!string.Equals(tool.Name, request.Tool, StringComparison.Ordinal))
        {
            throw new HostOperationException(
                "unknown_tool",
                $"Workflow plugin '{entry.Id}' exposes only the tool '{tool.Name}'.");
        }

        await PublishWorkflowAsync(entry, budget.Token).ConfigureAwait(false);

        var rechecked = ApprovalEngine.Evaluate(Candidate(entry, evaluation), approvals);
        entry.Decision = rechecked;
        RequireAllowed(entry, rechecked);

        var root = CallBinding.RootOf(context);
        var lease = StateOf(entry.Id).BeginExecution(entry.Id, root, nested);
        ActiveRun run;
        try
        {
            run = workflowRuns.Begin(context.RunId, entry.Id, root, context);
        }
        catch
        {
            lease.Dispose();
            throw;
        }

        return new WorkflowCall(entry, definition, run, lease);
    }

    /// <summary>
    /// Runs one bound workflow call outside the plugin gate. The caller's budget becomes the run's whole budget - the
    /// engine never gets a fresh timer - and the run's own token is the linked caller/shutdown/deadline token, so a
    /// cancelled call stops scheduling new steps at once.
    /// </summary>
    private async Task<ToolResult> RunWorkflowAsync(
        WorkflowCall call,
        PluginExecuteRequest request,
        CallDeadline budget,
        CancellationToken ct)
    {
        var remaining = budget.Remaining;
        var options = new RunOptions
        {
            // The tool call's arguments are the workflow's input: steps reach them through {"$ref": "$input..."}. The host
            // is the endpoint that runs this tool, so this is the one place the argument bytes are parsed.
            Input = ParseWorkflowInput(request),
            Timeout = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1),

            // Cancellation drains for the same window a worker call gets before its process tree is killed, so a workflow
            // never holds a caller far past its budget waiting for cooperative steps that are already cancelled.
            CancellationDrainTimeout = WorkerSlot.CancelGraceWindow,
        };

        var result = await new WorkflowEngine()
            .RunAsync(call.Definition, new WorkflowHostFacade(this, call.Run), options, budget.Token)
            .ConfigureAwait(false);
        return DescribeWorkflowResult(call.Entry, result);
    }

    private static readonly byte[] EmptyArguments = "{}"u8.ToArray();

    /// <summary>The workflow's <c>$input</c>: the call's argument JSON, where an empty payload means <c>{}</c>.</summary>
    private static JsonElement ParseWorkflowInput(PluginExecuteRequest request)
    {
        var bytes = request.ArgumentsJson.IsEmpty ? EmptyArguments : request.ArgumentsJson;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new HostOperationException(
                "invalid_request",
                $"The arguments of '{request.Tool}' are not valid JSON: {exception.Message}");
        }
    }

    /// <summary>Publishes a workflow's snapshot: its one tool and its declared permissions, replacing the previous one.</summary>
    private Task PublishWorkflowAsync(PluginEntry entry, CancellationToken ct)
    {
        entry.Tools = entry.WorkflowTools;
        entry.Contributions = entry.WorkflowContributions;
        var loadout = new WorkerLoadout(
            entry.Tools,
            entry.Contributions,
            Started: false,
            StateOf(entry.Id).NextLoadGeneration());
        return PublishAsync(entry.Id, loadout, ct);
    }

    /// <summary>
    /// Relays one workflow step's tool call to the main program. The forwarded payload is the same
    /// <see cref="HostToolCallForward"/> a worker callback produces, filled from the registered run and never from the
    /// workflow document, so the main program's run binding, permission checks and approvals apply unchanged.
    /// </summary>
    private async Task<HostToolResult> CallToolForWorkflowAsync(
        ActiveRun run,
        string name,
        JsonElement arguments,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new HostToolResult("the workflow step names no tool", IsError: true);
        }

        if (string.Equals(name, "run_code", StringComparison.Ordinal))
        {
            // A workflow is not a top-level entry point either: nesting run_code is refused here as it is for a worker.
            return new HostToolResult(
                "run_code cannot be called from inside a workflow; call the underlying tools directly.",
                IsError: true);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(run.Lifetime, ct);
        ToolResult forwarded;
        try
        {
            forwarded = await ForwardToolCallAsync(
                    new HostToolCallForward(run.RunId, run.PluginId, name, HostToolCalls.ArgumentsUtf8(arguments)),
                    linked.Token)
                .ConfigureAwait(false);
        }
        catch (IpcRemoteException exception)
        {
            // A refusal by the main program (unknown tool, denied approval, budget) is the step's failure, not the run's.
            return new HostToolResult($"the main program refused '{name}': {exception.Message}", IsError: true);
        }
        catch (Exception exception) when (exception is IOException
            or IpcConnectionClosedException
            or IpcProtocolException
            or InvalidOperationException
            or HostOperationException)
        {
            return new HostToolResult($"'{name}' could not be relayed to the main program: {exception.Message}", IsError: true);
        }

        return new HostToolResult(forwarded.Content ?? string.Empty, forwarded.IsError);
    }

    /// <summary>
    /// Turns a finished run into what the calling tool returns: on success the content of the last step nothing depends on
    /// (the workflow's output), and on failure the failing step and its reason with <c>isError</c> set.
    /// </summary>
    private static ToolResult DescribeWorkflowResult(PluginEntry entry, WorkflowRunResult result)
    {
        if (result.Status == WorkflowRunStatus.Completed)
        {
            return new ToolResult(TerminalContent(result), IsError: false);
        }

        var label = result.Status switch
        {
            WorkflowRunStatus.Failed => "failed",
            WorkflowRunStatus.Invalid => "is invalid",
            WorkflowRunStatus.Cancelled => "was cancelled",
            _ => "timed out",
        };

        foreach (var step in result.Steps)
        {
            if (step.Status == StepStatus.Failed)
            {
                var reason = step.Error ?? result.Error ?? "no reason was recorded";
                return new ToolResult(
                    $"workflow '{entry.Id}' {label}: step '{step.Id}' ({step.Tool}) failed: {reason}",
                    IsError: true);
            }
        }

        var detail = string.IsNullOrWhiteSpace(result.Error) ? "no reason was recorded" : result.Error;
        return new ToolResult($"workflow '{entry.Id}' {label}: {detail}", IsError: true);
    }

    /// <summary>
    /// Content of the workflow's output step: the last step (in document order) that nothing else depends on. A workflow
    /// with several leaves reports its last leaf, which is the documented single-tool contract; a workflow with no steps
    /// produces an empty result.
    /// </summary>
    private static string TerminalContent(WorkflowRunResult result)
    {
        var dependedOn = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in result.Steps)
        {
            foreach (var dependency in step.DependsOn)
            {
                dependedOn.Add(dependency);
            }
        }

        StepResult? last = null;
        foreach (var step in result.Steps)
        {
            if (!dependedOn.Contains(step.Id) && step.Status == StepStatus.Succeeded)
            {
                last = step;
            }
        }

        if (last is null)
        {
            foreach (var step in result.Steps)
            {
                if (step.Status == StepStatus.Succeeded)
                {
                    last = step;
                }
            }
        }

        return last?.Content ?? string.Empty;
    }

    /// <summary>
    /// One bound workflow call: the parsed document, the registered run and the execution lease. Disposing it cancels the
    /// run's lifetime first - a callback of a finished run is refused at once - and releases the lease afterwards, so a
    /// revoke waiting on the in-flight count cannot be admitted while a stale callback is still pending.
    /// </summary>
    private sealed class WorkflowCall(
        PluginEntry entry,
        WorkflowDefinition definition,
        ActiveRun run,
        PluginState.Lease lease) : IDisposable
    {
        public PluginEntry Entry { get; } = entry;

        public WorkflowDefinition Definition { get; } = definition;

        public ActiveRun Run { get; } = run;

        public PluginState.Lease Lease { get; } = lease;

        public void Dispose()
        {
            Run.Dispose();
            Lease.Dispose();
        }
    }

    /// <summary>The facade a workflow's steps call tools through; it only ever knows the registered run.</summary>
    private sealed class WorkflowHostFacade(ExtensionHost host, ActiveRun run) : IHostFacade
    {
        public Task<HostToolResult> CallToolAsync(string name, JsonElement arguments, CancellationToken ct) =>
            host.CallToolForWorkflowAsync(run, name, arguments, ct);
    }
}
