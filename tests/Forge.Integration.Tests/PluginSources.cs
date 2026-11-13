namespace Forge.Integration.Tests;

/// <summary>C# source of the test plugins. Every plugin exposes the same six tools:
/// <c>echo</c> (returns "&lt;prefix&gt;:&lt;text&gt;"), <c>pid</c> (worker process id), <c>sleep</c> (ignores cancellation for
/// <c>ms</c> milliseconds, writing <c>marker</c> first when given), <c>throw</c>, <c>callHost</c> (calls the host tool
/// <c>target</c> with <c>args</c> through <see cref="Forge.Plugin.Sdk.IHostFacade"/>) and <c>crash</c> (ends the worker).
/// <see cref="Spawnable"/> adds the two tools that start a long-lived child process.</summary>
internal static class PluginSources
{
    public const string EchoTool = "echo";
    public const string PidTool = "pid";
    public const string SleepTool = "sleep";
    public const string ThrowTool = "throw";
    public const string CallHostTool = "callHost";
    public const string CrashTool = "crash";

    public static readonly string[] AllTools = [EchoTool, PidTool, SleepTool, ThrowTool, CallHostTool, CrashTool];

    /// <summary>Tools declared by <see cref="Spawnable"/>.</summary>
    public static readonly string[] SpawnableTools = [EchoTool, PidTool, SpawnTool, SpawnAndCrashTool];

    /// <summary><c>spawn</c>: starts a long-lived child process, writes its pid to the <c>marker</c> argument when it is
    /// non-empty, reports it, and then hangs for <c>ms</c> milliseconds when that argument is positive.</summary>
    public const string SpawnTool = "spawn";

    /// <summary><c>spawnAndCrash</c>: starts that child process and then ends the worker.</summary>
    public const string SpawnAndCrashTool = "spawnAndCrash";

    public static string Standard(
        string id,
        string echoPrefix = "echo",
        string version = "1.0.0",
        string toolPrefix = "") =>
        $$"""
        public sealed class TestPlugin : IPlugin
        {
            public PluginInfo Info => new("{{id}}", "{{version}}", "end-to-end test plugin");

            public void Register(IRegistrationContext context)
            {
                context.AddTool(new EchoTool());
                context.AddTool(new PidTool());
                context.AddTool(new SleepTool());
                context.AddTool(new ThrowTool());
                context.AddTool(new CallHostTool());
                context.AddTool(new CrashTool());
            }

            internal static string Arg(JsonElement args, string name) =>
                args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) ? value.ToString() : "";
        }

        public sealed class EchoTool : IPluginTool
        {
            private const string Prefix = "{{echoPrefix}}";

            public ToolDeclaration Declaration { get; } =
                new("{{toolPrefix}}echo", "Echoes the text argument", "{\"type\":\"object\"}", ToolSideEffect.ReadOnly);

            public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct) =>
                Task.FromResult(new ToolResult(Prefix + ":" + TestPlugin.Arg(invocation.Arguments, "text")));
        }

        public sealed class PidTool : IPluginTool
        {
            public ToolDeclaration Declaration { get; } =
                new("{{toolPrefix}}pid", "Reports the worker process id", "{\"type\":\"object\"}", ToolSideEffect.ReadOnly);

            public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct) =>
                Task.FromResult(new ToolResult(Environment.ProcessId.ToString()));
        }

        public sealed class SleepTool : IPluginTool
        {
            public ToolDeclaration Declaration { get; } =
                new("{{toolPrefix}}sleep", "Sleeps without honoring cancellation", "{\"type\":\"object\"}", ToolSideEffect.ReadOnly);

            public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct)
            {
                var marker = TestPlugin.Arg(invocation.Arguments, "marker");
                if (marker.Length > 0)
                {
                    File.WriteAllText(marker, "started");
                }

                var milliseconds = int.TryParse(TestPlugin.Arg(invocation.Arguments, "ms"), out var parsed) ? parsed : 1000;
                await Task.Delay(milliseconds);
                return new ToolResult("slept:" + milliseconds);
            }
        }

        public sealed class ThrowTool : IPluginTool
        {
            public ToolDeclaration Declaration { get; } =
                new("{{toolPrefix}}throw", "Always throws", "{\"type\":\"object\"}", ToolSideEffect.ReadOnly);

            public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct) =>
                throw new InvalidOperationException("boom:" + TestPlugin.Arg(invocation.Arguments, "text"));
        }

        public sealed class CallHostTool : IPluginTool
        {
            public ToolDeclaration Declaration { get; } =
                new("{{toolPrefix}}callHost", "Calls a main-program tool", "{\"type\":\"object\"}", ToolSideEffect.Execute);

            public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct)
            {
                var target = TestPlugin.Arg(invocation.Arguments, "target");
                var nested = invocation.Arguments.ValueKind == JsonValueKind.Object &&
                    invocation.Arguments.TryGetProperty("args", out var value)
                        ? value
                        : JsonDocument.Parse("{}").RootElement;
                var result = await invocation.Host.CallToolAsync(target, nested, ct);
                return new ToolResult(result.Content, result.IsError);
            }
        }

        public sealed class CrashTool : IPluginTool
        {
            public ToolDeclaration Declaration { get; } =
                new("{{toolPrefix}}crash", "Terminates the worker process", "{\"type\":\"object\"}", ToolSideEffect.Execute);

            public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct)
            {
                Environment.Exit(3);
                return Task.FromResult(new ToolResult("unreachable"));
            }
        }

        """;

