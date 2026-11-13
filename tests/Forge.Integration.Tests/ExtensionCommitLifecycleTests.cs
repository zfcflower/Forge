using Forge.HostClient;
using Xunit;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.Integration.Tests;

/// <summary>
/// <c>extensions.commit</c>: hash mismatch changes nothing, broken drafts cannot commit, success registers
/// tools and empties <c>.trash</c>, same-version and cross-version updates behave, and a failed commit
/// leaves the old version runnable. All waits are event-driven (tool tables, records, files).
/// </summary>
public sealed class ExtensionCommitLifecycleTests
{
    [Fact]
    public async Task Hash_mismatch_changes_nothing()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();

        var staged = await fx.Client.StageExtensionAsync(
            ExtensionAuthoringSupport.StagePluginRequest("mm"), fx.Ct);
        Assert.Empty(staged.Diagnostics);

        // Tamper with the staged bytes after the user saw the hashes.
        var pendingFile = Path.Combine(fx.PluginsDirectory, ".pending", "mm", "Plugin.cs");
        File.AppendAllText(pendingFile, "\n// tampered after approval\n");
        var before = ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory);

        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => ExtensionAuthoringSupport.CommitAsync(fx, staged),
            "approval_mismatch");

        ExtensionAuthoringSupport.AssertSameTree(
            before, ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory), "failed commit");
        Assert.False(Directory.Exists(ExtensionAuthoringSupport.LiveDirectory(fx.PluginsDirectory, "mm")));
        Assert.Null(ApprovalDatabase.ReadRecord(fx.DataDirectory, "mm", "1.0.0"));
        Assert.Empty(fx.Client.Tools);
        Assert.True(Directory.Exists(ExtensionAuthoringSupport.PendingDirectory(fx.PluginsDirectory, "mm")));
    }

    [Fact]
    public async Task Failed_commit_keeps_the_old_version_runnable()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();

        await ExtensionAuthoringSupport.StageAndCommitPluginAsync(fx, "keep", version: "1.0.0");
        await fx.WaitForToolsAsync("keep", 6, "keep tools");
        var liveBefore = ExtensionAuthoringSupport.SnapshotTree(
            ExtensionAuthoringSupport.LiveDirectory(fx.PluginsDirectory, "keep"));
        var recordBefore = fx.ReadRecord("keep", "1.0.0")!;

        var second = await fx.Client.StageExtensionAsync(
            ExtensionAuthoringSupport.StagePluginRequest("keep", version: "1.0.0"), fx.Ct);
        Assert.True(second.IsUpdate);
        File.AppendAllText(
            Path.Combine(fx.PluginsDirectory, ".pending", "keep", "Plugin.cs"), "\n// tampered\n");

        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => ExtensionAuthoringSupport.CommitAsync(fx, second),
            "approval_mismatch");

        ExtensionAuthoringSupport.AssertSameTree(
            liveBefore,
            ExtensionAuthoringSupport.SnapshotTree(ExtensionAuthoringSupport.LiveDirectory(fx.PluginsDirectory, "keep")),
            "live directory after failed commit");
        var recordAfter = fx.ReadRecord("keep", "1.0.0")!;
        Assert.Equal("Approved", recordAfter.Status);
        Assert.Equal(recordBefore.SourceHash, recordAfter.SourceHash);
        await fx.AssertToolContentAsync("keep", "echo", """{"text":"still-here"}""", "x:still-here");
    }

    [Fact]
    public async Task Draft_with_diagnostics_cannot_commit()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();

        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Plugin.cs"] = PluginSources.Broken("broken"),
        };
        var staged = await fx.Client.StageExtensionAsync(
            ExtensionAuthoringSupport.StagePluginRequest("broken", files: files), fx.Ct);
        Assert.NotEmpty(staged.Diagnostics);
        var before = ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory);

        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => ExtensionAuthoringSupport.CommitAsync(fx, staged),
            "invalid_extension");

        ExtensionAuthoringSupport.AssertSameTree(
            before, ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory), "broken commit");
        Assert.False(Directory.Exists(ExtensionAuthoringSupport.LiveDirectory(fx.PluginsDirectory, "broken")));
        Assert.Null(ApprovalDatabase.ReadRecord(fx.DataDirectory, "broken", "1.0.0"));
        Assert.Empty(fx.Client.Tools);
    }

    [Fact]
    public async Task Successful_commit_registers_tools_and_empties_trash()
    {
        await using var fx = new E2eFixture();
        var changes = 0;
        fx.Client.ToolsChanged += () => Interlocked.Increment(ref changes);
        await fx.StartAsync();

        var staged = await fx.Client.StageExtensionAsync(
            ExtensionAuthoringSupport.StagePluginRequest("ship"), fx.Ct);
        Assert.Empty(staged.Diagnostics);

        var result = await ExtensionAuthoringSupport.CommitAsync(fx, staged);

        Assert.Equal("ship", result.Status.PluginId);
        Assert.Equal("1.0.0", result.Status.Version);
        Assert.Equal(
            PluginSources.AllTools.OrderBy(name => name, StringComparer.Ordinal),
            result.ToolNames.OrderBy(name => name, StringComparer.Ordinal));
        await fx.WaitForToolsAsync("ship", PluginSources.AllTools.Length, "ship tools");
        Assert.True(Volatile.Read(ref changes) >= 1);
        Assert.Empty(ExtensionAuthoringSupport.TrashEntries(fx.PluginsDirectory));
        Assert.False(Directory.Exists(ExtensionAuthoringSupport.PendingDirectory(fx.PluginsDirectory, "ship")));
        Assert.Equal("Approved", fx.ReadRecord("ship", "1.0.0")!.Status);

        await fx.AssertToolContentAsync("ship", "echo", """{"text":"live"}""", "x:live");
    }

    [Fact]
    public async Task Same_version_update_reports_previous_text_and_switches_code()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();

        var v1Files = ExtensionAuthoringSupport.PluginFiles("upd", "1.0.0", echoPrefix: "v1");
        await ExtensionAuthoringSupport.StageAndCommitPluginAsync(fx, "upd", echoPrefix: "v1");
        await fx.WaitForToolsAsync("upd", 6, "upd v1 tools");

        var second = await fx.Client.StageExtensionAsync(
            ExtensionAuthoringSupport.StagePluginRequest(
                "upd", files: ExtensionAuthoringSupport.PluginFiles("upd", "1.0.0", echoPrefix: "v2")),
            fx.Ct);
        Assert.True(second.IsUpdate);
        Assert.Equal("1.0.0", second.LiveVersion);
        var previous = Assert.Single(second.Files, file => file.Path == "Plugin.cs").PreviousText;
        Assert.Equal(v1Files["Plugin.cs"], previous);

        await ExtensionAuthoringSupport.CommitAsync(fx, second);
        await fx.WaitForToolsAsync("upd", 6, "upd v2 tools");

        Assert.Equal("Approved", fx.ReadRecord("upd", "1.0.0")!.Status);
        Assert.Empty(ExtensionAuthoringSupport.TrashEntries(fx.PluginsDirectory));
    }

    [Fact]
    public async Task Same_version_update_serves_the_new_code()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();

        await ExtensionAuthoringSupport.StageAndCommitPluginAsync(fx, "upd2", echoPrefix: "v1");
        await fx.WaitForToolsAsync("upd2", 6, "upd2 v1 tools");

        var second = await fx.Client.StageExtensionAsync(
            ExtensionAuthoringSupport.StagePluginRequest(
                "upd2", files: ExtensionAuthoringSupport.PluginFiles("upd2", "1.0.0", echoPrefix: "v2")),
            fx.Ct);
        await ExtensionAuthoringSupport.CommitAsync(fx, second);
        await fx.WaitForToolsAsync("upd2", 6, "upd2 v2 tools");

        await fx.AssertToolContentAsync("upd2", "echo", """{"text":"after"}""", "v2:after");
    }

    [Fact]
    public async Task Cross_version_update_revokes_the_old_version()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();

        await ExtensionAuthoringSupport.StageAndCommitPluginAsync(fx, "cross", version: "1.0.0");
        await fx.WaitForToolsAsync("cross", 6, "cross v1 tools");

        var second = await fx.Client.StageExtensionAsync(
            ExtensionAuthoringSupport.StagePluginRequest(
                "cross", version: "2.0.0",
                files: ExtensionAuthoringSupport.PluginFiles("cross", "2.0.0", echoPrefix: "v2")),
            fx.Ct);
        Assert.True(second.IsUpdate);
        Assert.Equal("1.0.0", second.LiveVersion);
        var previousCross = second.Files.Single(file => file.Path == "Plugin.cs").PreviousText;
        Assert.NotNull(previousCross);

        await ExtensionAuthoringSupport.CommitAsync(fx, second);
        await fx.WaitForToolsAsync("cross", 6, "cross v2 tools");

        var oldRecord = fx.ReadRecord("cross", "1.0.0")!;
        Assert.NotEqual("Approved", oldRecord.Status);
        Assert.Equal("Approved", fx.ReadRecord("cross", "2.0.0")!.Status);
        Assert.Empty(ExtensionAuthoringSupport.TrashEntries(fx.PluginsDirectory));
    }

    [Fact]
    public async Task Restart_after_cross_version_update_does_not_restore_the_old_approval()
    {
        await using var host = RestartableHost.Create();
        var client = await host.StartAsync();

        await ExtensionAuthoringSupport.StageAndCommitPluginAsync(client, host.Ct, "keepr", version: "1.0.0");
        await host.WaitUntilAsync(
            () => host.Client.ToolsByPlugin.TryGetValue("keepr", out var tools) && tools.Count == 6, "keepr v1 tools");

        var secondStaged = await client.StageExtensionAsync(
            ExtensionAuthoringSupport.StagePluginRequest(
                "keepr", version: "2.0.0",
                files: ExtensionAuthoringSupport.PluginFiles("keepr", "2.0.0", echoPrefix: "v2")),
            host.Ct);
        await ExtensionAuthoringSupport.CommitAsync(client, host.Ct, secondStaged);
        await host.WaitUntilAsync(
            () => host.Client.ToolsByPlugin.TryGetValue("keepr", out var tools) && tools.Count == 6, "keepr v2 tools");

        client = await host.RestartAsync();

        Assert.NotEqual("Approved", ApprovalDatabase.ReadRecord(host.DataDirectory, "keepr", "1.0.0")!.Status);
        Assert.Equal("Approved", ApprovalDatabase.ReadRecord(host.DataDirectory, "keepr", "2.0.0")!.Status);
        await host.WaitUntilAsync(
            () => host.Client.ToolsByPlugin.TryGetValue("keepr", out var tools) && tools.Count == 6, "keepr v2 tools after restart");
        var statuses = await client.GetPluginStatusesAsync(host.Ct);
        Assert.DoesNotContain(statuses, status => status.PluginId == "keepr" && status.Version == "1.0.0");
    }

    [Fact]
    public async Task Cross_version_update_serves_the_new_code()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();

        await ExtensionAuthoringSupport.StageAndCommitPluginAsync(fx, "cross2", version: "1.0.0");
        await fx.WaitForToolsAsync("cross2", 6, "cross2 v1 tools");

        var second = await fx.Client.StageExtensionAsync(
            ExtensionAuthoringSupport.StagePluginRequest(
                "cross2", version: "2.0.0",
                files: ExtensionAuthoringSupport.PluginFiles("cross2", "2.0.0", echoPrefix: "v2")),
            fx.Ct);
        await ExtensionAuthoringSupport.CommitAsync(fx, second);
        await fx.WaitForToolsAsync("cross2", 6, "cross2 v2 tools");

        await fx.AssertToolContentAsync("cross2", "echo", """{"text":"new"}""", "v2:new");
    }
}
