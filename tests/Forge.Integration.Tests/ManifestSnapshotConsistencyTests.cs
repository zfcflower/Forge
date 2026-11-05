using System.Text.Json;
using Forge.Abstractions;
using Forge.Ipc;
using Xunit;

namespace Forge.Integration.Tests;

/// <summary>Manifest snapshot consistency: editing the manifest without a refresh must fail closed for tools,
/// commands, prompts and approvals without running any handler, and the status fingerprint must change only after
/// the refresh. The approval database is never read; every hash comes from <c>plugins.status</c>.</summary>
public sealed class ManifestSnapshotConsistencyTests
{
    [Fact]
    public async Task Manifest_edit_without_refresh_fails_closed_then_fingerprint_changes_after_refresh()
    {
        await using var fx = new E2eFixture();
        var marker = Path.Combine(fx.Root, "registered.txt");
        var source = PluginSources.Contributed("gamma", marker);
        fx.WritePluginWithManifest("gamma", source, PluginSources.ManifestWithPermissions("gamma", "1.0.0", "ReadFile"));
        await fx.StartAsync();
        await fx.ApproveViaStatusAsync("gamma");
        await fx.WaitUntilAsync(() => fx.ToolsOf("gamma").Count == 1, "the tool of gamma");
        await fx.WaitUntilAsync(() => fx.Client.Commands.Count == 1, "the command of gamma");
        var beforeStatus = (await fx.Client.GetPluginStatusesAsync(fx.Ct)).Single(s => s.PluginId == "gamma");
        var registeredLines = File.ReadAllLines(marker).Length;

        var staleTool = fx.ToolOf("gamma", "cecho");
        var staleCommand = Assert.Single(fx.Client.Commands);
        var stalePrompt = Assert.Single(fx.Client.PromptContributors);

        fx.WritePluginWithManifest(
            "gamma", source, PluginSources.ManifestWithPermissions("gamma", "1.0.0", "ReadFile", "Network"));

        var staleToolResult = await fx.InvokeAsync(staleTool, """{"text":"edited"}""");
        Assert.True(staleToolResult.IsError, staleToolResult.Content);
        Assert.Contains("plugin_not_approved", staleToolResult.Content, StringComparison.Ordinal);
        Assert.Contains("changed on disk; refresh", staleToolResult.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("c:edited", staleToolResult.Content, StringComparison.Ordinal);

        var staleCommandResult = await staleCommand.ExecuteAsync(
            "edited", new CommandContext("edited", fx.Root), fx.Ct);
        Assert.True(staleCommandResult.IsError, staleCommandResult.Output);
        Assert.DoesNotContain("cmd:", staleCommandResult.Output, StringComparison.Ordinal);

        var stalePromptText = await fx.Client.GetPluginPromptAsync(
            stalePrompt, new ToolContext("edited", fx.Root), fx.Ct);
        Assert.Null(stalePromptText);

        var refused = await Assert.ThrowsAsync<IpcRemoteException>(() =>
            fx.Client.ApprovePluginAsync(
                "gamma", "1.0.0", beforeStatus.SourceHash, beforeStatus.DependencyHash, beforeStatus.SdkVersion, fx.Ct));
        Assert.Equal("approval_mismatch", refused.Code);
        Assert.Equal(registeredLines, File.ReadAllLines(marker).Length);

        await fx.Client.RefreshPluginsAsync(fx.Ct);
        var afterStatus = await fx.WaitForStatusAsync("gamma", "a status row for gamma after refresh");
        Assert.NotNull(afterStatus);
        Assert.Equal(beforeStatus.SourceHash, afterStatus!.SourceHash);
        Assert.NotEqual(beforeStatus.DependencyHash, afterStatus.DependencyHash);

        await fx.ApproveViaStatusAsync("gamma");
        await fx.WaitUntilAsync(() => fx.ToolsOf("gamma").Count == 1, "the tool of gamma after re-approval");
        var after = await fx.InvokeAsync("gamma", "cecho", """{"text":"back"}""");
        Assert.False(after.IsError, after.Content);
        Assert.Equal("c:back", after.Content);
    }
}
