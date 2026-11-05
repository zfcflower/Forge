using System.Collections.Frozen;
using System.Text.Json;
using Forge.Plugin.Sdk;
using Forge.Scripting;

namespace Forge.PluginWorker;

/// <summary>One loaded plugin: its entry instance, the invocation targets the plugin registered (tools, executable
/// commands, prompt contributors) and the collectible load context they run in. <see cref="Contributions"/> is the
/// complete declaration snapshot the host publishes. <see cref="Detach"/> drops every reference so the context can be
/// collected and nothing the plugin registered can be invoked after unload.</summary>
internal sealed class LoadedPlugin
{
    private static readonly FrozenDictionary<string, IPluginTool> NoTools = FrozenDictionary<string, IPluginTool>.Empty;
    private static readonly FrozenDictionary<string, IPluginCommand> NoCommands = FrozenDictionary<string, IPluginCommand>.Empty;
    private static readonly FrozenDictionary<string, IPluginPromptContributor> NoPrompts =
        FrozenDictionary<string, IPluginPromptContributor>.Empty;

    private IPlugin? plugin;
    private FrozenDictionary<string, IPluginTool> tools;
    private FrozenDictionary<string, IPluginCommand> commands;
    private FrozenDictionary<string, IPluginPromptContributor> prompts;

    private LoadedPlugin(
        string pluginId,
        string version,
        string assemblyPath,
        PluginLoadContext context,
        IPlugin plugin,
        PluginContributionSet contributions,
        FrozenDictionary<string, IPluginTool> tools,
        FrozenDictionary<string, IPluginCommand> commands,
        FrozenDictionary<string, IPluginPromptContributor> prompts)
    {
        PluginId = pluginId;
        Version = version;
        AssemblyPath = assemblyPath;
        Context = context;
        this.plugin = plugin;
        Contributions = contributions;
        this.tools = tools;
        this.commands = commands;
        this.prompts = prompts;
    }

    public string PluginId { get; }

    public string Version { get; }

    public string AssemblyPath { get; }

    public PluginLoadContext Context { get; }

    public IPlugin? Plugin => Volatile.Read(ref plugin);

    public FrozenDictionary<string, IPluginTool> Tools => Volatile.Read(ref tools);

    public FrozenDictionary<string, IPluginCommand> Commands => Volatile.Read(ref commands);

    public FrozenDictionary<string, IPluginPromptContributor> Prompts => Volatile.Read(ref prompts);

    /// <summary>Everything the plugin declared, as sent to the host in <c>worker.load</c>.</summary>
    public PluginContributionSet Contributions { get; }

    public static LoadedPlugin Create(
        string pluginId,
        string version,
        string assemblyPath,
        PluginLoadContext context,
        IPlugin plugin,
        RegistrationRecorder recorder)
    {
        var tools = new Dictionary<string, IPluginTool>(recorder.Tools.Count, StringComparer.Ordinal);
        foreach (var tool in recorder.Tools)
        {
            tools.Add(tool.Declaration.Name, tool);
        }

        return new LoadedPlugin(
            pluginId,
            version,
            assemblyPath,
            context,
            plugin,
            recorder.BuildContributions(),
            tools.ToFrozenDictionary(StringComparer.Ordinal),
            recorder.Commands.ToFrozenDictionary(StringComparer.Ordinal),
            recorder.Prompts.ToFrozenDictionary(StringComparer.Ordinal));
    }

    /// <summary>Drops the plugin instance, its invocation targets and its declaration set; the load context stays
    /// reachable through <see cref="Context"/> only.</summary>
    public void Detach()
    {
        Volatile.Write(ref plugin, null);
        Volatile.Write(ref tools, NoTools);
        Volatile.Write(ref commands, NoCommands);
        Volatile.Write(ref prompts, NoPrompts);
    }
}

/// <summary>
/// Collects and validates everything a plugin registers. Every declaration is checked here - names, duplicates, enum
/// values and the reserved <c>run_code</c> identity - because the host must never publish a contribution it cannot run:
/// a malformed set fails the load with the offending item named instead of silently dropping it.
/// </summary>
internal sealed class RegistrationRecorder(string pluginId) : IRegistrationContext
{
    /// <summary>The main program's own script tool; a plugin may not shadow it with a tool or command.</summary>
    public const string ReservedIdentity = "run_code";

