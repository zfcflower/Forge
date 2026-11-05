using Forge.Abstractions;

namespace Forge.Core.Tools;

/// <summary>
/// <c>extension_disable</c>: switch an installed version off so its tools stop being callable while the approval is kept.
/// It is a write (it changes what the next model request may call) but the app lets it through without a tool-call card.
/// </summary>
public sealed class ExtensionDisableTool : ITool
{
    public const string Name = ExtensionToolNames.Disable;

    private const string SchemaJson =
        """{"type":"object","properties":{"id":{"type":"string","description":"Extension id from extension_list."},"version":{"type":"string","description":"Installed version from extension_list."}},"required":["id","version"],"additionalProperties":false}""";

    private const string Description =
        "Disable one installed extension version: its tools stop being registered and can no longer be called, and the user can enable it again later from settings. Takes the id and version exactly as extension_list reports them. Needs no approval of its own.";

    private readonly IExtensionAuthoring authoring;

    public ExtensionDisableTool(IExtensionAuthoring authoring)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        this.authoring = authoring;
        Definition = new ToolDefinition(Name, Description, SchemaJson);
    }

    public ToolDefinition Definition { get; }

    public ToolSideEffect SideEffect => ToolSideEffect.Write;

    public async ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(context);

        if (!ExtensionDraftReader.TryReadIdAndVersion(call.ArgumentsJson, out var id, out var version, out var error))
        {
            return new ToolResult($"{Name}: {error}", IsError: true);
        }

        try
        {
            return new ToolResult(await authoring.DisableAsync(id, version, ct).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ToolResult($"{Name} failed: {exception.Message}", IsError: true);
        }
    }
}

/// <summary>
/// <c>extension_delete</c>: remove an installed version for good. The app asks the user to confirm this very call, so it is
/// its own confirmation; afterwards the id and the version stay blocked and a new draft needs a new version.
/// </summary>
public sealed class ExtensionDeleteTool : ITool
{
    public const string Name = ExtensionToolNames.Delete;

    private const string SchemaJson =
        """{"type":"object","properties":{"id":{"type":"string","description":"Extension id from extension_list."},"version":{"type":"string","description":"Installed version from extension_list."}},"required":["id","version"],"additionalProperties":false}""";

    private const string Description =
        "Delete one installed extension version for good: the user is asked to confirm this call, its files are removed and its tools unregister. Afterwards the same id and version stay blocked, so a later draft must use a new version. Only delete when the user asked for it.";

    private readonly IExtensionAuthoring authoring;

    public ExtensionDeleteTool(IExtensionAuthoring authoring)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        this.authoring = authoring;
        Definition = new ToolDefinition(Name, Description, SchemaJson);
    }

    public ToolDefinition Definition { get; }

    public ToolSideEffect SideEffect => ToolSideEffect.Write;

    public async ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(context);

        if (!ExtensionDraftReader.TryReadIdAndVersion(call.ArgumentsJson, out var id, out var version, out var error))
        {
            return new ToolResult($"{Name}: {error}", IsError: true);
        }

        try
        {
            return new ToolResult(await authoring.DeleteAsync(id, version, ct).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ToolResult($"{Name} failed: {exception.Message}", IsError: true);
        }
    }
}
