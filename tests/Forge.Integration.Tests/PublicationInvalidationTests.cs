using Sdk = Forge.Plugin.Sdk;
using Xunit;

namespace Forge.Integration.Tests;

/// <summary>What <c>plugins.status</c> reports after a plugin's manifest or source changes on disk without a refresh.</summary>
public sealed class PublicationInvalidationTests
{
    [Fact]
    public async Task Manifest_edit_without_refresh_is_not_reported_as_an_approved_clean_status()
    {
        await using var fx = new E2eFixture();
        var source = PluginSources.Standard("delta", echoPrefix: "d");
        fx.WritePluginWithManifest("delta", source, PluginSources.ManifestWithPermissions("delta", "1.0.0", "ReadFile"));
        await fx.StartAsync();
        await fx.ApproveViaStatusAndWaitForToolsAsync("delta", PluginSources.AllTools.Length, "the tools of delta");

        var before = await StatusOfAsync(fx, "delta");
        Assert.Equal("approved", before.Status);
        Assert.Null(before.Error);

        fx.WritePluginWithManifest(
            "delta", source, PluginSources.ManifestWithPermissions("delta", "1.0.0", "ReadFile", "Network"));

        var after = await StatusOfAsync(fx, "delta");
        Assert.False(
            after.Status == "approved" && after.Error is null,
            "plugins.status reports delta as approved and error-free although its manifest changed on disk without a refresh.");
    }

    [Fact]
    public async Task Source_edit_without_refresh_fails_closed_at_execution_while_approval_needs_an_explicit_refresh()
    {
        await using var fx = new E2eFixture();
        await fx.StartWithStatusApprovedPluginAsync("delta", "d");
        await fx.WaitForToolsAsync("delta", PluginSources.AllTools.Length, "the tools of delta");

        var before = await fx.InvokeAsync("delta", PluginSources.EchoTool, "{\"text\":\"before\"}");
        Assert.False(before.IsError, before.Content);
        Assert.Equal("d:before", before.Content);

        fx.EditSource("delta", PluginSources.Standard("delta", echoPrefix: "edited"));

        var refused = await fx.InvokeAsync("delta", PluginSources.EchoTool, "{\"text\":\"after\"}");
        Assert.True(refused.IsError, $"an edit without refresh still executed: '{refused.Content}'");
        Assert.Contains("refresh", refused.Content, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<Sdk.PluginStatusInfo> StatusOfAsync(E2eFixture fx, string id)
    {
        var statuses = await fx.Client.GetPluginStatusesAsync(fx.Ct);
        return Assert.Single(statuses, status => string.Equals(status.PluginId, id, StringComparison.Ordinal));
    }
}