    /// <summary>A plugin whose <c>spawn</c> tool starts a long-lived child process (a <c>ping</c> on Windows, a
    /// <c>sleep</c> elsewhere) and reports its pid, and whose <c>spawnAndCrash</c> tool does the same and then ends the
    /// worker. Plugin source is compiled against the BCL subset in <c>ScriptCompiler</c>, which does not reference
    /// <c>System.Diagnostics.Process</c>, so the child is started through reflection.</summary>
    public static string Spawnable(string id, string echoPrefix = "a", string version = "1.0.0") =>
        $$"""
        public sealed class TestPlugin : IPlugin
        {
            public PluginInfo Info => new("{{id}}", "{{version}}", "process-tree test plugin");

            public void Register(IRegistrationContext context)
            {
                context.AddTool(new EchoTool());
                context.AddTool(new PidTool());
                context.AddTool(new SpawnTool());
                context.AddTool(new SpawnAndCrashTool());
            }

            internal static string Arg(JsonElement args, string name) =>
                args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) ? value.ToString() : "";
        }

        public sealed class EchoTool : IPluginTool
        {
            private const string Prefix = "{{echoPrefix}}";

            public ToolDeclaration Declaration { get; } =
                new("echo", "Echoes the text argument", "{\"type\":\"object\"}", ToolSideEffect.ReadOnly);

            public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct) =>
                Task.FromResult(new ToolResult(Prefix + ":" + TestPlugin.Arg(invocation.Arguments, "text")));
        }

        public sealed class PidTool : IPluginTool
        {
            public ToolDeclaration Declaration { get; } =
                new("pid", "Reports the worker process id", "{\"type\":\"object\"}", ToolSideEffect.ReadOnly);

            public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct) =>
                Task.FromResult(new ToolResult(Environment.ProcessId.ToString()));
        }

        public sealed class SpawnTool : IPluginTool
        {
            public ToolDeclaration Declaration { get; } =
                new("spawn", "Starts a long-lived child process and reports its pid", "{\"type\":\"object\"}", ToolSideEffect.Execute);

            public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct)
            {
                var pid = SpawnedChild.Start(TestPlugin.Arg(invocation.Arguments, "marker"));
                var milliseconds = int.TryParse(TestPlugin.Arg(invocation.Arguments, "ms"), out var parsed) ? parsed : 0;
                if (milliseconds > 0)
                {
                    await Task.Delay(milliseconds);
                }

                return new ToolResult(pid.ToString());
            }
        }

        public sealed class SpawnAndCrashTool : IPluginTool
        {
            public ToolDeclaration Declaration { get; } =
                new("spawnAndCrash", "Starts a long-lived child process and then terminates the worker", "{\"type\":\"object\"}", ToolSideEffect.Execute);

            public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct)
            {
                SpawnedChild.Start(TestPlugin.Arg(invocation.Arguments, "marker"));
                Environment.Exit(3);
                return Task.FromResult(new ToolResult("unreachable"));
            }
        }

        internal static class SpawnedChild
        {
            internal static int Start(string marker)
            {
                var processType = Type.GetType("System.Diagnostics.Process, System.Diagnostics.Process", throwOnError: true);
                var startInfoType = Type.GetType("System.Diagnostics.ProcessStartInfo, System.Diagnostics.Process", throwOnError: true);
                var startInfo = Activator.CreateInstance(startInfoType);
                startInfoType.GetProperty("UseShellExecute").SetValue(startInfo, false);
                startInfoType.GetProperty("CreateNoWindow").SetValue(startInfo, true);
                var arguments = (System.Collections.IList)startInfoType.GetProperty("ArgumentList").GetValue(startInfo);
                if (OperatingSystem.IsWindows())
                {
                    startInfoType.GetProperty("FileName").SetValue(startInfo, "ping");
                    arguments.Add("-n");
                    arguments.Add("600");
                    arguments.Add("127.0.0.1");
                }
                else
                {
                    startInfoType.GetProperty("FileName").SetValue(startInfo, "sleep");
                    arguments.Add("600");
                }

                var child = processType.GetMethod("Start", new[] { startInfoType }).Invoke(null, new object[] { startInfo });
                if (child is null)
                {
                    throw new InvalidOperationException("the child process did not start");
                }

                var pid = (int)processType.GetProperty("Id").GetValue(child);
                if (marker.Length > 0)
                {
                    File.WriteAllText(marker, pid.ToString());
                }

                return pid;
            }
        }
        """;

