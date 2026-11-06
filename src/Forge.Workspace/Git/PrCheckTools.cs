using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>
/// The agent's CI tools: <c>pr_checks</c> (a snapshot of the checks and merge state, or a wait for them to settle) and
/// <c>pr_ci_log</c> (the log of one failed job). Both are read-only. They never fix, sync, re-run or merge anything;
/// what to do with each result is described by the <c>pr_workflow</c> tool's "checks" phase, which their descriptions
/// point to. Registered by the host next to <see cref="GitTools"/>.
/// </summary>
public sealed class PrCheckTools
{
    public const string ChecksName = "pr_checks";
    public const string CiLogName = "pr_ci_log";

    public PrCheckTools(IPrPlatform platform, PrChecksPoller poller, Func<PrChecksPollerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(poller);
        ArgumentNullException.ThrowIfNull(options);

        Checks = new PrChecksTool(platform, poller, options);
        CiLog = new PrCiLogTool(platform);
        All = [Checks, CiLog];
    }

    /// <summary>
    /// <c>{"pr"?: integer, "wait"?: boolean}</c> → by default one reading: every check (name, bucket, link, and the run and
    /// job ids of failed ones), the draft flag, review decision and merge state, plus a verdict. An empty check list is
    /// reported as "not determined yet", never as "no CI". With <c>wait</c> it polls through <see cref="PrChecksPoller"/>
    /// using the live options, until the checks settle, the maximum wait elapses or the turn is cancelled. The pull request
    /// defaults to the one of the current branch.
    /// </summary>
    public ITool Checks { get; }

    /// <summary>
    /// <c>{"job_id": integer, "run_id"?: integer}</c> → the log of the failed steps of one job, whole (the platform layer
    /// keeps the tail of a very long log and says so). Over-length output is spilled to an artifact by the agent loop.
    /// </summary>
    public ITool CiLog { get; }

    /// <summary>Both tools, for registration.</summary>
    public IReadOnlyList<ITool> All { get; }
}

/// <summary>Shared plumbing of the PR check tools: one JSON parse per call and exceptions (except cancellation) turned into tool errors.</summary>
internal abstract class PrCheckToolBase : ITool
{
    protected PrCheckToolBase(string name, string description, string schema, ToolSideEffect sideEffect)
    {
        Definition = new ToolDefinition(name, description, schema);
        SideEffect = sideEffect;
    }

    public ToolDefinition Definition { get; }

    public ToolSideEffect SideEffect { get; }

    public async ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(context);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        }
        catch (JsonException exception)
        {
            return new ToolResult($"{Definition.Name} arguments are not valid JSON: {exception.Message}", IsError: true);
        }

        using (document)
        {
            try
            {
                return await InvokeCoreAsync(document.RootElement, context, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new ToolResult($"{Definition.Name} failed: {exception.Message}", IsError: true);
            }
        }
    }

    protected abstract ValueTask<ToolResult> InvokeCoreAsync(JsonElement arguments, ToolContext context, CancellationToken ct);
}

/// <summary>Strict readers for the identifiers of the PR check tools; GitHub run and job ids exceed the 32-bit range.</summary>
internal static class PrCheckJson
{
    public static int? PositiveInt32(JsonElement root, string name) =>
        PositiveInt64(root, name) is { } value
            ? value <= int.MaxValue ? (int)value : throw new ArgumentException($"'{name}' is too large for a pull request number.")
            : null;

    public static long? PositiveInt64(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) || number <= 0)
        {
            throw new ArgumentException($"'{name}' must be a positive integer.");
        }

        return number;
    }

    public static long RequiredPositiveInt64(JsonElement root, string name) =>
        PositiveInt64(root, name) ?? throw new ArgumentException($"'{name}' is required and must be a positive integer.");
}
