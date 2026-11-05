using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Plugin.Sdk;

/// <summary>Entry point of a user plugin. Instances are created per plugin in the Worker process and
/// must have a public parameterless constructor.</summary>
public interface IPlugin
{
    PluginInfo Info { get; }
    void Register(IRegistrationContext context);
}

public sealed record PluginInfo(string Id, string Version, string Description = "");

/// <summary>Collects what a plugin contributes. Registration happens once at load; the host records
/// the declarations and drives execution later through IPC.</summary>
public interface IRegistrationContext
{
    void AddTool(IPluginTool tool);
    void AddCommand(CommandDeclaration command);
    void AddWorkflow(WorkflowDeclaration workflow);
    void AddPanel(PanelDeclaration panel);
    void RequirePermission(Permission permission);
    void AddCommand(IPluginCommand command) => throw new NotSupportedException("Executable commands are not supported by this registration context.");
    void AddAgent(AgentDeclaration agent) => throw new NotSupportedException("Agent declarations are not supported by this registration context.");
    void AddPromptContributor(IPluginPromptContributor contributor) => throw new NotSupportedException("Prompt contributors are not supported by this registration context.");
}

[JsonSerializable(typeof(PluginInfo))]
[JsonSerializable(typeof(ToolDeclaration))]
[JsonSerializable(typeof(CommandDeclaration))]
[JsonSerializable(typeof(WorkflowDeclaration))]
[JsonSerializable(typeof(PanelDeclaration))]
[JsonSerializable(typeof(UiSchema))]
[JsonSerializable(typeof(Permission))]
public partial class PluginSdkJsonContext : JsonSerializerContext
{
}
