using Forge.Plugin.Sdk;
using Forge.Scripting.Tests.Support;
using Xunit;

namespace Forge.Scripting.Tests;

/// <summary>In verified-image mode a plugin's native library request must fail with <see cref="DllNotFoundException"/>
/// naming the library, never fall through to default probing and never depend on what the plugin directory holds. The
/// path mode keeps handing unmanaged resolution to the default probing.</summary>
public class VerifiedNativeLibraryTests
{
    private const string DecoyName = "private-native.bin";

    [Fact]
    public void Verified_mode_refuses_a_native_library_while_the_file_is_present_replaced_and_deleted()
    {
        using var pluginDir = new TempDirectory();
        var (libraryPath, entryPoint) = SystemNativeLibrary();
        var decoy = Path.Combine(pluginDir.Root, DecoyName);
        File.Copy(libraryPath, decoy);
        var context = PluginLoadContext.FromVerifiedImages(pluginDir.Root, new Dictionary<string, byte[]>());
        context.LoadPluginAssembly(Build.Image(PluginSource(DecoyName, entryPoint), "NativePlugin"));
        var tool = Tool(context);

        var present = Assert.Throws<DllNotFoundException>(() => { _ = Invoke(tool); });
        Assert.Contains(DecoyName, present.Message, StringComparison.Ordinal);
        Assert.Contains("native libraries are not supported for verified plugins", present.Message, StringComparison.Ordinal);

        File.WriteAllBytes(decoy, Build.Image(PluginSources.Dependency, "TestDep"));
        var replaced = Assert.Throws<DllNotFoundException>(() => { _ = Invoke(tool); });
        Assert.Equal(present.Message, replaced.Message);

        File.Delete(decoy);
        var deleted = Assert.Throws<DllNotFoundException>(() => { _ = Invoke(tool); });
        Assert.Equal(present.Message, deleted.Message);

        context.BeginUnload();
    }

    [Fact]
    public void Verified_mode_refuses_a_native_library_the_operating_system_could_resolve()
    {
        using var pluginDir = new TempDirectory();
        var (libraryPath, entryPoint) = SystemNativeLibrary();
        var context = PluginLoadContext.FromVerifiedImages(pluginDir.Root, new Dictionary<string, byte[]>());
        context.LoadPluginAssembly(Build.Image(PluginSource(libraryPath, entryPoint), "NativePlugin"));
        var tool = Tool(context);

        var ex = Assert.Throws<DllNotFoundException>(() => { _ = Invoke(tool); });

        Assert.Contains(libraryPath, ex.Message, StringComparison.Ordinal);
        Assert.Contains("native libraries are not supported for verified plugins", ex.Message, StringComparison.Ordinal);
        context.BeginUnload();
    }

    [Fact]
    public void Path_mode_still_resolves_a_native_library_from_the_operating_system()
    {
        using var pluginDir = new TempDirectory();
        var (libraryPath, entryPoint) = SystemNativeLibrary();
        var context = PathModeContext(pluginDir.Root, PluginSource(libraryPath, entryPoint));

        var content = Invoke(Tool(context));

        Assert.True(int.Parse(content) > 0, content);
        context.BeginUnload();
    }

