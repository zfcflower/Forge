using System.Text;
using Forge.Abstractions;

namespace Forge.Core.Tools;

/// <summary>
/// <c>extension_create</c> and <c>extension_update</c>: stage one draft and wait for the user's decision. The tool can
/// never make an extension live by itself — <see cref="IExtensionAuthoring.SubmitAsync"/> owns the approval card and the
/// commit in the app — so the only way past this tool is a user decision, and the result text says what that decision was.
/// </summary>
public sealed class ExtensionSubmitTool : ITool
{
    private const string SchemaJson =
        """
        {
            "type": "object",
            "properties": {
                "id": {"type": "string", "description": "Stable extension id; it names the plugin directory and is also the id the user sees."},
                "version": {"type": "string", "description": "Version of this draft. A deleted id and version stay blocked, so a re-created extension needs a new version."},
                "kind": {"type": "string", "enum": ["plugin", "workflow"], "description": "plugin = C# compiled and run in a worker; workflow = declarative workflow.json run by the host."},
                "files": {"type": "object", "description": "Relative '/'-separated path -> full file text. plugin: exactly one .cs file. workflow: workflow.json. Never manifest.json, which is generated from these fields.", "additionalProperties": {"type": "string"}},
                "permissions": {
                    "type": "array",
                    "description": "Permissions the extension declares. They appear on the approval card and are covered by the hashes the user approves.",
                    "items": {
                        "type": "object",
                        "properties": {
                            "kind": {"type": "string", "enum": ["ReadFile", "WriteFile", "ExecuteCommand", "Network", "Database"]},
                            "scope": {"type": "string", "description": "Optional path, host or database name the permission is limited to."}
                        },
                        "required": ["kind"],
                        "additionalProperties": false
                    }
                },
                "dependencies": {
                    "type": "array",
                    "description": "NuGet-style dependencies the host resolves for a plugin; any change needs a new approval.",
                    "items": {
                        "type": "object",
                        "properties": {
                            "name": {"type": "string"},
                            "version": {"type": "string"}
                        },
                        "required": ["name", "version"],
                        "additionalProperties": false
                    }
                },
                "tool": {
                    "type": "object",
                    "description": "workflow only: the single tool the workflow exposes. Omit it for a plugin, which declares its tools in code.",
                    "properties": {
                        "name": {"type": "string", "description": "Tool name the model will call."},
                        "description": {"type": "string", "description": "What the tool does, shown to the model."},
                        "input_schema": {"type": "object", "description": "JSON Schema of the tool's arguments; the call arguments are bound as $input inside the workflow."}
                    },
                    "required": ["name", "description", "input_schema"],
                    "additionalProperties": false
                }
            },
            "required": ["id", "version", "kind", "files"],
            "additionalProperties": false
        }
        """;

    private const string CreateIntro =
        "Create an extension that adds new tools to the session. The call stages the draft and then waits for the user to approve or reject it in the approval queue, so a slow call is normal; it returns only after that decision. Approved: the extension is live and its tools can be called from your next model request. Rejected: the draft is discarded and any live version stays exactly as it was. Never claim an approval the result did not report. The manifest is generated from id, version, kind, permissions and dependencies, so never send manifest.json. A deleted id and version stay blocked; submit a new version instead.";

    private const string UpdateIntro =
        "Replace an existing extension with a new version. The call stages the draft and then waits for the user to approve or reject it: until that approval the live version keeps running, and a rejection changes nothing at all. Same arguments and file format as extension_create, and the id must already exist — use extension_list when you are not sure. Never claim an approval the result did not report. A deleted id and version stay blocked; submit a new version instead.";

    private const string PluginFormat =
        "plugin: files carries exactly one .cs file. The plugin is a public class implementing Forge.Plugin.Sdk.IPlugin with a public parameterless constructor: Info returns a Forge.Plugin.Sdk.PluginInfo (id, version, description), and Register(IRegistrationContext context) adds its tools with context.AddTool and its permissions with context.RequirePermission. A plugin takes no tool argument.";