    /// <summary>A plugin whose source does not compile.</summary>
    public static string Broken(string id) =>
        $$"""
        public sealed class TestPlugin : IPlugin
        {
            public PluginInfo Info => new("{{id}}", "1.0.0", "broken on purpose");

            public void Register(IRegistrationContext context)
            {
                context.AddTool(new MissingSemicolonTool())
            }
        }

        public sealed class MissingSemicolonTool : IPluginTool
        {
            public ToolDeclaration Declaration { get; } = new("broken", "never registered", "{}", ToolSideEffect.ReadOnly);

            public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct) => Task.FromResult(new ToolResult("x"));
        }
        """;

    /// <summary>A plugin that contributes every non-UI kind at once: one tool, one executable command (which calls
    /// the main-program tool <c>probe</c>), one Markdown workflow, one agent, one dynamic prompt contributor (which
    /// also calls <c>probe</c>), one panel declaration and one permission. <paramref name="markerPath"/> is appended
    /// from <c>Register</c>, so a test can prove whether registration ran.</summary>
    public static string Contributed(string id, string markerPath, string version = "1.0.0") =>
        $$"""
        public sealed class TestPlugin : IPlugin
        {
            public PluginInfo Info => new("{{id}}", "{{version}}", "non-UI contributions test plugin");

            public void Register(IRegistrationContext context)
            {
                File.AppendAllText(@"{{markerPath}}", "registered\n");
                context.AddTool(new CEchoTool());
                context.AddCommand(new GreetCommand());
                context.AddWorkflow(new WorkflowDeclaration("guide", "Guides the agent", "---\nname: guide\n---\n\n# Steps\n\n1. Do it.\n"));
                context.AddAgent(new AgentDeclaration("helper", "Helps the agent", "You are a helper.", new AgentModelSettings("model-x", AgentThinkingEffort.Low, 128)));
                context.AddPromptContributor(new BriefContributor());
                context.AddPanel(new PanelDeclaration("side", "Side panel", new UiSchema("text", "{\"text\":\"hello\"}")));
                context.RequirePermission(new Permission(PermissionKind.ReadFile));
            }
        }

        public sealed class CEchoTool : IPluginTool
        {
            public ToolDeclaration Declaration { get; } =
                new("cecho", "Echoes the text argument", "{\"type\":\"object\"}", ToolSideEffect.ReadOnly);

            public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct)
            {
                var text = invocation.Arguments.ValueKind == JsonValueKind.Object &&
                    invocation.Arguments.TryGetProperty("text", out var value)
                        ? value.ToString()
                        : "";
                return Task.FromResult(new ToolResult("c:" + text));
            }
        }

        public sealed class GreetCommand : IPluginCommand
        {
            public CommandDeclaration Declaration { get; } = new("greet", "Greets", "<name>");

            public async Task<CommandOutput> ExecuteAsync(CommandInvocation invocation, CancellationToken ct)
            {
                var result = await invocation.Host.CallToolAsync("probe", JsonDocument.Parse("{\"from\":\"command\"}").RootElement, ct);
                return new CommandOutput(Output: "cmd:" + result.Content, IsError: result.IsError);
            }
        }

        public sealed class BriefContributor : IPluginPromptContributor
        {
            public PromptContributorDeclaration Declaration { get; } = new("brief");

            public async Task<string?> GetTextAsync(PromptInvocation invocation, CancellationToken ct)
            {
                var result = await invocation.Host.CallToolAsync("probe", JsonDocument.Parse("{\"from\":\"prompt\"}").RootElement, ct);
                return "prompt:" + result.Content;
            }
        }
        """;

