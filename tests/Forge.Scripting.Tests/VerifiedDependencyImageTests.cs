using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Forge.Scripting.Tests.Support;
using Xunit;

namespace Forge.Scripting.Tests;

public class VerifiedDependencyImageTests
{
    [Fact]
    public void Dependency_is_loaded_from_the_supplied_bytes_not_the_replaced_file_on_disk()
    {
        using var compileDir = new TempDirectory();
        using var pluginDir = new TempDirectory();
        var original = Build.Image(PluginSources.Dependency, "TestDep");
        var compileTimePath = Path.Combine(compileDir.Root, "TestDep.dll");
        File.WriteAllBytes(compileTimePath, original);
        var image = Build.Image(PluginSources.UsesDependency, "UsesDependency", compileTimePath);
        File.WriteAllBytes(
            Path.Combine(pluginDir.Root, "TestDep.dll"),
            Build.Image(PluginSources.DependencyVariant("hello from the replacement"), "TestDep"));

        var context = PluginLoadContext.FromVerifiedImages(
            pluginDir.Root,
            new Dictionary<string, byte[]> { ["TestDep"] = original });
        context.LoadPluginAssembly(image);
        var plugin = context.CreatePlugin("Uses.DependentPlugin");
        var vendored = Assert.Single(context.Assemblies, assembly => assembly.GetName().Name == "TestDep");

        Assert.True(context.UsesVerifiedDependencyImages);
        Assert.Equal("hello from dep", plugin.Info.Description);
        Assert.Same(context, AssemblyLoadContext.GetLoadContext(vendored));
        Assert.Equal(string.Empty, vendored.Location);
        context.BeginUnload();
    }

    [Fact]
    public void Unresolved_dependency_fails_naming_it_and_never_reads_the_plugin_directory()
    {
        using var compileDir = new TempDirectory();
        using var pluginDir = new TempDirectory();
        var marker = Path.Combine(pluginDir.Root, "disk-dependency-loaded.txt");
        var compileTimePath = Path.Combine(compileDir.Root, "TestDep.dll");
        File.WriteAllBytes(compileTimePath, Build.Image(PluginSources.Dependency, "TestDep"));
        File.WriteAllBytes(Path.Combine(pluginDir.Root, "TestDep.dll"), Build.Image(DiskTouchingDependency(marker), "TestDep"));
        var image = Build.Image(PluginSources.UsesDependency, "UsesDependency", compileTimePath);
        var context = PluginLoadContext.FromVerifiedImages(pluginDir.Root, new Dictionary<string, byte[]>());
        context.LoadPluginAssembly(image);

        var ex = Assert.ThrowsAny<FileNotFoundException>(() => context.CreatePlugin("Uses.DependentPlugin"));

        Assert.Contains("TestDep", MessageChain(ex), StringComparison.Ordinal);
        Assert.DoesNotContain(context.Assemblies, assembly => assembly.GetName().Name == "TestDep");
        Assert.False(File.Exists(marker));
        context.BeginUnload();
    }

    [Fact]
    public void Verified_mode_rejects_a_dependency_image_that_is_not_managed()
    {
        using var dir = new TempDirectory();
        var native = WithoutCliHeader(Build.Image(PluginSources.Dependency, "TestDep"));

        var ex = Assert.Throws<BadImageFormatException>(() =>
            PluginLoadContext.FromVerifiedImages(dir.Root, new Dictionary<string, byte[]> { ["TestDep"] = native }));

        Assert.Contains("TestDep", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not a managed assembly", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pe_without_managed_metadata_is_not_a_managed_image()
    {
        var managed = Build.Image(PluginSources.Dependency, "TestDep");
        var native = WithoutCliHeader(managed);

        Assert.True(ManagedImage.IsManaged(managed));
        Assert.Equal("TestDep", ManagedImage.ReadAssemblyName(managed));
        Assert.False(ManagedImage.IsManaged(native));
        Assert.Null(ManagedImage.ReadAssemblyName(native));
        Assert.False(ManagedImage.IsManaged([]));
        Assert.Null(ManagedImage.ReadAssemblyName([1, 2, 3, 4]));
    }

    [Fact]
    public async Task Collected_context_leaves_no_verified_image_bytes_behind()
    {
        var (context, images) = LoadFromVerifiedImagesAndUnload();

        Assert.True(await PluginLoadContext.WaitForCollectionAsync(context, TimeSpan.FromSeconds(10)));
        Assert.False(context.IsAlive);
        Assert.False(images.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Context, WeakReference Images) LoadFromVerifiedImagesAndUnload()
    {
        using var compileDir = new TempDirectory();
        using var pluginDir = new TempDirectory();
        var original = Build.Image(PluginSources.Dependency, "TestDep");
        var compileTimePath = Path.Combine(compileDir.Root, "TestDep.dll");
        File.WriteAllBytes(compileTimePath, original);
        var pluginImage = Build.Image(PluginSources.UsesDependency, "UsesDependency", compileTimePath);
        var dependencyImage = (byte[])original.Clone();
        var images = new Dictionary<string, byte[]> { ["TestDep"] = dependencyImage };

        var context = PluginLoadContext.FromVerifiedImages(pluginDir.Root, images);
        context.LoadPluginAssembly(pluginImage);
        Assert.Equal("hello from dep", context.CreatePlugin("Uses.DependentPlugin").Info.Description);
        context.BeginUnload();
        return (new WeakReference(context), new WeakReference(images["TestDep"]));
    }

    private static string DiskTouchingDependency(string marker) =>
        $$"""
        namespace Dep;

        internal static class DiskMarker
        {
            [System.Runtime.CompilerServices.ModuleInitializer]
            public static void Init()
            {
                try
                {
                    File.WriteAllText(@"{{marker}}", "the on-disk dependency was loaded");
                }
                catch (IOException)
                {
                }
            }
        }

        public static class Greeter
        {
            public static string Greet() => "hello from the on-disk copy";
        }
        """;

    private static string MessageChain(Exception exception)
    {
        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
    }

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
}
