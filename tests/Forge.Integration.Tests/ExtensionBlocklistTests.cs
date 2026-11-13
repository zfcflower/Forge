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
        await ExtensionAuthoringSupport.StageAndCommitPluginAsync(fx, "gated", "1.0.0");

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
        await ExtensionAuthoringSupport.StageAndCommitPluginAsync(client, host.Ct, "gone", "1.0.0");
        await host.WaitUntilAsync(
            () => host.Client.ToolsByPlugin.TryGetValue("gone", out var tools) && tools.Count == 6, "gone tools");

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
        await ExtensionAuthoringSupport.StageAndCommitPluginAsync(fx, "flip", "1.0.0");
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
        await ExtensionAuthoringSupport.StageAndCommitPluginAsync(fx, "halt", "1.0.0");
        await fx.WaitForToolsAsync("halt", 6, "halt tools");
        var staleEcho = fx.ToolOf("halt", "echo");
        var pid = await fx.WorkerPidAsync("halt");

        await fx.Client.DisablePluginAsync("halt", "1.0.0", fx.Ct);

        await fx.WaitForToolsAsync("halt", 0, "halt tools to be unregistered");
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
        await ExtensionAuthoringSupport.StageAndCommitPluginAsync(fx, "resume", "1.0.0");
        await fx.WaitForToolsAsync("resume", 6, "resume tools");

        await fx.Client.DisablePluginAsync("resume", "1.0.0", fx.Ct);
        await fx.WaitForToolsAsync("resume", 0, "resume tools to be unregistered");

        await fx.Client.EnablePluginAsync("resume", "1.0.0", fx.Ct);
        await fx.WaitForToolsAsync("resume", 6, "resume tools after enable");

        await fx.AssertToolContentAsync("resume", "echo", """{"text":"back"}""", "x:back");
    }

    [Fact]
    public async Task Enable_with_changed_disk_content_stays_not_running()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        await ExtensionAuthoringSupport.StageAndCommitPluginAsync(fx, "drift", "1.0.0");
        await fx.WaitForToolsAsync("drift", 6, "drift tools");

        await fx.Client.DisablePluginAsync("drift", "1.0.0", fx.Ct);
        await fx.WaitForToolsAsync("drift", 0, "drift tools to be unregistered");

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
        await ExtensionAuthoringSupport.StageAndCommitPluginAsync(fx, "bye", "1.0.0");
        await fx.WaitForToolsAsync("bye", 6, "bye tools");

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
        await ExtensionAuthoringSupport.StageAndCommitPluginAsync(fx, "ver", "1.0.0");

        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.DisablePluginAsync("ver", "9.9.9", fx.Ct), "version_mismatch");
        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.EnablePluginAsync("ver", "9.9.9", fx.Ct), "version_mismatch");
        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.DeletePluginAsync("ver", "9.9.9", fx.Ct), "version_mismatch");

        // The installed version is untouched and still runnable.
        await fx.AssertToolContentAsync("ver", "echo", """{"text":"ok"}""", "x:ok");
    }
}
