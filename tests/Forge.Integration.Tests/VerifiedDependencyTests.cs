using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Forge.Scripting;
using Xunit;

namespace Forge.Integration.Tests;

/// <summary>A plugin's private dependency is verified with the rest of the plugin directory, and the worker then loads
/// it from those verified bytes: replacing the file on disk after verification must never change what executes, even
/// when the first load happens mid-tool-call.</summary>
public sealed class VerifiedDependencyTests
{
    private const string DependencyAssembly = "BarrierDep";

    [Fact]
    public async Task Dependency_replaced_after_verification_never_runs_even_when_first_loaded_mid_call()
    {
        await using var fx = new E2eFixture();
        var marker = Path.Combine(fx.Root, "replaced-dependency-loaded.txt");
        var started = Path.Combine(fx.Root, "tool-started.txt");
        var barrier = Path.Combine(fx.Root, "barrier.txt");
        var original = Compile(DependencySource(), DependencyAssembly);
        var replacement = Compile(ReplacingDependencySource(marker), DependencyAssembly);
        var compileDirectory = Path.Combine(fx.Root, "compile");
        Directory.CreateDirectory(compileDirectory);
        var referencePath = Path.Combine(compileDirectory, DependencyAssembly + ".dll");
        File.WriteAllBytes(referencePath, original);
        var plugin = Compile(BarrierPluginSource("delta"), "delta", [referencePath]);
        var output = WritePrebuiltPlugin(
            fx,
            "delta",
            plugin,
            new Dictionary<string, byte[]> { [DependencyAssembly + ".dll"] = original });
        var dependencyPath = Path.Combine(output, DependencyAssembly + ".dll");

        await fx.StartAndApproveAsync("delta", 1, "the delta tools");
        Assert.False(File.Exists(marker));

        var call = fx.InvokeAsync("delta", "dep", JsonSerializer.Serialize(new { started, barrier }));
        await fx.WaitUntilAsync(() => File.Exists(started), "the tool to reach the barrier");

        Assert.NotEqual(Sha256Of(original), Sha256Of(replacement));
        File.WriteAllBytes(dependencyPath, replacement);
        File.WriteAllText(barrier, "released");

        var result = await call;
        Assert.False(result.IsError, result.Content);
        Assert.Equal("original", result.Content);
        Assert.False(File.Exists(marker));
        Assert.Equal(Sha256Of(replacement), Sha256OfFile(dependencyPath));
    }

    [Fact]
    public async Task A_plugin_file_without_managed_metadata_stops_the_load()
    {
        await using var fx = new E2eFixture();
        var plugin = Compile(PluginSources.Standard("delta"), "delta");
        var native = WithoutCliHeader(plugin);
        Assert.False(ManagedImage.IsManaged(native));
        var output = WritePrebuiltPlugin(fx, "delta", plugin, new Dictionary<string, byte[]> { ["native.dll"] = native });

        await fx.StartAsync();
        await fx.ApproveAsync("delta");
        Assert.Empty(fx.ToolsOf("delta"));

        File.Delete(Path.Combine(output, "native.dll"));
        await fx.Client.RefreshPluginsAsync(fx.Ct);
        await fx.ApproveAsync("delta");
        await fx.WaitForToolsAsync("delta", PluginSources.AllTools.Length, "the delta tools once the unmanaged file is gone");
        await fx.AssertToolContentAsync("delta", "echo", """{"text":"ok"}""", "echo:ok");
    }

    /// <summary>Writes a prebuilt (source-free) plugin package: <c>bin/&lt;version&gt;/plugin.dll</c> plus its private
    /// dependencies, so the host hashes exactly those files and hands them to the worker.</summary>
    private static string WritePrebuiltPlugin(
        E2eFixture fx,
        string id,
        byte[] pluginImage,
        IReadOnlyDictionary<string, byte[]> files,
        string version = "1.0.0")
    {
        var pluginDirectory = fx.PluginDirectory(id);
        var output = Path.Combine(pluginDirectory, "bin", version);
        Directory.CreateDirectory(output);
        File.WriteAllBytes(Path.Combine(output, "plugin.dll"), pluginImage);
        foreach (var (name, bytes) in files)
        {
            File.WriteAllBytes(Path.Combine(output, name), bytes);
        }

        File.WriteAllText(
            Path.Combine(pluginDirectory, "manifest.json"),
            JsonSerializer.Serialize(new { id, version, entryAssembly = "plugin.dll" }));
        return output;
    }

    private static byte[] Compile(string source, string assemblyName, IReadOnlyList<string>? references = null)
    {
        var result = new ScriptCompiler().CompileDirect(new CompilationRequest
        {
            Source = source,
            AssemblyName = assemblyName,
            References = references ?? [],
        });
        Assert.True(result.Success, result.ErrorText);
        return result.AssemblyBytes!;
    }

    private static string DependencySource() =>
        """
        namespace BarrierDep;

        public static class Greeter
        {
            public static string Greet() => "original";
        }
        """;

    private static string ReplacingDependencySource(string marker) =>
        $$"""
        namespace BarrierDep;

        internal static class Hook
        {
            [System.Runtime.CompilerServices.ModuleInitializer]
            public static void Init()
            {
                try
                {
                    File.WriteAllText(@"{{marker}}", "the replaced dependency was loaded");
                }
                catch (IOException)
                {
                }
            }
        }

        public static class Greeter
        {
            public static string Greet() => "replaced";
        }
        """;

    private static string BarrierPluginSource(string id) =>
        $$"""
        public sealed class TestPlugin : IPlugin
        {
            public PluginInfo Info => new("{{id}}", "1.0.0", "first loads a private dependency mid-call");

            public void Register(IRegistrationContext context) => context.AddTool(new DepTool());
        }

        public sealed class DepTool : IPluginTool
        {
            public ToolDeclaration Declaration { get; } =
                new("dep", "Loads a private dependency after a barrier", "{\"type\":\"object\"}", ToolSideEffect.ReadOnly);

            public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct)
            {
                File.WriteAllText(Arg(invocation.Arguments, "started"), "started");
                var barrier = Arg(invocation.Arguments, "barrier");
                var deadline = Environment.TickCount64 + 60000;
                while (!File.Exists(barrier))
                {
                    if (Environment.TickCount64 > deadline)
                    {
                        throw new TimeoutException("the barrier was never released");
                    }

                    Thread.Sleep(20);
                }

                await Task.Yield();
                return new ToolResult(Greeting());
            }

            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
            private static string Greeting() => BarrierDep.Greeter.Greet();

            private static string Arg(JsonElement args, string name) =>
                args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) ? value.ToString() : "";
        }
        """;

    /// <summary>A copy of a managed PE image with the CLI header directory cleared, so it parses as native code.</summary>
    private static byte[] WithoutCliHeader(byte[] image)
    {
        var bytes = (byte[])image.Clone();
        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3C));
        var optionalHeader = peOffset + 24;
        var magic = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(optionalHeader));
        var directories = optionalHeader + (magic == 0x20B ? 112 : 96);
        const int CliHeaderDirectory = 14;
        bytes.AsSpan(directories + (CliHeaderDirectory * 8), 8).Clear();
        return bytes;
    }

    private static string Sha256Of(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Sha256OfFile(string path) => Sha256Of(File.ReadAllBytes(path));
}
