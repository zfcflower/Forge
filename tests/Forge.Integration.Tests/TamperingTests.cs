using Xunit;

namespace Forge.Integration.Tests;

public sealed class TamperingTests
{
    [Fact]
    public async Task Replaced_artifact_is_blocked_until_reapproved_and_never_runs_unapproved()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        var evilMarker = Path.Combine(fx.Root, "evil-loaded.txt");
        fx.WritePlugin("gamma", EvilSource(evilMarker));
        await fx.StartAndApproveAsync("alpha", PluginSources.AllTools.Length);

        var staleEcho = fx.ToolOf("alpha", "echo");
        var alphaDll = Assert.Single(Directory.GetFiles(
            Path.Combine(fx.PluginDirectory("alpha"), "bin"), "plugin.dll", SearchOption.AllDirectories));
        var gammaDll = Assert.Single(Directory.GetFiles(
            Path.Combine(fx.PluginDirectory("gamma"), "bin"), "plugin.dll", SearchOption.AllDirectories));
        Assert.False(File.Exists(evilMarker));

        File.Copy(gammaDll, alphaDll, overwrite: true);
        await fx.Client.RefreshPluginsAsync(fx.Ct);
        await fx.WaitForToolsAsync("alpha", 0, "the tampered alpha tools to be unregistered");

        var blocked = await fx.InvokeAsync(staleEcho, """{"text":"x"}""");
        Assert.True(blocked.IsError);
        Assert.Contains("must not run", blocked.Content, StringComparison.Ordinal);
        Assert.False(File.Exists(evilMarker));

        var current = fx.ReadRecord("alpha")!;
        await fx.Client.ApprovePluginAsync("alpha", "1.0.0", current.SourceHash, current.DependencyHash, current.SdkVersion, fx.Ct);
        await fx.WaitForToolsAsync("alpha", PluginSources.AllTools.Length, "alpha tools after re-approval");

        await fx.AssertToolContentAsync("alpha", "echo", """{"text":"x"}""", "evil:x");
        Assert.True(File.Exists(evilMarker));
    }

    private static string EvilSource(string marker) =>
        $$"""
        internal static class EvilHook
        {
            [System.Runtime.CompilerServices.ModuleInitializer]
            public static void Init()
            {
                try
                {
                    File.WriteAllText(@"{{marker}}", "loaded");
                }
                catch (IOException)
                {
                }
            }
        }

        public sealed class TestPlugin : IPlugin
        {
            public PluginInfo Info => new("alpha", "1.0.0", "tampered bytes");

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
            public ToolDeclaration Declaration { get; } =
                new("echo", "Echoes the text argument", "{\"type\":\"object\"}", ToolSideEffect.ReadOnly);

            public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct) =>
                Task.FromResult(new ToolResult("evil:" + TestPlugin.Arg(invocation.Arguments, "text")));
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
                await Task.Delay(1000);
                return new ToolResult("slept");
            }
        }

        public sealed class ThrowTool : IPluginTool
        {
            public ToolDeclaration Declaration { get; } =
                new("throw", "Always throws", "{\"type\":\"object\"}", ToolSideEffect.ReadOnly);

            public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct) =>
                throw new InvalidOperationException("boom");
        }

        public sealed class CallHostTool : IPluginTool
        {
            public ToolDeclaration Declaration { get; } =
                new("callHost", "Calls a main-program tool", "{\"type\":\"object\"}", ToolSideEffect.Execute);

            public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct)
            {
                var result = await invocation.Host.CallToolAsync("missing", JsonDocument.Parse("{}").RootElement, ct);
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
}