    private static readonly HashSet<string> PanelKinds = new(StringComparer.Ordinal)
    {
        "form",
        "table",
        "markdown",
        "text",
        "button",
        "status",
    };

    private readonly List<IPluginTool> tools = [];
    private readonly HashSet<string> toolNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IPluginCommand> commands = new(StringComparer.Ordinal);
    private readonly List<CommandDeclaration> commandDeclarations = [];
    private readonly List<WorkflowDeclaration> workflows = [];
    private readonly HashSet<string> workflowNames = new(StringComparer.Ordinal);
    private readonly List<AgentDeclaration> agents = [];
    private readonly HashSet<string> agentNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IPluginPromptContributor> prompts = new(StringComparer.Ordinal);
    private readonly List<PromptContributorDeclaration> promptDeclarations = [];
    private readonly List<PanelDeclaration> panels = [];
    private readonly HashSet<string> panelIds = new(StringComparer.Ordinal);
    private readonly List<Permission> permissions = [];
    private readonly HashSet<(PermissionKind Kind, string Scope)> permissionKeys = [];

    public IReadOnlyList<IPluginTool> Tools => tools;

    public IReadOnlyDictionary<string, IPluginCommand> Commands => commands;

    public IReadOnlyDictionary<string, IPluginPromptContributor> Prompts => prompts;

    public IReadOnlyList<Permission> Permissions => permissions;

    public void AddTool(IPluginTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var declaration = tool.Declaration ??
            throw new PluginLoadException("A registered tool returned no declaration.");
        var name = declaration.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new PluginLoadException("A registered tool has an empty name.");
        }

        if (string.Equals(name, ReservedIdentity, StringComparison.Ordinal))
        {
            throw new PluginLoadException(
                $"A plugin may not register a tool named '{ReservedIdentity}': that identity belongs to the main program's script tool.");
        }

        if (!toolNames.Add(name))
        {
            throw new PluginLoadException($"The plugin registers more than one tool named '{name}'.");
        }

        if (!Enum.IsDefined(declaration.SideEffect))
        {
            throw new PluginLoadException($"Tool '{name}' declares an undefined side effect value '{declaration.SideEffect}'.");
        }