    [Fact]
    public void Path_mode_still_falls_through_to_default_probing_for_an_unresolvable_library()
    {
        using var pluginDir = new TempDirectory();
        var context = PathModeContext(pluginDir.Root, PluginSource(DecoyName, "getpid"));
        var tool = Tool(context);

        var ex = Assert.Throws<DllNotFoundException>(() => { _ = Invoke(tool); });

        Assert.Contains(DecoyName, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not supported for verified plugins", ex.Message, StringComparison.Ordinal);
        context.BeginUnload();
    }

    private static PluginLoadContext PathModeContext(string pluginDirectory, string source)
    {
        var (libraryPath, _) = SystemNativeLibrary();
        File.Copy(libraryPath, Path.Combine(pluginDirectory, DecoyName));
        var context = new PluginLoadContext(pluginDirectory);
        Assert.False(context.UsesVerifiedDependencyImages);
        context.LoadPluginAssembly(Build.Image(source, "NativePlugin"));
        return context;
    }

    private static IPluginTool Tool(PluginLoadContext context) =>
        Assert.Single(Registered(context.CreatePlugin("Native.NativePlugin")));

    private static List<IPluginTool> Registered(IPlugin plugin)
    {
        var registration = new RecordingRegistration();
        plugin.Register(registration);
        return registration.Tools;
    }

    private static string Invoke(IPluginTool tool) =>
        tool.InvokeAsync(null!, CancellationToken.None).GetAwaiter().GetResult().Content;

    private static string PluginSource(string library, string entryPoint) =>
        $$"""
        using System.Runtime.InteropServices;
        using Forge.Plugin.Sdk;

        namespace Native;

        internal static class PrivateNative
        {
            [DllImport(@"{{library}}", EntryPoint = "{{entryPoint}}")]
            public static extern int GetId();
        }

        public sealed class NativeTool : IPluginTool
        {
            public ToolDeclaration Declaration { get; } =
                new("native", "Calls a native library", "{}", ToolSideEffect.ReadOnly);

            public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct) =>
                Task.FromResult(new ToolResult(PrivateNative.GetId().ToString()));
        }

        public sealed class NativePlugin : IPlugin
        {
            public PluginInfo Info { get; } = new("native", "1.0.0", "uses a native library");

            public void Register(IRegistrationContext context) => context.AddTool(new NativeTool());
        }
        """;

    /// <summary>The path of a native library every supported platform ships, plus an export that returns the process
    /// id so a successful resolution is observable.</summary>
    private static (string LibraryPath, string EntryPoint) SystemNativeLibrary()
    {
        if (OperatingSystem.IsWindows())
        {
            return (Path.Combine(Environment.SystemDirectory, "kernel32.dll"), "GetCurrentProcessId");
        }

        if (OperatingSystem.IsMacOS())
        {
            return ("/usr/lib/libSystem.B.dylib", "getpid");
        }

        foreach (var candidate in LinuxLibraries)
        {
            if (File.Exists(candidate))
            {
                return (candidate, "getpid");
            }
        }

        if (MappedLibraryPath() is { } mapped)
        {
            return (mapped, "getpid");
        }

        throw new InvalidOperationException(
            $"No native C library was found on this platform; looked at {string.Join(", ", LinuxLibraries)} and /proc/self/maps.");
    }

    private static readonly string[] LinuxLibraries =
    [
        "/usr/lib/x86_64-linux-gnu/libc.so.6",
        "/lib/x86_64-linux-gnu/libc.so.6",
        "/usr/lib/aarch64-linux-gnu/libc.so.6",
        "/lib/aarch64-linux-gnu/libc.so.6",
        "/usr/lib64/libc.so.6",
        "/lib64/libc.so.6",
        "/usr/lib/libc.so.6",
        "/lib/libc.so.6",
    ];

    /// <summary>The file a mapped C library was loaded from, read from <c>/proc/self/maps</c> so the test also works on
    /// a distribution the candidate list does not cover.</summary>
    private static string? MappedLibraryPath()
    {
        try
        {
            if (!File.Exists("/proc/self/maps"))
            {
                return null;
            }

            foreach (var line in File.ReadLines("/proc/self/maps"))
            {
                var separator = line.IndexOf(" /", StringComparison.Ordinal);
                if (separator < 0)
                {
                    continue;
                }

                var path = line[(separator + 1)..].Trim();
                var name = Path.GetFileName(path);
                if ((name.Contains("libc.so", StringComparison.Ordinal) || name.Contains("libc-", StringComparison.Ordinal)) &&
                    File.Exists(path))
                {
                    return path;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }
}
