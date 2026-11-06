using System.Text;
using Forge.Abstractions;

namespace Forge.Core.Tools;

/// <summary>
/// <c>extension_list</c>: the installed extensions and the drafts still waiting for approval. Read-only, so it runs in a
/// read-only batch and needs no approval; it is the only way an agent learns the exact id and version to update, disable
/// or delete.
/// </summary>
public sealed class ExtensionListTool : ITool
{
    public const string Name = ExtensionToolNames.List;

    private const string SchemaJson =
        """{"type":"object","properties":{},"additionalProperties":false}""";

    private const string Description =
        "List the installed extensions with their status, registered tool names, whether a draft of theirs is waiting for approval, and their last error. Use it to find the exact id and version that extension_update, extension_disable and extension_delete take. Read-only and needs no approval.";

    private readonly IExtensionAuthoring authoring;

    public ExtensionListTool(IExtensionAuthoring authoring)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        this.authoring = authoring;
        Definition = new ToolDefinition(Name, Description, SchemaJson);
    }

    public ToolDefinition Definition { get; }

    public ToolSideEffect SideEffect => ToolSideEffect.ReadOnly;

    public async ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(context);

        if (ExtensionTools.RefuseSubAgent(context, Definition.Name) is { } refusal)
        {
            return refusal;
        }

        IReadOnlyList<ExtensionSummary> items;
        try
        {
            items = await authoring.ListAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ToolResult($"{Name} failed: {exception.Message}", IsError: true);
        }

        if (items.Count == 0)
        {
            return new ToolResult("No extensions are installed and no draft is waiting for approval.");
        }

        var pending = 0;
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index].HasPendingDraft)
            {
                pending++;
            }
        }

        var builder = new StringBuilder(items.Count * 96 + 64);
        builder.Append(items.Count).Append(items.Count == 1 ? " extension" : " extensions");
        if (pending > 0)
        {
            builder.Append(", ").Append(pending).Append(pending == 1 ? " with a pending draft" : " with pending drafts");
        }

        builder.Append(":\n");
        for (var index = 0; index < items.Count; index++)
        {
            Append(builder, items[index]);
            builder.Append('\n');
        }

        return new ToolResult(builder.ToString().TrimEnd('\n'));
    }

    private static void Append(StringBuilder builder, ExtensionSummary item)
    {
        builder.Append("- ").Append(item.Id).Append(' ').Append(item.Version)
            .Append(" [").Append(item.Status).Append("] tools: ");
        if (item.ToolNames.Count == 0)
        {
            builder.Append("none");
        }
        else
        {
            for (var index = 0; index < item.ToolNames.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(item.ToolNames[index]);
            }
        }

        if (item.HasPendingDraft)
        {
            builder.Append("; pending draft");
        }

        if (!string.IsNullOrWhiteSpace(item.Error))
        {
            builder.Append("; error: ").Append(item.Error);
        }
    }
}