        tools.Add(tool);
    }

    /// <summary>Metadata-only command registration: a command needs an executable body, so this fails the load instead
    /// of publishing a command that would do nothing when the user runs it.</summary>
    public void AddCommand(CommandDeclaration command)
    {
        ArgumentNullException.ThrowIfNull(command);
        throw new PluginLoadException(
            $"Plugin '{pluginId}' declares command '{command.Name}' without an executable body. Implement "
            + $"Forge.Plugin.Sdk.IPluginCommand and register it with AddCommand(IPluginCommand); a metadata-only command is refused "
            + "instead of being published as a command that cannot run.");
    }

    public void AddCommand(IPluginCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var declaration = command.Declaration ??
            throw new PluginLoadException("A registered command returned no declaration.");
        var name = declaration.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new PluginLoadException("A registered command has an empty name.");
        }

        if (string.Equals(name, ReservedIdentity, StringComparison.Ordinal))
        {
            throw new PluginLoadException(
                $"A plugin may not register a command named '{ReservedIdentity}': that identity belongs to the main program's script tool.");
        }

        if (!commands.TryAdd(name, command))
        {
            throw new PluginLoadException($"The plugin registers more than one command named '{name}'.");
        }

        commandDeclarations.Add(new CommandDeclaration(name, declaration.Description ?? string.Empty, declaration.ArgumentHint ?? string.Empty));
    }

    public void AddWorkflow(WorkflowDeclaration workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var name = workflow.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new PluginLoadException("A registered workflow has an empty name.");
        }

        if (string.IsNullOrWhiteSpace(workflow.Body))
        {
            throw new PluginLoadException(
                $"Workflow '{name}' has no body. A workflow is a Markdown skill (frontmatter and step text), so an empty body cannot be published.");
        }

        if (!workflowNames.Add(name))
        {
            throw new PluginLoadException($"The plugin registers more than one workflow named '{name}'.");
        }

        workflows.Add(new WorkflowDeclaration(name, workflow.Description ?? string.Empty, workflow.Body));
    }

    public void AddAgent(AgentDeclaration agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var name = agent.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new PluginLoadException("A registered agent has an empty name.");
        }

        if (string.IsNullOrWhiteSpace(agent.Prompt))
        {
            throw new PluginLoadException($"Agent '{name}' has no prompt.");
        }

        if (agent.Model is not { } model || string.IsNullOrWhiteSpace(model.Model))
        {
            throw new PluginLoadException($"Agent '{name}' declares no model.");
        }

        if (!Enum.IsDefined(model.Thinking))
        {
            throw new PluginLoadException($"Agent '{name}' declares an undefined thinking effort '{model.Thinking}'.");
        }

        if (model.MaxOutputTokens is <= 0)
        {
            throw new PluginLoadException($"Agent '{name}' declares maxOutputTokens {model.MaxOutputTokens}; it must be positive when set.");
        }

        if (agent.ToolNames is { } names)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var toolName in names)
            {
                if (string.IsNullOrWhiteSpace(toolName))
                {
                    throw new PluginLoadException($"Agent '{name}' lists a tool name that is blank.");
                }

                if (!seen.Add(toolName))
                {
                    throw new PluginLoadException($"Agent '{name}' lists tool '{toolName}' twice.");
                }
            }
        }

        if (!agentNames.Add(name))
        {
            throw new PluginLoadException($"The plugin registers more than one agent named '{name}'.");
        }

        agents.Add(agent with
        {
            Description = agent.Description ?? string.Empty,
            Prompt = agent.Prompt,
            Model = model,
            ToolNames = agent.ToolNames is null ? null : [.. agent.ToolNames],
        });
    }

    public void AddPromptContributor(IPluginPromptContributor contributor)
    {
        ArgumentNullException.ThrowIfNull(contributor);
        var declaration = contributor.Declaration ??
            throw new PluginLoadException("A registered prompt contributor returned no declaration.");
        var id = declaration.Id;
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new PluginLoadException("A registered prompt contributor has an empty id.");
        }

        if (!prompts.TryAdd(id, contributor))
        {
            throw new PluginLoadException($"The plugin registers more than one prompt contributor with id '{id}'.");
        }

        promptDeclarations.Add(new PromptContributorDeclaration(id));
    }

    /// <summary>Panel contributions are declaration-only in this stage: the declaration is recorded and validated, no
    /// control is instantiated and no rendering code exists in the worker.</summary>
    public void AddPanel(PanelDeclaration panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        if (string.IsNullOrWhiteSpace(panel.Id))
        {
            throw new PluginLoadException("A registered panel has an empty id.");
        }

        if (string.IsNullOrWhiteSpace(panel.Title))
        {
            throw new PluginLoadException($"Panel '{panel.Id}' has no title.");
        }

        if (panel.Schema is not { } schema || schema.Kind is null || !PanelKinds.Contains(schema.Kind))
        {
            throw new PluginLoadException(
                $"Panel '{panel.Id}' declares schema kind '{panel.Schema?.Kind}', which is not one of {string.Join(", ", PanelKinds)}.");
        }

        if (schema.Definition.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new PluginLoadException($"Panel '{panel.Id}' declares no schema definition.");
        }

        if (!panelIds.Add(panel.Id))
        {
            throw new PluginLoadException($"The plugin registers more than one panel with id '{panel.Id}'.");
        }

        panels.Add(panel);
    }

    public void RequirePermission(Permission permission)
    {
        ArgumentNullException.ThrowIfNull(permission);
        if (!Enum.IsDefined(permission.Kind))
        {
            throw new PluginLoadException($"The plugin requires an undefined permission kind '{permission.Kind}'.");
        }

        var scope = permission.Scope?.Trim();
        if (scope is { Length: 0 })
        {
            throw new PluginLoadException($"The plugin requires permission '{permission.Kind}' with an empty scope.");
        }

        if (!permissionKeys.Add((permission.Kind, scope ?? string.Empty)))
        {
            return;
        }

        permissions.Add(new Permission(permission.Kind, scope));
    }

    /// <summary>The complete declaration snapshot; the host publishes it as one replacement registration.</summary>
    public PluginContributionSet BuildContributions()
    {
        var declarations = new ToolDeclaration[tools.Count];
        for (var index = 0; index < tools.Count; index++)
        {
            declarations[index] = tools[index].Declaration;
        }

        return new PluginContributionSet
        {
            Tools = declarations,
            Commands = [.. commandDeclarations],
            Workflows = [.. workflows],
            Agents = [.. agents],
            PromptContributors = [.. promptDeclarations],
            Panels = [.. panels],
            Permissions = [.. permissions],
        };
    }
}
