using AbstractionsToolResult = Forge.Abstractions.ToolResult;
using AbstractionsToolSideEffect = Forge.Abstractions.ToolSideEffect;
using SdkToolDeclaration = Forge.Plugin.Sdk.ToolDeclaration;

namespace Forge.HostClient;

/// <summary>An <see cref="Forge.Abstractions.ITool"/> that forwards every call to the plugin tool of the
/// Extension Host over IPC. One instance exists per declaration; re-registration replaces it.</summary>
public sealed class ExtensionHostTool : Forge.Abstractions.ITool
{
    private readonly ExtensionHostClient client;
    private readonly Forge.Abstractions.ToolDefinition definition;
    private readonly AbstractionsToolSideEffect sideEffect;

    internal ExtensionHostTool(ExtensionHostClient client, string pluginId, SdkToolDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(declaration);
        this.client = client;
        PluginId = pluginId;
        Declaration = declaration;
        var schema = string.IsNullOrWhiteSpace(declaration.InputSchemaJson) ? "{}" : declaration.InputSchemaJson;
        definition = new Forge.Abstractions.ToolDefinition(declaration.Name, declaration.Description, schema);
        sideEffect = HostProtocolJson.ToAbstractionsSideEffect(declaration.SideEffect);
    }

    public string PluginId { get; }

    public SdkToolDeclaration Declaration { get; }

    public Forge.Abstractions.ToolDefinition Definition => definition;

    public AbstractionsToolSideEffect SideEffect => sideEffect;

    public ValueTask<AbstractionsToolResult> InvokeAsync(
        Forge.Abstractions.ToolCall call, Forge.Abstractions.ToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(context);
        return client.InvokePluginToolAsync(PluginId, Declaration.Name, call, context, ct);
    }

    public override string ToString() => $"{PluginId}:{Definition.Name}";
}
