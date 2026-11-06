using Forge.HostClient;
using Xunit;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.Integration.Tests;

/// <summary>
/// Lifecycle blocks: disable/delete refuse same-version restaging (<c>extension_blocked</c>, surviving
/// restarts), enable only lifts <c>Disabled</c>, disable unregisters tools and stops the worker, enable
/// runs again only when the approved hashes still cover the disk, delete removes the directory and its
/// status row. Unknown plugins report <c>unknown_plugin</c>, wrong versions <c>version_mismatch</c>.
/// </summary>
public sealed class ExtensionBlocklistTests
{
    [Fact]
    public async Task Disable_blocks_restaging_the_same_version()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        await CommitAsync(fx, "gated", "1.0.0");

        await fx.Client.DisablePluginAsync("gated", "1.0.0", fx.Ct);
        Assert.Equal("Disabled", fx.ReadRecord("gated", "1.0.0")!.Status);

        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.StageExtensionAsync(
                ExtensionAuthoringSupport.StagePluginRequest("gated", version: "1.0.0"), fx.Ct),
            "extension_blocked");
        Assert.False(Directory.Exists(ExtensionAuthoringSupport.PendingDirectory(fx.PluginsDirectory, "gated")));
    }

    [Fact]
    public async Task Delete_blocks_restaging_and_survives_restart()
    {
        await using var host = RestartableHost.Create();
        var client = await host.StartAsync();
        await CommitAsync(host, "gone", "1.0.0");

        await client.DeletePluginAsync("gone", "1.0.0", host.Ct);

        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => client.StageExtensionAsync(
                ExtensionAuthoringSupport.StagePluginRequest("gone", version: "1.0.0"), host.Ct),
            "extension_blocked");

        client = await host.RestartAsync();
        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => client.StageExtensionAsync(
                ExtensionAuthoringSupport.StagePluginRequest("gone", version: "1.0.0"), host.Ct),
            "extension_blocked");
        Assert.Equal("Uninstalled", ApprovalDatabase.ReadRecord(host.DataDirectory, "gone", "1.0.0")!.Status);
    }

    [Fact]
    public async Task Enable_lifts_disabled_but_not_uninstalled()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        await CommitAsync(fx, "flip", "1.0.0");
        await fx.Client.DisablePluginAsync("flip", "1.0.0", fx.Ct);

        await fx.Client.EnablePluginAsync("flip", "1.0.0", fx.Ct);
        Assert.Equal("Approved", fx.ReadRecord("flip", "1.0.0")!.Status);

        // Staging is allowed again: the block only existed while disabled.
        var staged = await fx.Client.StageExtensionAsync(
            ExtensionAuthoringSupport.StagePluginRequest("flip", version: "1.0.0"), fx.Ct);
        Assert.Empty(staged.Diagnostics);

        await fx.Client.DeletePluginAsync("flip", "1.0.0", fx.Ct);

        // The deleted plugin is undiscovered, so enable has nothing to lift: it reports
        // unknown_plugin and the uninstall block below stays in force.
        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.EnablePluginAsync("flip", "1.0.0", fx.Ct), "unknown_plugin");

        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.StageExtensionAsync(
                ExtensionAuthoringSupport.StagePluginRequest("flip", version: "1.0.0"), fx.Ct),
            "extension_blocked");
        Assert.Equal("Uninstalled", fx.ReadRecord("flip", "1.0.0")!.Status);
    }

    [Fact]
    public async Task Disable_unregisters_tools_and_stops_the_worker()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        await CommitAsync(fx, "halt", "1.0.0");
        await fx.WaitUntilAsync(() => fx.ToolsOf("halt").Count == 6, "halt tools");
        var staleEcho = fx.ToolOf("halt", "echo");
        var pid = await fx.WorkerPidAsync("halt");

        await fx.Client.DisablePluginAsync("halt", "1.0.0", fx.Ct);

        await fx.WaitUntilAsync(() => fx.ToolsOf("halt").Count == 0, "halt tools to be unregistered");
        await fx.WaitForExitAsync(pid, "the disabled worker");
        Assert.Equal("Disabled", fx.ReadRecord("halt", "1.0.0")!.Status);

        var rejected = await staleEcho.InvokeAsync(
            new Forge.Abstractions.ToolCall(Guid.NewGuid().ToString("N"), "echo", """{"text":"x"}"""),
            new Forge.Abstractions.ToolContext("halt-session", fx.Root),
            fx.Ct);
        Assert.True(rejected.IsError);
        Assert.Contains("must not run", rejected.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Enable_restores_tools_when_hashes_still_cover_the_disk()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        await CommitAsync(fx, "resume", "1.0.0");
        await fx.WaitUntilAsync(() => fx.ToolsOf("resume").Count == 6, "resume tools");

        await fx.Client.DisablePluginAsync("resume", "1.0.0", fx.Ct);
        await fx.WaitUntilAsync(() => fx.ToolsOf("resume").Count == 0, "resume tools to be unregistered");

        await fx.Client.EnablePluginAsync("resume", "1.0.0", fx.Ct);
        await fx.WaitUntilAsync(() => fx.ToolsOf("resume").Count == 6, "resume tools after enable");

        var echo = await fx.InvokeAsync("resume", "echo", """{"text":"back"}""");
        Assert.False(echo.IsError, echo.Content);
        Assert.Equal("x:back", echo.Content);
    }

    [Fact]
    public async Task Enable_with_changed_disk_content_stays_not_running()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        await CommitAsync(fx, "drift", "1.0.0");
        await fx.WaitUntilAsync(() => fx.ToolsOf("drift").Count == 6, "drift tools");

        await fx.Client.DisablePluginAsync("drift", "1.0.0", fx.Ct);
        await fx.WaitUntilAsync(() => fx.ToolsOf("drift").Count == 0, "drift tools to be unregistered");

        fx.EditSource("drift", PluginSources.Standard("drift", echoPrefix: "changed"));
        await fx.Client.RefreshPluginsAsync(fx.Ct);

        await fx.Client.EnablePluginAsync("drift", "1.0.0", fx.Ct);

        // The enable call applies synchronously: with non-covering hashes no registration
        // notification is ever sent, and no stale registration is in flight (disable was
        // observed empty and a refresh round-tripped after the edit).
        Assert.Empty(fx.ToolsOf("drift"));
    }

    [Fact]
    public async Task Delete_removes_directory_pending_and_status_row()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        await CommitAsync(fx, "bye", "1.0.0");
        await fx.WaitUntilAsync(() => fx.ToolsOf("bye").Count == 6, "bye tools");

        // A staged update is discarded together with the live directory.
        var update = await fx.Client.StageExtensionAsync(
            ExtensionAuthoringSupport.StagePluginRequest("bye", version: "1.0.0"), fx.Ct);
        Assert.True(update.IsUpdate);

        await fx.Client.DeletePluginAsync("bye", "1.0.0", fx.Ct);

        Assert.False(Directory.Exists(ExtensionAuthoringSupport.LiveDirectory(fx.PluginsDirectory, "bye")));
        Assert.False(Directory.Exists(ExtensionAuthoringSupport.PendingDirectory(fx.PluginsDirectory, "bye")));
        Assert.Empty(fx.ToolsOf("bye"));
        var statuses = await fx.Client.GetPluginStatusesAsync(fx.Ct);
        Assert.DoesNotContain(statuses, status => string.Equals(status.PluginId, "bye", StringComparison.Ordinal));
        Assert.Equal("Uninstalled", fx.ReadRecord("bye", "1.0.0")!.Status);
    }

    [Fact]
    public async Task Unknown_plugin_reports_unknown_plugin()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();

        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.DisablePluginAsync("ghost", "1.0.0", fx.Ct), "unknown_plugin");
        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.EnablePluginAsync("ghost", "1.0.0", fx.Ct), "unknown_plugin");
        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.DeletePluginAsync("ghost", "1.0.0", fx.Ct), "unknown_plugin");
    }

    [Fact]
    public async Task Wrong_version_reports_version_mismatch()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        await CommitAsync(fx, "ver", "1.0.0");

        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.DisablePluginAsync("ver", "9.9.9", fx.Ct), "version_mismatch");
        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.EnablePluginAsync("ver", "9.9.9", fx.Ct), "version_mismatch");
        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.DeletePluginAsync("ver", "9.9.9", fx.Ct), "version_mismatch");

        // The installed version is untouched and still runnable.
        var echo = await fx.InvokeAsync("ver", "echo", """{"text":"ok"}""");
        Assert.False(echo.IsError, echo.Content);
        Assert.Equal("x:ok", echo.Content);
    }

    private static async Task CommitAsync(E2eFixture fx, string id, string version)
    {
        var staged = await fx.Client.StageExtensionAsync(
            ExtensionAuthoringSupport.StagePluginRequest(id, version: version), fx.Ct);
        Assert.Empty(staged.Diagnostics);
        await fx.Client.CommitExtensionAsync(
            new Sdk.ExtensionCommitRequest(id, staged.SourceHash, staged.DependencyHash, staged.SdkVersion),
            fx.Ct);
    }

    private static async Task CommitAsync(RestartableHost host, string id, string version)
    {
        var staged = await host.Client.StageExtensionAsync(
            ExtensionAuthoringSupport.StagePluginRequest(id, version: version), host.Ct);
        Assert.Empty(staged.Diagnostics);
        await host.Client.CommitExtensionAsync(
            new Sdk.ExtensionCommitRequest(id, staged.SourceHash, staged.DependencyHash, staged.SdkVersion),
            host.Ct);
        await host.WaitUntilAsync(
            () => host.Client.ToolsByPlugin.TryGetValue(id, out var tools) && tools.Count == 6, id + " tools");
    }
}
