using Forge.Abstractions;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.HostClient;

/// <summary>An <see cref="ICommand"/> contributed by a plugin. Executing it runs the command body in that plugin's
/// worker over IPC after the main program's approval path allowed a <see cref="ToolSideEffect.Execute"/> call; no
/// plugin code runs in the main program.</summary>
public sealed class ExtensionHostCommand : ICommand
{
    private readonly ExtensionHostClient client;
    private readonly CommandDefinition definition;

    internal ExtensionHostCommand(ExtensionHostClient client, string pluginId, Sdk.CommandDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(declaration);
        this.client = client;
        PluginId = pluginId;
        Declaration = declaration;
        definition = ContributionSnapshots.ToCommandDefinition(declaration);
    }

    public string PluginId { get; }

    public Sdk.CommandDeclaration Declaration { get; }

    public CommandDefinition Definition => definition;

    public ValueTask<CommandResult> ExecuteAsync(string arguments, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        return client.InvokeCommandAsync(this, arguments ?? string.Empty, context, ct);
    }

    public override string ToString() => $"{PluginId}:{Definition.Name}";
}
