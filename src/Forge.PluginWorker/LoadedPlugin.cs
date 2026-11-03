using System.Collections.Frozen;
using Forge.Plugin.Sdk;
using Forge.Scripting;

namespace Forge.PluginWorker;

/// <summary>One loaded plugin: its entry instance, its declared tools and the collectible load context
/// they run in. <see cref="Detach"/> drops every reference so the context can be collected.</summary>
internal sealed class LoadedPlugin
{
    private static readonly FrozenDictionary<string, IPluginTool> NoTools = FrozenDictionary<string, IPluginTool>.Empty;

    private IPlugin? plugin;
    private FrozenDictionary<string, IPluginTool> tools;

    private LoadedPlugin(
        string pluginId,
        string version,
        string assemblyPath,
        PluginLoadContext context,
        IPlugin plugin,
        FrozenDictionary<string, IPluginTool> tools,
        ToolDeclaration[] declarations)
    {
        PluginId = pluginId;
        Version = version;
        AssemblyPath = assemblyPath;
        Context = context;
        this.plugin = plugin;
        this.tools = tools;
        Declarations = declarations;
    }

    public string PluginId { get; }

    public string Version { get; }

    public string AssemblyPath { get; }

    public PluginLoadContext Context { get; }

    public IPlugin? Plugin => Volatile.Read(ref plugin);

    public FrozenDictionary<string, IPluginTool> Tools => Volatile.Read(ref tools);

    /// <summary>Tool declarations as sent to the host; they outlive the plugin after unload.</summary>
    public ToolDeclaration[] Declarations { get; }

    public static LoadedPlugin Create(
        string pluginId,
        string version,
        string assemblyPath,
        PluginLoadContext context,
        IPlugin plugin,
        IReadOnlyList<IPluginTool> tools)
    {
        var byName = new Dictionary<string, IPluginTool>(tools.Count, StringComparer.Ordinal);
        var declarations = new ToolDeclaration[tools.Count];
        for (var index = 0; index < tools.Count; index++)
        {
            var tool = tools[index];
            var declaration = tool.Declaration;
            byName.Add(declaration.Name, tool);
            declarations[index] = declaration;
        }

        return new LoadedPlugin(
            pluginId,
            version,
            assemblyPath,
            context,
            plugin,
            byName.ToFrozenDictionary(StringComparer.Ordinal),
            declarations);
    }

    /// <summary>Drops the plugin instance and its tools; the load context stays reachable through
    /// <see cref="Context"/> only.</summary>
    public void Detach()
    {
        Volatile.Write(ref plugin, null);
        Volatile.Write(ref tools, NoTools);
    }
}

/// <summary>Collects the plugin's registrations. The worker serves tools; commands, workflows, panels
/// and permissions are recorded by the Extension Host and main program, not here.</summary>
internal sealed class RegistrationRecorder : IRegistrationContext
{
    private readonly List<IPluginTool> tools = [];
    private readonly HashSet<string> names = new(StringComparer.Ordinal);

    public IReadOnlyList<IPluginTool> Tools => tools;

    public void AddTool(IPluginTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var declaration = tool.Declaration ??
            throw new PluginLoadException("A registered tool returned no declaration.");
        if (string.IsNullOrWhiteSpace(declaration.Name))
        {
            throw new PluginLoadException("A registered tool has an empty name.");
        }

        if (!names.Add(declaration.Name))
        {
            throw new PluginLoadException($"The plugin registers more than one tool named '{declaration.Name}'.");
        }

        tools.Add(tool);
    }

    public void AddCommand(CommandDeclaration command)
    {
    }

    public void AddWorkflow(WorkflowDeclaration workflow)
    {
    }

    public void AddPanel(PanelDeclaration panel)
    {
    }

    public void RequirePermission(Permission permission)
    {
    }
}
