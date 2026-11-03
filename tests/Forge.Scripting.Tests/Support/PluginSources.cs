namespace Forge.Scripting.Tests.Support;

internal static class PluginSources
{
    public const string EchoEntry = "Demo.DemoPlugin";

    public const string Echo = """
        using Forge.Plugin.Sdk;

        namespace Demo;

        public sealed class EchoTool : IPluginTool
        {
            public ToolDeclaration Declaration { get; } = new("echo", "Echoes its input", "{}", ToolSideEffect.ReadOnly);

            public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct) =>
                Task.FromResult(new ToolResult("ok"));
        }

        public sealed class DemoPlugin : IPlugin
        {
            public PluginInfo Info { get; } = new("demo", "1.0.0", "Demo plugin");

            public void Register(IRegistrationContext context) => context.AddTool(new EchoTool());
        }
        """;

    public const string Broken = """
        using Forge.Plugin.Sdk;

        namespace Broken;

        public sealed class BrokenPlugin : IPlugin
        {
            public void Register(IRegistrationContext context) => context.AddTool(Missing);
        }
        """;

    public const string ErrorAndWarning = """
        namespace Mixed;

        public static class MixedPlugin
        {
            public static int Run()
            {
                int unused;
                return Missing;
            }
        }
        """;

    public const string WarningOnly = """
        namespace Warned;

        public static class WarnedPlugin
        {
            public static int Run()
            {
                int unused;
                return 1;
            }
        }
        """;

    public const string UsesListWithoutDirective = """
        namespace Lists;

        public static class ListUser
        {
            public static int Count() => new List<int> { 1, 2 }.Count;
        }
        """;

    public const string EntryVariants = """
        using Forge.Plugin.Sdk;

        namespace Variants;

        public abstract class AbstractPlugin : IPlugin
        {
            public PluginInfo Info => new("abstract", "1.0.0");

            public abstract void Register(IRegistrationContext context);
        }

        public sealed class NotAPlugin
        {
        }

        public sealed class ParameterizedPlugin : IPlugin
        {
            public ParameterizedPlugin(int seed)
            {
            }

            public PluginInfo Info => new("parameterized", "1.0.0");

            public void Register(IRegistrationContext context)
            {
            }
        }

        public sealed class PrivateCtorPlugin : IPlugin
        {
            private PrivateCtorPlugin()
            {
            }

            public PluginInfo Info => new("private", "1.0.0");

            public void Register(IRegistrationContext context)
            {
            }
        }

        public sealed class ThrowingPlugin : IPlugin
        {
            public ThrowingPlugin() => throw new InvalidOperationException("ctor failed");

            public PluginInfo Info => new("throws", "1.0.0");

            public void Register(IRegistrationContext context)
            {
            }
        }
        """;

    public const string Dependency = """
        namespace Dep;

        public static class Greeter
        {
            public static string Greet() => "hello from dep";
        }
        """;

    public const string UsesDependency = """
        using Forge.Plugin.Sdk;

        namespace Uses;

        public sealed class DependentPlugin : IPlugin
        {
            public PluginInfo Info { get; } = new("dependent", "1.0.0", Dep.Greeter.Greet());

            public void Register(IRegistrationContext context)
            {
            }
        }
        """;

    public static string DependencyVariant(string greeting) => $$"""
        namespace Dep;

        public static class Greeter
        {
            public static string Greet() => "{{greeting}}";
        }
        """;

    public static string ManyErrors(int count)
    {
        var members = Enumerable.Range(1, count).Select(i => $"    public int V{i} = Missing{i};");
        return "namespace Many;\npublic sealed class ManyPlugin\n{\n" + string.Join('\n', members) + "\n}\n";
    }
}