    private const string WorkflowFormat =
        """
        workflow: files carries workflow.json and tool describes the one tool the workflow exposes. workflow.json is {"version":1,"steps":[{"id":"fetch","tool":"read_file","args":{"path":"README.md"},"after":[]}]}. Every step calls a registered tool. A step without after runs right after the previous step, after:[] starts in parallel, and a list waits for exactly those step ids. Step arguments can bind earlier output with {"$ref":"$.steps.<step-id>.json.field"} (also .content, .isError and .status) and the arguments of this tool's own call with {"$ref":"$input.field"}.
        """;

    private const string DraftFormat = "\n\n" + PluginFormat + "\n\n" + WorkflowFormat;

    private const string CreateDescription = CreateIntro + DraftFormat;

    private const string UpdateDescription = UpdateIntro + DraftFormat;

    private readonly IExtensionAuthoring authoring;
    private readonly bool isUpdate;

    public ExtensionSubmitTool(IExtensionAuthoring authoring, bool isUpdate)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        this.authoring = authoring;
        this.isUpdate = isUpdate;
        Definition = new ToolDefinition(
            isUpdate ? ExtensionToolNames.Update : ExtensionToolNames.Create,
            isUpdate ? UpdateDescription : CreateDescription,
            SchemaJson);
    }

    public ToolDefinition Definition { get; }

    public ToolSideEffect SideEffect => ToolSideEffect.Write;

    public async ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(context);

        if (ExtensionTools.RefuseSubAgent(context, Definition.Name) is { } refusal)
        {
            return refusal;
        }

        var draft = ExtensionDraftReader.Read(call.ArgumentsJson, isUpdate, out var error);
        if (draft is null)
        {
            return new ToolResult($"{Definition.Name}: {error}", IsError: true);
        }

        ExtensionSubmitResult result;
        try
        {
            result = await authoring.SubmitAsync(draft, context, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ToolResult($"{Definition.Name} failed: {exception.Message}", IsError: true);
        }

        return Report(draft, result);
    }

    private ToolResult Report(ExtensionDraft draft, ExtensionSubmitResult result)
    {
        switch (result.Outcome)
        {
            case ExtensionSubmitOutcome.Approved:
            {
                var builder = new StringBuilder(192);
                builder.Append("Approved: extension '").Append(draft.Id).Append("' version '").Append(draft.Version)
                    .Append("' is live");
                if (result.ToolNames.Count == 0)
                {
                    builder.Append(" and registered no tool (see extension_list)");
                }
                else
                {
                    builder.Append(" and registered ");
                    AppendNames(builder, result.ToolNames);
                }

                builder.Append(". Its tools can be called from your next model request.");
                return new ToolResult(Append(builder, result.Message));
            }

            case ExtensionSubmitOutcome.Rejected:
            {
                var builder = new StringBuilder(192);
                builder.Append("The user rejected extension '").Append(draft.Id).Append("' version '").Append(draft.Version)
                    .Append("'. The draft was discarded and any live version is unchanged. ")
                    .Append("Do not resend the same draft: ask the user what to change first.");
                return new ToolResult(Append(builder, result.Message));
            }

            case ExtensionSubmitOutcome.Withdrawn:
            {
                var builder = new StringBuilder(192);
                builder.Append("The approval for extension '").Append(draft.Id).Append("' version '").Append(draft.Version)
                    .Append("' was withdrawn before the user decided. This is not a rejection: the draft is kept pending ")
                    .Append("and the user can still decide later; do not resend the same draft.");
                return new ToolResult(Append(builder, result.Message));
            }

            case ExtensionSubmitOutcome.Invalid:
                var reason = string.IsNullOrWhiteSpace(result.Message)
                    ? $"extension '{draft.Id}' version '{draft.Version}' was not accepted and no reason was reported."
                    : result.Message;
                return new ToolResult(reason, IsError: true);

            default:
                return new ToolResult(
                    $"extension '{draft.Id}' version '{draft.Version}': the host returned the unknown outcome '{result.Outcome}'.",
                    IsError: true);
        }
    }

    private static string Append(StringBuilder builder, string? detail) =>
        string.IsNullOrWhiteSpace(detail) ? builder.ToString() : builder.Append('\n').Append(detail).ToString();

    private static void AppendNames(StringBuilder builder, IReadOnlyList<string> names)
    {
        for (var index = 0; index < names.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(index == names.Count - 1 ? " and " : ", ");
            }

            builder.Append('\'').Append(names[index]).Append('\'');
        }
    }
}