    /// <summary>A plugin that registers a metadata-only command with no executable body.</summary>
    public static string PlainCommand(string id) =>
        $$"""
        public sealed class TestPlugin : IPlugin
        {
            public PluginInfo Info => new("{{id}}", "1.0.0", "metadata-only command test plugin");

            public void Register(IRegistrationContext context)
            {
                context.AddCommand(new CommandDeclaration("dud", "Does nothing", "<x>"));
            }
        }
        """;

    /// <summary>A plugin that requires a permission kind no manifest can declare.</summary>
    public static string BadRuntimePermission(string id) =>
        $$"""
        public sealed class TestPlugin : IPlugin
        {
            public PluginInfo Info => new("{{id}}", "1.0.0", "bad permission test plugin");

            public void Register(IRegistrationContext context)
            {
                context.AddTool(new EchoTool());
                context.RequirePermission(new Permission((PermissionKind)99));
            }
        }

        public sealed class EchoTool : IPluginTool
        {
            public ToolDeclaration Declaration { get; } =
                new("echo", "Echoes", "{\"type\":\"object\"}", ToolSideEffect.ReadOnly);

            public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct) =>
                Task.FromResult(new ToolResult("echo"));
        }
        """;

    /// <summary>A plugin whose agent declares an undefined thinking effort.</summary>
    public static string BadThinking(string id) =>
        $$"""
        public sealed class TestPlugin : IPlugin
        {
            public PluginInfo Info => new("{{id}}", "1.0.0", "bad thinking test plugin");

            public void Register(IRegistrationContext context)
            {
                context.AddTool(new EchoTool());
                context.AddAgent(new AgentDeclaration("helper", "Helps", "You are a helper.", new AgentModelSettings("model-x", (AgentThinkingEffort)99, 128)));
            }
        }

        public sealed class EchoTool : IPluginTool
        {
            public ToolDeclaration Declaration { get; } =
                new("echo", "Echoes", "{\"type\":\"object\"}", ToolSideEffect.ReadOnly);

            public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct) =>
                Task.FromResult(new ToolResult("echo"));
        }
        """;

    /// <summary>A plugin whose workflow has no body.</summary>
    public static string EmptyBodyWorkflow(string id) =>
        $$"""
        public sealed class TestPlugin : IPlugin
        {
            public PluginInfo Info => new("{{id}}", "1.0.0", "empty workflow test plugin");

            public void Register(IRegistrationContext context)
            {
                context.AddTool(new EchoTool());
                context.AddWorkflow(new WorkflowDeclaration("empty", "Has no body", ""));
            }
        }

        public sealed class EchoTool : IPluginTool
        {
            public ToolDeclaration Declaration { get; } =
                new("echo", "Echoes", "{\"type\":\"object\"}", ToolSideEffect.ReadOnly);

            public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct) =>
                Task.FromResult(new ToolResult("echo"));
        }
        """;

    /// <summary>A plugin whose panel declares an unknown schema kind.</summary>
    public static string BadPanelKind(string id) =>
        $$"""
        public sealed class TestPlugin : IPlugin
        {
            public PluginInfo Info => new("{{id}}", "1.0.0", "bad panel test plugin");

            public void Register(IRegistrationContext context)
            {
                context.AddTool(new EchoTool());
                context.AddPanel(new PanelDeclaration("p1", "P", new UiSchema("hologram", "{}")));
            }
        }

        public sealed class EchoTool : IPluginTool
        {
            public ToolDeclaration Declaration { get; } =
                new("echo", "Echoes", "{\"type\":\"object\"}", ToolSideEffect.ReadOnly);

            public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct) =>
                Task.FromResult(new ToolResult("echo"));
        }
        """;

    /// <summary>Source-free manifest text for a source plugin with the given permissions.</summary>
    public static string ManifestWithPermissions(string id, string version, params string[] kinds)
    {
        var permissions = string.Join(",", kinds.Select(kind => "{\"kind\":\"" + kind + "\"}"));
        return "{\"id\":\"" + id + "\",\"version\":\"" + version + "\",\"source\":\"Plugin.cs\",\"permissions\":[" + permissions + "]}";
    }
}
