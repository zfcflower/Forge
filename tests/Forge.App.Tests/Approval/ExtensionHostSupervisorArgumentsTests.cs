using Forge.App.Composition;
using Xunit;

namespace Forge.App.Tests.Approval;

/// <summary>
/// The plugin-root switches the supervisor hands the extension host (wave W10a). The rule is "a switch is passed only
/// when its directory exists": a layout without bundled or built-in plugins passes neither, so a path that is merely
/// expected (or a typo in the directory name) can never reach the host as an empty payload. Pinned here because the
/// alternative - an argument list built inline in <c>Create</c> - is only observable by starting a host process.
/// </summary>
public sealed class ExtensionHostSupervisorArgumentsTests : IDisposable
{
    private readonly string root = TestTempFiles.NewDirectory("forge-host-arguments-");

    public void Dispose() => TestTempFiles.DeleteTree(root);

    [Fact]
    public void A_layout_without_plugin_directories_passes_neither_switch()
    {
        var app = NewAppDirectory("plain");

        var arguments = ExtensionHostSupervisor.PluginRootArguments(app);

        Assert.Empty(arguments);
    }

    [Fact]
    public void An_existing_bundled_directory_is_passed_as_a_switch_and_a_value()
    {
        var app = NewAppDirectory("bundled");
        var bundled = Directory.CreateDirectory(Path.Combine(app, "plugins-bundled")).FullName;

        var arguments = ExtensionHostSupervisor.PluginRootArguments(app);

        Assert.Equal(["--bundled", bundled], arguments);
    }

    [Fact]
    public void An_existing_builtin_directory_is_passed_as_a_switch_and_a_value()
    {
        var app = NewAppDirectory("builtin");
        var builtIn = Directory.CreateDirectory(Path.Combine(app, "plugins-builtin")).FullName;

        var arguments = ExtensionHostSupervisor.PluginRootArguments(app);

        Assert.Equal(["--builtin", builtIn], arguments);
    }

    [Fact]
    public void Both_directories_are_passed_bundled_first()
    {
        var app = NewAppDirectory("both");
        var bundled = Directory.CreateDirectory(Path.Combine(app, "plugins-bundled")).FullName;
        var builtIn = Directory.CreateDirectory(Path.Combine(app, "plugins-builtin")).FullName;

        var arguments = ExtensionHostSupervisor.PluginRootArguments(app);

        // The order is the host's parse order, and it also pins that each switch carries exactly its own directory.
        Assert.Equal(["--bundled", bundled, "--builtin", builtIn], arguments);
    }

    /// <summary>An application layout that exists but holds no plugin roots.</summary>
    private string NewAppDirectory(string name)
    {
        var app = Path.Combine(root, name);
        Directory.CreateDirectory(app);
        return app;
    }
}
