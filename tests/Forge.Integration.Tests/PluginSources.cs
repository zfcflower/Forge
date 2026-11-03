namespace Forge.Integration.Tests;

/// <summary>C# source of the test plugins. Every plugin exposes the same six tools:
/// <c>echo</c> (returns "&lt;prefix&gt;:&lt;text&gt;"), <c>pid</c> (worker process id), <c>sleep</c> (ignores cancellation for
/// <c>ms</c> milliseconds, writing <c>marker</c> first when given), <c>throw</c>, <c>callHost</c> (calls the host tool
/// <c>target</c> with <c>args</c> through <see cref="Forge.Plugin.Sdk.IHostFacade"/>) and <c>crash</c> (ends the worker).</summary>
internal static class PluginSources
{
    public const string EchoTool = "echo";
    public const string PidTool = "pid";
    public const string SleepTool = "sleep";
    public const string ThrowTool = "throw";
    public const string CallHostTool = "callHost";
    public const string CrashTool = "crash";

    public static readonly string[] AllTools = [EchoTool, PidTool, SleepTool, ThrowTool, CallHostTool, CrashTool];

    public static string Standard(string id, string echoPrefix = "echo", string version = "1.0.0") =>
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

        public sealed class SleepTool : IPluginTool
        {
            public ToolDeclaration Declaration { get; } =
                new("sleep", "Sleeps without honoring cancellation", "{\"type\":\"object\"}", ToolSideEffect.ReadOnly);

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
                new("throw", "Always throws", "{\"type\":\"object\"}", ToolSideEffect.ReadOnly);

            public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct) =>
                throw new InvalidOperationException("boom:" + TestPlugin.Arg(invocation.Arguments, "text"));
        }

        public sealed class CallHostTool : IPluginTool
        {
            public ToolDeclaration Declaration { get; } =
                new("callHost", "Calls a main-program tool", "{\"type\":\"object\"}", ToolSideEffect.Execute);

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
                new("crash", "Terminates the worker process", "{\"type\":\"object\"}", ToolSideEffect.Execute);

            public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct)
            {
                Environment.Exit(3);
                return Task.FromResult(new ToolResult("unreachable"));
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
}
