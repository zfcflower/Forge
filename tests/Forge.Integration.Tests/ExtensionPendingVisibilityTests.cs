using Forge.Abstractions;
using Forge.HostClient;
using Xunit;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.Integration.Tests;

/// <summary>
/// Draft invisibility (<c>.pending</c>/<c>.trash</c> never surface) and <c>extensions.pending</c> recovery:
/// restart-safe drafts, per-draft diagnostics, ignored staging leftovers.
/// Timing is event-driven throughout (host replies and file gates); no fixed sleeps.
/// </summary>
public sealed class ExtensionPendingVisibilityTests
{
    [Fact]
    public async Task Staged_draft_never_surfaces_in_status_or_tools()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        var changes = 0;
        fx.Client.ToolsChanged += () => Interlocked.Increment(ref changes);

        var staged = await fx.Client.StageExtensionAsync(
            ExtensionAuthoringSupport.StagePluginRequest("draft1"), fx.Ct);
        Assert.Empty(staged.Diagnostics);

        await fx.Client.RefreshPluginsAsync(fx.Ct);

        var statuses = await fx.Client.GetPluginStatusesAsync(fx.Ct);
        Assert.DoesNotContain(statuses, status => string.Equals(status.PluginId, "draft1", StringComparison.Ordinal));
        Assert.Empty(fx.Client.Tools);
        Assert.Equal(0, Volatile.Read(ref changes));
        Assert.Null(ApprovalDatabase.ReadRecord(fx.DataDirectory, "draft1", "1.0.0"));
        Assert.True(File.Exists(Path.Combine(fx.PluginsDirectory, ".pending", "draft1", "Plugin.cs")));
    }

    [Fact]
    public async Task Dot_directories_are_invisible_even_with_junk_inside()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();

        Directory.CreateDirectory(Path.Combine(fx.PluginsDirectory, ".trash", "oldone"));
        File.WriteAllText(
            Path.Combine(fx.PluginsDirectory, ".trash", "oldone", "manifest.json"),
            """{"id":"oldone","version":"1.0.0"}""");
        Directory.CreateDirectory(Path.Combine(fx.PluginsDirectory, ".pending", "evil"));
        File.WriteAllText(
            Path.Combine(fx.PluginsDirectory, ".pending", "evil", "manifest.json"),
            """{"id":"evil","version":"1.0.0","source":"Missing.cs"}""");

        await fx.Client.RefreshPluginsAsync(fx.Ct);

        var statuses = await fx.Client.GetPluginStatusesAsync(fx.Ct);
        Assert.DoesNotContain(statuses, status => status.PluginId is "oldone" or "evil");
        Assert.Empty(fx.Client.Tools);
    }

    [Fact]
    public async Task Pending_draft_survives_host_restart_and_commits_afterwards()
    {
        await using var host = RestartableHost.Create();
        var client = await host.StartAsync();

        var staged = await client.StageExtensionAsync(
            ExtensionAuthoringSupport.StagePluginRequest("keeper"), host.Ct);
        Assert.Empty(staged.Diagnostics);

        client = await host.RestartAsync();

        var pending = await client.GetPendingExtensionsAsync(host.Ct);
        var restored = Assert.Single(pending, item => item.PluginId == "keeper");
        Assert.Equal("1.0.0", restored.Version);
        Assert.Equal(Sdk.ExtensionKinds.Plugin, restored.Kind);
        Assert.Equal(staged.SourceHash, restored.SourceHash);
        Assert.Equal(staged.DependencyHash, restored.DependencyHash);
        Assert.Equal(staged.SdkVersion, restored.SdkVersion);
        Assert.Empty(restored.Diagnostics);

        var result = await client.CommitExtensionAsync(
            new Sdk.ExtensionCommitRequest("keeper", restored.SourceHash, restored.DependencyHash, restored.SdkVersion),
            host.Ct);
        Assert.Equal("keeper", result.Status.PluginId);
        await host.WaitUntilAsync(() => client.ToolsByPlugin.TryGetValue("keeper", out var tools) && tools.Count == 6, "keeper tools");
    }

    [Fact]
    public async Task One_bad_draft_carries_only_its_own_diagnostics()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();

        var good = await fx.Client.StageExtensionAsync(
            ExtensionAuthoringSupport.StagePluginRequest("goodone"), fx.Ct);
        Assert.Empty(good.Diagnostics);

        var badFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Plugin.cs"] = PluginSources.Broken("badone"),
        };
        var bad = await fx.Client.StageExtensionAsync(
            ExtensionAuthoringSupport.StagePluginRequest("badone", files: badFiles), fx.Ct);
        Assert.NotEmpty(bad.Diagnostics);

        var pending = await fx.Client.GetPendingExtensionsAsync(fx.Ct);
        Assert.Equal(2, pending.Count);
        var keptGood = Assert.Single(pending, item => item.PluginId == "goodone");
        Assert.Empty(keptGood.Diagnostics);
        Assert.False(string.IsNullOrWhiteSpace(keptGood.SourceHash));
        var keptBad = Assert.Single(pending, item => item.PluginId == "badone");
        Assert.NotEmpty(keptBad.Diagnostics);

        // A draft whose manifest vanished (crashed editor, disk loss) is skipped, not fatal.
        File.Delete(Path.Combine(fx.PluginsDirectory, ".pending", "badone", "manifest.json"));

        var pendingAfterLoss = await fx.Client.GetPendingExtensionsAsync(fx.Ct);
        var survivor = Assert.Single(pendingAfterLoss);
        Assert.Equal("goodone", survivor.PluginId);
        Assert.Empty(survivor.Diagnostics);
    }

    [Fact]
    public async Task Interrupted_stage_leftover_is_ignored()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();

        var staged = await fx.Client.StageExtensionAsync(
            ExtensionAuthoringSupport.StagePluginRequest("real"), fx.Ct);
        Assert.Empty(staged.Diagnostics);

        // A half-written stage: temp directory whose manifest declares a different name.
        var leftover = Path.Combine(fx.PluginsDirectory, ".pending", "real.tmp-1234abcd");
        Directory.CreateDirectory(leftover);
        File.WriteAllText(
            Path.Combine(leftover, "manifest.json"),
            """{"id":"someone-else","version":"1.0.0","source":"Plugin.cs"}""");
        File.WriteAllText(Path.Combine(leftover, "Plugin.cs"), PluginSources.Standard("someone-else"));

        var pending = await fx.Client.GetPendingExtensionsAsync(fx.Ct);
        var real = Assert.Single(pending);
        Assert.Equal("real", real.PluginId);
        Assert.DoesNotContain(pending, item => item.PluginId.Contains("tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Draft_registers_nothing_and_spawns_no_worker_process()
    {
        var probeCalls = 0;
        var probe = new ExtensionAuthoringSupport.ProbeTool(
            "probe",
            (_, _) =>
            {
                Interlocked.Increment(ref probeCalls);
                return Task.FromResult(new ToolResult("probe-ok"));
            });
        await using var fx = E2eFixture.AllowingApprovals(probe);
        await fx.StartAsync();

        await fx.Client.StageExtensionAsync(
            ExtensionAuthoringSupport.StageWorkflowRequest(
                "wf-draft", ExtensionAuthoringSupport.SingleStepWorkflow("probe")),
            fx.Ct);
        await fx.Client.RefreshPluginsAsync(fx.Ct);

        var statuses = await fx.Client.GetPluginStatusesAsync(fx.Ct);
        Assert.DoesNotContain(statuses, status => string.Equals(status.PluginId, "wf-draft", StringComparison.Ordinal));
        Assert.Empty(fx.Client.Tools);
        Assert.Equal(0, Volatile.Read(ref probeCalls));
        Assert.Null(ApprovalDatabase.ReadRecord(fx.DataDirectory, "wf-draft", "1.0.0"));
    }
}
