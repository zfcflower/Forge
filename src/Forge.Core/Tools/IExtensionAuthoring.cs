using Forge.Abstractions;

namespace Forge.Core.Tools;

/// <summary>Extension forms an agent can author: <c>plugin</c> (C#, L2) or <c>workflow</c> (declarative JSON, L0).</summary>
public static class ExtensionDraftKinds
{
    public const string Plugin = "plugin";
    public const string Workflow = "workflow";
}

/// <summary>A permission the extension asks for; <see cref="Kind"/> is a <c>PermissionKind</c> name such as <c>ReadFile</c>.</summary>
public sealed record ExtensionPermissionDraft(string Kind, string? Scope = null);

public sealed record ExtensionDependencyDraft(string Name, string Version);

/// <summary>The one tool a workflow extension exposes; its arguments reach the workflow as <c>$input</c>.</summary>
public sealed record ExtensionToolDraft(string Name, string Description, string InputSchemaJson);

/// <summary>
/// What <c>extension_create</c>/<c>extension_update</c> submit. <see cref="Files"/> maps relative paths to full text; the
/// manifest is generated from the structured fields, never written by the agent. <see cref="IsUpdate"/> states the agent's
/// intent: create fails when the id already exists, update fails when it does not.
/// </summary>
public sealed record ExtensionDraft(
    string Id,
    string Version,
    string Kind,
    IReadOnlyDictionary<string, string> Files,
    IReadOnlyList<ExtensionPermissionDraft> Permissions,
    IReadOnlyList<ExtensionDependencyDraft> Dependencies,
    ExtensionToolDraft? Tool,
    bool IsUpdate);

public enum ExtensionSubmitOutcome
{
    /// <summary>The user approved; the extension is live and its tools are registered.</summary>
    Approved,

    /// <summary>The user rejected; the draft was discarded and any live version is unchanged.</summary>
    Rejected,

    /// <summary>The draft never reached the user: validation or compile errors, a blocked id/version, or a host failure.</summary>
    Invalid,
}

/// <summary><see cref="Message"/> is model-facing text: the reason for a rejection or the diagnostics of an invalid draft.</summary>
public sealed record ExtensionSubmitResult(ExtensionSubmitOutcome Outcome, string Message, IReadOnlyList<string> ToolNames);

/// <summary>One installed or staged extension as <c>extension_list</c> reports it.</summary>
public sealed record ExtensionSummary(
    string Id,
    string Version,
    string Status,
    IReadOnlyList<string> ToolNames,
    bool HasPendingDraft,
    string? Error = null);

/// <summary>
/// The only extension capability Core holds. Implemented by the app: <see cref="SubmitAsync"/> stages the draft, puts an
/// approval card in front of the user and waits for the decision; approving and committing are never reachable from Core,
/// so no agent can make an extension live without the user. Cancellation of <see cref="SubmitAsync"/> withdraws the wait
/// but leaves the draft pending for the user to decide later.
/// </summary>
public interface IExtensionAuthoring
{
    Task<IReadOnlyList<ExtensionSummary>> ListAsync(CancellationToken ct);

    Task<ExtensionSubmitResult> SubmitAsync(ExtensionDraft draft, ToolContext context, CancellationToken ct);

    /// <summary>Returns model-facing text describing the result; failures are text too, not exceptions.</summary>
    Task<string> DisableAsync(string id, string version, CancellationToken ct);

    /// <summary>Returns model-facing text. The confirmation happens before this call, as the tool's own approval.</summary>
    Task<string> DeleteAsync(string id, string version, CancellationToken ct);
}

/// <summary>Names of the extension tools; the app's approval policy allows all but <see cref="Delete"/> without asking.</summary>
public static class ExtensionToolNames
{
    public const string List = "extension_list";
    public const string Create = "extension_create";
    public const string Update = "extension_update";
    public const string Disable = "extension_disable";
    public const string Delete = "extension_delete";

    /// <summary>Tools that only stage or switch off; the real gate is the plugin approval card, so they need no tool-call approval.</summary>
    public static IReadOnlyList<string> AllowedWithoutAsking { get; } = [List, Create, Update, Disable];
}

/// <summary>Factory for the five extension tools. They are for the root agent only: no built-in agent lists them.</summary>
public static class ExtensionTools
{
    /// <summary>
    /// Builds the five tools over one <paramref name="authoring"/> implementation. They are created once per host and
    /// registered as external tools; nothing here can approve an extension without the user.
    /// </summary>
    public static IReadOnlyList<ITool> Create(IExtensionAuthoring authoring)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        return
        [
            new ExtensionListTool(authoring),
            new ExtensionSubmitTool(authoring, isUpdate: false),
            new ExtensionSubmitTool(authoring, isUpdate: true),
            new ExtensionDisableTool(authoring),
            new ExtensionDeleteTool(authoring),
        ];
    }
}
