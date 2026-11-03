using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Forge.Plugin.Sdk;
using Forge.Scripting.Tests.Support;
using Xunit;

namespace Forge.Scripting.Tests;

public class PluginLoadContextTests
{
    [Fact]
    public void Image_loads_and_its_entry_registers_the_declared_tool()
    {
        using var dir = new TempDirectory();
        var context = new PluginLoadContext(dir.Root);

        var assembly = context.LoadPluginAssembly(Build.Image(PluginSources.Echo));
        var plugin = context.CreatePlugin(PluginSources.EchoEntry);

        Assert.Same(assembly, context.PluginAssembly);
        Assert.Same(context, AssemblyLoadContext.GetLoadContext(assembly));
        Assert.Equal(new PluginInfo("demo", "1.0.0", "Demo plugin"), plugin.Info);
        var registration = new RecordingRegistration();
        plugin.Register(registration);
        var tool = Assert.Single(registration.Tools);
        Assert.Equal(new ToolDeclaration("echo", "Echoes its input", "{}", ToolSideEffect.ReadOnly), tool.Declaration);
    }

    [Fact]
    public void Plugin_sdk_is_shared_with_the_host_so_IPlugin_has_one_identity()
    {
        using var dir = new TempDirectory();
        var context = new PluginLoadContext(dir.Root);
        var assembly = context.LoadPluginAssembly(Build.Image(PluginSources.Echo));
        var pluginType = assembly.GetType(PluginSources.EchoEntry)!;

        Assert.Contains(typeof(IPlugin), pluginType.GetInterfaces());
        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(typeof(IPlugin).Assembly));
        Assert.Contains("Forge.Plugin.Sdk", context.SharedAssemblyNames);
        Assert.Contains(assembly.GetReferencedAssemblies(), name => name.Name == "Forge.Plugin.Sdk");
    }

    [Fact]
    public void Extra_shared_names_are_added_and_blank_names_are_ignored()
    {
        using var dir = new TempDirectory();

        var context = new PluginLoadContext(dir.Root, name: "custom", sharedAssemblyNames: ["Acme.Shared", " ", ""]);

        Assert.Equal("custom", context.Name);
        Assert.Contains("Acme.Shared", context.SharedAssemblyNames);
        Assert.DoesNotContain(" ", context.SharedAssemblyNames);
        Assert.DoesNotContain(string.Empty, context.SharedAssemblyNames);
    }

    [Fact]
    public void Shared_names_are_the_platform_set_and_the_contract_not_every_host_loaded_assembly()
    {
        using var dir = new TempDirectory();
        var hostCopy = AssemblyLoadContext.Default.LoadFromStream(
            new MemoryStream(Build.Image(PluginSources.DependencyVariant("hello from host dep"), "HostSibling"), writable: false));
        Assert.Contains(hostCopy, AssemblyLoadContext.Default.Assemblies);

        var context = new PluginLoadContext(dir.Root);

        Assert.Contains("System.Runtime", context.SharedAssemblyNames);
        Assert.Contains(typeof(IPlugin).Assembly.GetName().Name!, context.SharedAssemblyNames);
        Assert.DoesNotContain(hostCopy.GetName().Name!, context.SharedAssemblyNames);
    }

    [Fact]
    public async Task Plugin_local_copy_wins_over_a_host_loaded_assembly_with_the_same_simple_name()
    {
        using var compileDir = new TempDirectory();
        using var pluginDir = new TempDirectory();
        var localImage = Build.Image(PluginSources.DependencyVariant("hello from plugin dep"), "HostSibling");
        var compileTimePath = Path.Combine(compileDir.Root, "HostSibling.dll");
        File.WriteAllBytes(compileTimePath, localImage);
        File.WriteAllBytes(Path.Combine(pluginDir.Root, "HostSibling.dll"), localImage);
        var image = Build.Image(PluginSources.UsesDependency, "UsesDependency", compileTimePath);

        var hostCopy = AssemblyLoadContext.Default.LoadFromStream(
            new MemoryStream(Build.Image(PluginSources.DependencyVariant("hello from host dep"), "HostSibling"), writable: false));
        Assert.Contains(hostCopy, AssemblyLoadContext.Default.Assemblies);

        var (description, weak) = LoadVendoredDependencyAndUnload(pluginDir.Root, image, "HostSibling");
        var collected = await PluginLoadContext.WaitForCollectionAsync(weak, TimeSpan.FromSeconds(10));

        Assert.Equal("hello from plugin dep", description);
        Assert.True(collected);
    }

    [Fact]
    public void Default_name_is_derived_from_the_directory_name_ignoring_a_trailing_separator()
    {
        using var dir = new TempDirectory();

        var context = new PluginLoadContext(dir.Root + Path.DirectorySeparatorChar);

        Assert.Equal("forge-plugin:" + Path.GetFileName(dir.Root), context.Name);
        Assert.Equal(Path.GetFullPath(dir.Root), Path.TrimEndingDirectorySeparator(context.PluginDirectory));
    }

    [Fact]
    public void Blank_directory_is_rejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => new PluginLoadContext(" "));
    }

    [Fact]
    public void Plugin_image_loads_from_read_only_memory()
    {
        using var dir = new TempDirectory();
        var context = new PluginLoadContext(dir.Root);
        var image = Build.Image(PluginSources.Echo);

        var assembly = context.LoadPluginAssembly(new ReadOnlyMemory<byte>(image));

        Assert.Same(assembly, context.PluginAssembly);
    }

    [Fact]
    public void Second_image_in_the_same_context_is_rejected()
    {
        using var dir = new TempDirectory();
        var context = new PluginLoadContext(dir.Root);
        var image = Build.Image(PluginSources.Echo);
        context.LoadPluginAssembly(image);

        var ex = Assert.Throws<InvalidOperationException>(() => context.LoadPluginAssembly(image));

        Assert.Contains("already loaded", ex.Message);
    }

    [Fact]
    public void Null_image_is_rejected()
    {
        using var dir = new TempDirectory();
        var context = new PluginLoadContext(dir.Root);

        Assert.Throws<ArgumentNullException>(() => context.LoadPluginAssembly((byte[])null!));
    }

    [Fact]
    public void Garbage_bytes_fail_to_load_and_leave_the_context_empty()
    {
        using var dir = new TempDirectory();
        var context = new PluginLoadContext(dir.Root);

        Assert.Throws<BadImageFormatException>(() => context.LoadPluginAssembly(new byte[] { 1, 2, 3, 4 }));

        Assert.Null(context.PluginAssembly);
    }

    [Fact]
    public void Missing_assembly_path_is_reported_as_file_not_found()
    {
        using var dir = new TempDirectory();
        var context = new PluginLoadContext(dir.Root);

        Assert.Throws<FileNotFoundException>(() =>
            context.LoadPluginAssemblyFromPath(Path.Combine(dir.Root, "missing.dll")));
    }

    [Fact]
    public void CreatePlugin_before_any_load_reports_no_loaded_assembly()
    {
        using var dir = new TempDirectory();
        var context = new PluginLoadContext(dir.Root);

        var ex = Assert.Throws<InvalidOperationException>(() => context.CreatePlugin(PluginSources.EchoEntry));

        Assert.Contains("No plugin assembly is loaded", ex.Message);
    }

    [Theory]
    [InlineData("Variants.NotAPlugin", "does not implement")]
    [InlineData("Variants.AbstractPlugin", "it is abstract")]
    [InlineData("Variants.ParameterizedPlugin", "no public parameterless constructor")]
    [InlineData("Variants.PrivateCtorPlugin", "no public parameterless constructor")]
    [InlineData("Variants.Missing", "was not found in assembly")]
    public void Invalid_entry_is_rejected_with_a_specific_message(string entry, string expected)
    {
        using var dir = new TempDirectory();
        var context = new PluginLoadContext(dir.Root);
        context.LoadPluginAssembly(Build.Image(PluginSources.EntryVariants, "Variants"));

        var ex = Assert.Throws<InvalidOperationException>(() => context.CreatePlugin(entry));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Type_overload_rejects_non_plugin_types_and_the_interface_itself()
    {
        using var dir = new TempDirectory();
        var context = new PluginLoadContext(dir.Root);

        var notPlugin = Assert.Throws<InvalidOperationException>(() => context.CreatePlugin(typeof(string)));
        var contract = Assert.Throws<InvalidOperationException>(() => context.CreatePlugin(typeof(IPlugin)));

        Assert.Contains("does not implement", notPlugin.Message);
        Assert.Contains("it is abstract", contract.Message);
    }

    [Fact]
    public void Exception_from_the_entry_constructor_propagates_unwrapped()
    {
        using var dir = new TempDirectory();
        var context = new PluginLoadContext(dir.Root);
        context.LoadPluginAssembly(Build.Image(PluginSources.EntryVariants, "Variants"));

        var ex = Assert.Throws<InvalidOperationException>(() => context.CreatePlugin("Variants.ThrowingPlugin"));

        Assert.Equal("ctor failed", ex.Message);
    }

    [Fact]
    public async Task UnloadAsync_is_idempotent_and_returns_the_same_task()
    {
        using var dir = new TempDirectory();
        var context = new PluginLoadContext(dir.Root);

        var first = context.UnloadAsync(TimeSpan.FromMilliseconds(50));
        var second = context.UnloadAsync(TimeSpan.FromMilliseconds(50));

        Assert.Same(first, second);
        await first;
        Assert.True(context.IsUnloading);
        Assert.Null(context.PluginAssembly);
    }

    [Fact]
    public void Load_after_unload_is_rejected()
    {
        using var dir = new TempDirectory();
        var image = Build.Image(PluginSources.Echo);
        var path = Path.Combine(dir.Root, "DemoPlugin.dll");
        File.WriteAllBytes(path, image);
        var context = new PluginLoadContext(dir.Root);
        context.LoadPluginAssembly(image);

        context.BeginUnload();

        Assert.True(context.IsUnloading);
        Assert.Null(context.PluginAssembly);
        var fromBytes = Assert.Throws<InvalidOperationException>(() => context.LoadPluginAssembly(image));
        var fromPath = Assert.Throws<InvalidOperationException>(() => context.LoadPluginAssemblyFromPath(path));
        Assert.Contains("unloading", fromBytes.Message);
        Assert.Contains("unloading", fromPath.Message);
    }

    [Fact]
    public async Task Unload_lets_the_context_be_collected()
    {
        using var dir = new TempDirectory();

        var weak = LoadEchoAndUnload(dir.Root, Build.Image(PluginSources.Echo));
        var collected = await PluginLoadContext.WaitForCollectionAsync(weak, TimeSpan.FromSeconds(10));

        Assert.True(collected);
        Assert.False(weak.IsAlive);
    }

    [Fact]
    public async Task Unload_eventually_releases_the_dependency_file_lock()
    {
        using var compileDir = new TempDirectory();
        using var pluginDir = new TempDirectory();
        var (image, loadPath) = PrepareDependentPlugin(compileDir, pluginDir);

        var weak = LoadDependentAndUnload(pluginDir.Root, image, loadPath);
        var collected = await PluginLoadContext.WaitForCollectionAsync(weak, TimeSpan.FromSeconds(10));

        Assert.True(collected);
        DeleteWhenReleased(loadPath);
        Assert.False(File.Exists(loadPath));
    }

    [Fact]
    public async Task Collected_context_has_released_the_dependency_file_when_the_wait_returns()
    {
        using var compileDir = new TempDirectory();
        using var pluginDir = new TempDirectory();
        var (image, loadPath) = PrepareDependentPlugin(compileDir, pluginDir);

        var weak = LoadDependentAndUnload(pluginDir.Root, image, loadPath);
        var collected = await PluginLoadContext.WaitForCollectionAsync(weak, TimeSpan.FromSeconds(10));

        Assert.True(collected);
        File.Delete(loadPath);
    }

    [Fact]
    public async Task Dependency_is_resolved_from_the_plugin_directory_not_the_host()
    {
        using var compileDir = new TempDirectory();
        using var pluginDir = new TempDirectory();
        var dependency = Build.Image(PluginSources.Dependency, "TestDep");
        var compileTimePath = Path.Combine(compileDir.Root, "TestDep.dll");
        File.WriteAllBytes(compileTimePath, dependency);
        File.WriteAllBytes(Path.Combine(pluginDir.Root, "TestDep.dll"), dependency);
        var image = Build.Image(PluginSources.UsesDependency, "UsesDependency", compileTimePath);

        var (description, weak) = LoadDependentDescriptionAndUnload(pluginDir.Root, image);
        var collected = await PluginLoadContext.WaitForCollectionAsync(weak, TimeSpan.FromSeconds(10));

        Assert.Equal("hello from dep", description);
        Assert.True(collected);
    }

    [Fact]
    public async Task Wait_for_collection_reports_false_when_a_dependency_file_is_still_held_open()
    {
        using var compileDir = new TempDirectory();
        using var pluginDir = new TempDirectory();
        var (image, loadPath) = PrepareDependentPlugin(compileDir, pluginDir);

        var weak = LoadDependentAndUnload(pluginDir.Root, image, loadPath);
        using var hold = new FileStream(loadPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        var collected = await PluginLoadContext.WaitForCollectionAsync(weak, TimeSpan.FromMilliseconds(100));

        Assert.False(collected);
        var deadline = Environment.TickCount64 + 10_000;
        while (weak.IsAlive && Environment.TickCount64 < deadline)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(10);
        }

        Assert.False(weak.IsAlive);
    }

    [Fact]
    public void Missing_dependency_file_fails_the_entry_instead_of_falling_back_to_the_host()
    {
        using var compileDir = new TempDirectory();
        using var pluginDir = new TempDirectory();
        var dependency = Build.Image(PluginSources.Dependency, "TestDep");
        var compileTimePath = Path.Combine(compileDir.Root, "TestDep.dll");
        File.WriteAllBytes(compileTimePath, dependency);
        var image = Build.Image(PluginSources.UsesDependency, "UsesDependency", compileTimePath);
        var context = new PluginLoadContext(pluginDir.Root);
        context.LoadPluginAssembly(image);

        Assert.ThrowsAny<FileNotFoundException>(() => context.CreatePlugin("Uses.DependentPlugin"));
    }

    [Fact]
    public async Task Unload_eventually_releases_a_context_loaded_from_a_file_path()
    {
        using var dir = new TempDirectory();
        var assemblyPath = Path.Combine(dir.Root, "DemoPlugin.dll");
        File.WriteAllBytes(assemblyPath, Build.Image(PluginSources.Echo));

        var weak = LoadFromPathAndUnload(dir.Root, assemblyPath);
        var collected = await PluginLoadContext.WaitForCollectionAsync(weak, TimeSpan.FromSeconds(10));

        Assert.True(collected);
        DeleteWhenReleased(assemblyPath);
        Assert.False(File.Exists(assemblyPath));
    }

    [Fact]
    public async Task WaitForCollectionAsync_reports_a_live_target_as_not_collected()
    {
        var target = new object();
        var weak = new WeakReference(target);

        var collected = await PluginLoadContext.WaitForCollectionAsync(weak, TimeSpan.FromMilliseconds(50));

        Assert.False(collected);
        Assert.True(weak.IsAlive);
        GC.KeepAlive(target);
    }

    [Fact]
    public async Task WaitForCollectionAsync_reports_an_empty_reference_as_collected()
    {
        Assert.True(await PluginLoadContext.WaitForCollectionAsync(new WeakReference(null)));
    }

    [Fact]
    public async Task WaitForCollectionAsync_rejects_a_null_reference()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => PluginLoadContext.WaitForCollectionAsync(null!));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadEchoAndUnload(string directory, byte[] image)
    {
        var context = new PluginLoadContext(directory);
        context.LoadPluginAssembly(image);
        var plugin = context.CreatePlugin(PluginSources.EchoEntry);
        Assert.Equal("demo", plugin.Info.Id);
        context.BeginUnload();
        return new WeakReference(context);
    }

    private static (byte[] Image, string LoadPath) PrepareDependentPlugin(TempDirectory compileDir, TempDirectory pluginDir)
    {
        var dependency = Build.Image(PluginSources.Dependency, "TestDep");
        var compileTimePath = Path.Combine(compileDir.Root, "TestDep.dll");
        File.WriteAllBytes(compileTimePath, dependency);
        var loadPath = Path.Combine(pluginDir.Root, "TestDep.dll");
        File.WriteAllBytes(loadPath, dependency);
        return (Build.Image(PluginSources.UsesDependency, "UsesDependency", compileTimePath), loadPath);
    }

    private static void DeleteWhenReleased(string path)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (true)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(10);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (string Description, WeakReference Context) LoadVendoredDependencyAndUnload(
        string directory,
        byte[] image,
        string dependencySimpleName)
    {
        var context = new PluginLoadContext(directory);
        context.LoadPluginAssembly(image);
        var description = context.CreatePlugin("Uses.DependentPlugin").Info.Description;
        Assert.DoesNotContain(dependencySimpleName, context.SharedAssemblyNames);
        var vendored = context.Assemblies.Single(assembly => assembly.GetName().Name == dependencySimpleName);
        Assert.Same(context, AssemblyLoadContext.GetLoadContext(vendored));
        context.BeginUnload();
        return (description, new WeakReference(context));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadDependentAndUnload(string directory, byte[] image, string dependencyPath)
    {
        var context = new PluginLoadContext(directory);
        context.LoadPluginAssembly(image);
        var plugin = context.CreatePlugin("Uses.DependentPlugin");
        Assert.Equal("hello from dep", plugin.Info.Description);
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<UnauthorizedAccessException>(() => File.Delete(dependencyPath));
        }

        context.BeginUnload();
        return new WeakReference(context);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (string Description, WeakReference Context) LoadDependentDescriptionAndUnload(string directory, byte[] image)
    {
        var context = new PluginLoadContext(directory);
        context.LoadPluginAssembly(image);
        var description = context.CreatePlugin("Uses.DependentPlugin").Info.Description;
        context.BeginUnload();
        return (description, new WeakReference(context));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadFromPathAndUnload(string directory, string assemblyPath)
    {
        var context = new PluginLoadContext(directory);
        var assembly = context.LoadPluginAssemblyFromPath(assemblyPath);
        Assert.Equal(Path.GetFullPath(assemblyPath), assembly.Location);
        context.BeginUnload();
        return new WeakReference(context);
    }
}
