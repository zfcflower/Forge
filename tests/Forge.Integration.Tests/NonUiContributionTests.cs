using System.Collections.Concurrent;
using System.Text.Json;
using Forge.Abstractions;
using Forge.HostClient;
using Forge.Ipc;
using Xunit;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.Integration.Tests;

/// <summary>Non-UI contribution migration: every non-UI kind a plugin registers (tools, executable commands,
/// Markdown workflows, agents, dynamic prompt contributors, panel declarations, permissions) travels from the
/// SDK through the real Host and Worker to the real HostClient, appears only once approved, executes bound to
/// the original context, and disappears again on revoke, refresh or source change. Approval always uses the
/// hashes <c>plugins.status</c> reports; no test in this file reads the approval database.</summary>
public sealed class NonUiContributionTests
{
    [Fact]
    public async Task Status_reports_draft_hashes_before_approval_without_running_register()
    {
        await using var fx = new E2eFixture();
        var marker = fx.WriteContributedPlugin("gamma");
        await fx.StartAsync();

        var status = await fx.WaitForStatusAsync("gamma", "a status row for gamma");
        Assert.NotNull(status);
        Assert.Equal("draft", status!.Status);
        Assert.False(string.IsNullOrWhiteSpace(status.SourceHash));
        Assert.False(string.IsNullOrWhiteSpace(status.DependencyHash));
        Assert.False(string.IsNullOrWhiteSpace(status.SdkVersion));
        Assert.Contains(status.Permissions, permission => permission.Kind == Sdk.PermissionKind.ReadFile);
        Assert.Null(status.Error);

        Assert.False(File.Exists(marker), "plugins.status must not execute plugin code.");
        Assert.Empty(fx.Client.Tools);
        Assert.Empty(fx.Client.Commands);
        Assert.Empty(fx.Client.Workflows);
        Assert.Empty(fx.Client.Agents);
        Assert.Empty(fx.Client.PromptContributors);
    }

    [Fact]
    public async Task Approve_via_status_publishes_tools_and_all_contributions()
    {
        await using var fx = new E2eFixture();
        var marker = fx.WriteContributedPlugin("gamma");

        var contributionsChanged = 0;
        fx.Client.ContributionsChanged += () => Interlocked.Increment(ref contributionsChanged);
        await fx.StartAndApproveViaStatusAsync("gamma");

        await fx.WaitForToolsAsync("gamma", 1, "the tool of gamma");
        await fx.WaitUntilAsync(() => fx.Client.Commands.Count == 1, "the command of gamma");
        Assert.True(File.Exists(marker), "approval must run registration exactly once it is granted.");

        Assert.Equal("cecho", fx.ToolsOf("gamma")[0].Definition.Name);

        var command = Assert.Single(fx.Client.Commands);
        Assert.Equal("greet", command.Definition.Name);
        Assert.Equal("gamma", ((ExtensionHostCommand)command).PluginId);

        var workflow = Assert.Single(fx.Client.Workflows);
        Assert.Equal("guide", workflow.Name);
        Assert.Contains("Do it", workflow.Body, StringComparison.Ordinal);

        var agent = Assert.Single(fx.Client.Agents);
        Assert.Equal("helper", agent.Name);
        Assert.Equal("You are a helper.", agent.Prompt);
        Assert.Equal("model-x", agent.Model.Model);

        Assert.Equal(new PluginPromptReference("gamma", "brief"), Assert.Single(fx.Client.PromptContributors));
        Assert.True(Volatile.Read(ref contributionsChanged) >= 1);
    }

    [Fact]
    public async Task Unapproved_plugin_exposes_no_contributions_and_prompt_returns_null()
    {
        await using var fx = new E2eFixture();
        var marker = fx.WriteContributedPlugin("gamma");
        await fx.StartAsync();
        await fx.WaitForStatusAsync("gamma", "a status row for gamma");

        Assert.Empty(fx.Client.Commands);
        Assert.Empty(fx.Client.Workflows);
        Assert.Empty(fx.Client.Agents);
        Assert.Empty(fx.Client.PromptContributors);

        var prompt = await fx.Client.GetPluginPromptAsync(
            new PluginPromptReference("gamma", "brief"), new ToolContext("unapproved", fx.Root), fx.Ct);
        Assert.Null(prompt);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Command_executes_in_worker_bound_to_original_context()
    {
        var probeContexts = new ConcurrentQueue<ToolContext>();
        var approvals = new ConcurrentQueue<ApprovalRequest>();
        var probe = new RecordingTool(
            "probe",
            (call, context, _) =>
            {
                probeContexts.Enqueue(context);
                return ValueTask.FromResult(new ToolResult("saw:" + context.SessionId + "|" + context.WorkingDirectory));
            });
        await using var fx = new E2eFixture(
            toolResolver: name => name == probe.Definition.Name ? probe : null,
            approvalPath: (request, _) =>
            {
                approvals.Enqueue(request);
                return ValueTask.FromResult(ApprovalDecision.Allow);
            });
        fx.WriteContributedPlugin("gamma");
        await fx.StartAndApproveViaStatusAsync("gamma");
        await fx.WaitUntilAsync(() => fx.Client.Commands.Count == 1, "the command of gamma");

        var command = Assert.Single(fx.Client.Commands);
        var result = await command.ExecuteAsync("arthur", new CommandContext("cmd-session", fx.Root), fx.Ct);

        Assert.False(result.IsError, result.Output);
        Assert.Contains("cmd-session", result.Output, StringComparison.Ordinal);
        Assert.Contains(fx.Root, result.Output, StringComparison.Ordinal);

        var observed = Assert.Single(probeContexts);
        Assert.Equal("cmd-session", observed.SessionId);
        Assert.Equal(fx.Root, observed.WorkingDirectory);

        Assert.Equal(2, approvals.Count);
        Assert.Contains(approvals, a => a.ToolName == ExtensionHostClient.PluginCommandApprovalName("gamma", "greet") && a.SideEffect == ToolSideEffect.Execute);
        Assert.Contains(approvals, a => a.ToolName == "probe");
    }

    [Fact]
    public async Task Prompt_evaluates_in_worker_bound_to_original_context()
    {
        var probeContexts = new ConcurrentQueue<ToolContext>();
        var probe = new RecordingTool(
            "probe",
            (call, context, _) =>
            {
                probeContexts.Enqueue(context);
                return ValueTask.FromResult(new ToolResult("saw:" + context.SessionId + "|" + context.WorkingDirectory));
            });
        await using var fx = E2eFixture.AllowingApprovals(probe);
        fx.WriteContributedPlugin("gamma");
        await fx.StartAndApproveViaStatusAsync("gamma");
        await fx.WaitUntilAsync(() => fx.Client.PromptContributors.Count == 1, "the prompt contributor of gamma");

        var text = await fx.Client.GetPluginPromptAsync(
            new PluginPromptReference("gamma", "brief"), new ToolContext("prompt-session", fx.Root), fx.Ct);

        Assert.NotNull(text);
        Assert.Contains("prompt-session", text, StringComparison.Ordinal);
        Assert.Contains(fx.Root, text, StringComparison.Ordinal);

        var observed = Assert.Single(probeContexts);
        Assert.Equal("prompt-session", observed.SessionId);
        Assert.Equal(fx.Root, observed.WorkingDirectory);
    }

    [Fact]
    public async Task Panel_declaration_accepted_without_ui_renderer()
    {
        await using var fx = new E2eFixture();
        fx.WriteContributedPlugin("gamma");
        await fx.StartAndApproveViaStatusAsync("gamma");
        await fx.WaitForToolsAsync("gamma", 1, "the tool of gamma");

        await fx.AssertToolContentAsync("gamma", "cecho", """{"text":"panel-ok"}""", "c:panel-ok");
        Assert.Contains(
            fx.Client.Diagnostics,
            diagnostic => diagnostic.Contains("panel", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Permission_subset_of_manifest_loads()
    {
        await using var fx = new E2eFixture();
        fx.WriteContributedPlugin("gamma");
        await fx.StartAndApproveViaStatusAsync("gamma");
        await fx.WaitForToolsAsync("gamma", 1, "the tool of gamma");

        await fx.AssertToolContentAsync("gamma", "cecho", """{"text":"subset"}""", "c:subset");
    }

    [Fact]
    public async Task RequirePermission_beyond_manifest_rejects_load()
    {
        await using var fx = new E2eFixture();
        fx.WritePluginWithManifest(
            "greedy",
            PluginSources.Contributed("greedy", Path.Combine(fx.Root, "registered.txt")),
            PluginSources.ManifestWithPermissions("greedy", "1.0.0"));
        await fx.StartAsync();

        await fx.WaitForCleanDraftAsync("greedy");
        Assert.Empty(fx.ToolsOf("greedy"));

        await fx.ApproveViaStatusAndRefreshAsync("greedy");
        Assert.Empty(fx.ToolsOf("greedy"));
        var after = await fx.StatusOfAsync("greedy");
        Assert.False(string.IsNullOrWhiteSpace(after.Error));
    }

    [Fact]
    public async Task Manifest_permission_change_invalidates_old_approval()
    {
        await using var fx = new E2eFixture();
        var marker = fx.WriteContributedPlugin("gamma");
        var source = PluginSources.Contributed("gamma", marker);
        await fx.StartAndApproveViaStatusAsync("gamma");
        await fx.WaitForToolsAsync("gamma", 1, "the tool of gamma");

        var staleTool = fx.ToolOf("gamma", "cecho");
        var before = await fx.InvokeAsync(staleTool, """{"text":"before"}""");
        Assert.False(before.IsError, before.Content);
        var oldStatus = await fx.StatusOfAsync("gamma");

        fx.WritePluginWithManifest(
            "gamma", source, PluginSources.ManifestWithPermissions("gamma", "1.0.0", "ReadFile", "Network"));
        await fx.Client.RefreshPluginsAsync(fx.Ct);

        var stale = await fx.InvokeAsync(staleTool, """{"text":"stale"}""");
        Assert.True(stale.IsError, stale.Content);
        Assert.Contains("must not run", stale.Content, StringComparison.Ordinal);
        Assert.Contains("plugin_not_approved", stale.Content, StringComparison.Ordinal);
        Assert.Contains("was invalidated by a change to the recorded plugin", stale.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("c:stale", stale.Content, StringComparison.Ordinal);
        Assert.Empty(fx.ToolsOf("gamma"));

        var mismatch = await Assert.ThrowsAsync<IpcRemoteException>(() =>
            fx.Client.ApprovePluginAsync("gamma", "1.0.0", oldStatus.SourceHash, oldStatus.DependencyHash, oldStatus.SdkVersion, fx.Ct));
        Assert.Equal("approval_mismatch", mismatch.Code);

        await fx.ApproveViaStatusAndWaitForToolsAsync("gamma", 1, "the tool of gamma after re-approval");
        await fx.AssertToolContentAsync("gamma", "cecho", """{"text":"after"}""", "c:after");
    }

    [Fact]
    public async Task Malformed_manifest_permission_kind_fails_closed()
    {
        await using var fx = new E2eFixture();
        fx.WritePluginWithManifest(
            "bogus",
            PluginSources.Standard("bogus"),
            """{"id":"bogus","version":"1.0.0","source":"Plugin.cs","permissions":[{"kind":"Bogus"}]}""");
        await fx.StartAsync();
        await fx.Client.RefreshPluginsAsync(fx.Ct);

        var statuses = await fx.Client.GetPluginStatusesAsync(fx.Ct);
        Assert.DoesNotContain(statuses, status => status.PluginId == "bogus");
        Assert.Empty(fx.ToolsOf("bogus"));

        var refused = await Assert.ThrowsAsync<IpcRemoteException>(() =>
            fx.Client.ApprovePluginAsync("bogus", "1.0.0", new string('a', 64), new string('b', 64), "x", fx.Ct));
        Assert.Equal("unknown_plugin", refused.Code);
    }

    [Fact]
    public async Task Undefined_runtime_permission_kind_rejects_load()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("shady", PluginSources.BadRuntimePermission("shady"));
        await fx.StartAsync();

        await fx.WaitForCleanDraftAsync("shady");
        Assert.Empty(fx.ToolsOf("shady"));

        await fx.ApproveViaStatusAndRefreshAsync("shady");
        Assert.Empty(fx.ToolsOf("shady"));
        Assert.Empty(fx.Client.Commands);
        var after = await fx.StatusOfAsync("shady");
        Assert.Contains("undefined permission", after.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Undefined_thinking_effort_rejects_load()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("dreamer", PluginSources.BadThinking("dreamer"));
        await fx.StartAsync();

        await fx.WaitForCleanDraftAsync("dreamer");
        Assert.Empty(fx.ToolsOf("dreamer"));

        await fx.ApproveViaStatusAndRefreshAsync("dreamer");
        Assert.Empty(fx.ToolsOf("dreamer"));
        Assert.Empty(fx.Client.Agents);
        var after = await fx.StatusOfAsync("dreamer");
        Assert.Contains("undefined thinking", after.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Plain_command_declaration_without_body_rejected()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("dud", PluginSources.PlainCommand("dud"));
        await fx.StartAsync();

        await fx.WaitForCleanDraftAsync("dud");
        Assert.Empty(fx.Client.Commands);

        await fx.ApproveViaStatusAndRefreshAsync("dud");
        Assert.Empty(fx.Client.Commands);
        Assert.Empty(fx.Client.Tools);
        var after = await fx.StatusOfAsync("dud");
        Assert.Contains("without an executable body", after.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Empty_workflow_body_rejects_load()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("hollow", PluginSources.EmptyBodyWorkflow("hollow"));
        await fx.StartAsync();

        await fx.WaitForCleanDraftAsync("hollow");
        Assert.Empty(fx.ToolsOf("hollow"));

        await fx.ApproveViaStatusAndRefreshAsync("hollow");
        Assert.Empty(fx.ToolsOf("hollow"));
        Assert.Empty(fx.Client.Workflows);
        var after = await fx.StatusOfAsync("hollow");
        Assert.Contains("has no body", after.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unknown_panel_schema_kind_rejects_load()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("billboard", PluginSources.BadPanelKind("billboard"));
        await fx.StartAsync();

        await fx.WaitForCleanDraftAsync("billboard");
        Assert.Empty(fx.ToolsOf("billboard"));

        await fx.ApproveViaStatusAndRefreshAsync("billboard");
        Assert.Empty(fx.ToolsOf("billboard"));
        var after = await fx.StatusOfAsync("billboard");
        Assert.Contains("hologram", after.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Throwing_register_surfaces_metadata_error_in_status()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("regboom", ThrowingSource("regboom"));
        await fx.StartAsync();

        await fx.WaitForCleanDraftAsync("regboom");
        Assert.Empty(fx.ToolsOf("regboom"));
        Assert.Empty(fx.Client.Commands);

        await fx.ApproveViaStatusAndRefreshAsync("regboom");
        Assert.Empty(fx.ToolsOf("regboom"));
        Assert.Empty(fx.Client.Commands);
        var after = await fx.StatusOfAsync("regboom");
        Assert.Contains("reg-boom", after.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Compile_failure_reports_invalid_and_approve_throws_not_loadable()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("broken", PluginSources.Broken("broken"));
        await fx.StartAsync();

        var status = await fx.WaitForStatusAsync("broken", "a status row for broken");
        Assert.NotNull(status);
        Assert.Equal("invalid", status!.Status);
        Assert.False(string.IsNullOrWhiteSpace(status.Error));
        Assert.Empty(fx.ToolsOf("broken"));

        var refused = await Assert.ThrowsAsync<IpcRemoteException>(() =>
            fx.Client.ApprovePluginAsync("broken", "1.0.0", status.SourceHash, status.DependencyHash, status.SdkVersion, fx.Ct));
        Assert.Equal("plugin_not_loadable", refused.Code);
        Assert.Empty(fx.ToolsOf("broken"));
    }

    [Fact]
    public async Task Legacy_plugin_json_reports_incompatible_state_and_never_loads()
    {
        await using var fx = new E2eFixture();
        var legacy = Path.Combine(fx.PluginsDirectory, "legacy");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(
            Path.Combine(legacy, "plugin.json"),
            """{"id":"legacy","version":"1.0.0","entry":"legacy.dll"}""");
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        await fx.StartAsync();
        await fx.Client.RefreshPluginsAsync(fx.Ct);

        var statuses = await fx.Client.GetPluginStatusesAsync(fx.Ct);
        Assert.DoesNotContain(statuses, status => status.PluginId == "legacy");

        var refused = await Assert.ThrowsAsync<IpcRemoteException>(() =>
            fx.Client.ApprovePluginAsync("legacy", "1.0.0", new string('a', 64), new string('b', 64), "x", fx.Ct));
        Assert.Equal("unknown_plugin", refused.Code);

        await fx.ApproveViaStatusAndWaitForToolsAsync("alpha", PluginSources.AllTools.Length, "alpha tools");
        Assert.All(fx.Client.Tools, tool => Assert.Equal("alpha", ((ExtensionHostTool)tool).PluginId));
        await fx.AssertToolContentAsync("alpha", "echo", """{"text":"healthy"}""", "a:healthy");
    }

    [Fact]
    public async Task Legacy_plugin_json_diagnostic_is_written_to_host_stderr()
    {
        await using var host = RawHost.StartNew();
        using var budget = new CancellationTokenSource(TestBudgets.Safety(TimeSpan.FromSeconds(120), TimeSpan.FromMinutes(10)));
        var legacy = Path.Combine(host.Plugins, "legacy");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(
            Path.Combine(legacy, "plugin.json"),
            """{"id":"legacy","version":"1.0.0","entry":"legacy.dll"}""");

        await using var peer = await RawHost.ConnectAsync(host.Pipe, budget.Token);
        await peer.RequestAsync(
            Sdk.ExtensionProtocol.PluginsRefresh, IpcEmpty.Instance, budget.Token);

        var diagnostic = await host.WaitForStderrAsync("legacy plugin.json", TestBudgets.Condition);
        Assert.Contains("not compatible", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("manifest.json", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Revoke_removes_tools_and_contributions_and_stale_proxies_fail()
    {
        await using var fx = E2eFixture.AllowingApprovals();
        fx.WriteContributedPlugin("gamma");
        await fx.StartAndApproveViaStatusAsync("gamma");
        await fx.WaitUntilAsync(() => fx.Client.Commands.Count == 1, "the command of gamma");

        var staleTool = fx.ToolOf("gamma", "cecho");
        var staleCommand = Assert.Single(fx.Client.Commands);
        var prompt = Assert.Single(fx.Client.PromptContributors);
        Assert.Single(fx.Client.Workflows);
        Assert.Single(fx.Client.Agents);

        await fx.Client.RevokePluginAsync("gamma", "1.0.0", fx.Ct);
        await fx.WaitForToolsAsync("gamma", 0, "gamma tools after revoke");
        await fx.WaitUntilAsync(() => fx.Client.Commands.Count == 0, "gamma commands after revoke");
        Assert.Empty(fx.Client.Tools);
        Assert.Empty(fx.Client.Workflows);
        Assert.Empty(fx.Client.Agents);
        Assert.Empty(fx.Client.PromptContributors);

        var stale = await fx.InvokeAsync(staleTool, """{"text":"x"}""");
        Assert.True(stale.IsError, stale.Content);
        Assert.Contains("plugin_not_approved", stale.Content, StringComparison.Ordinal);
        Assert.Contains("is not approved", stale.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("c:x", stale.Content, StringComparison.Ordinal);
        Assert.Empty(fx.ToolsOf("gamma"));

        var staleCommandResult = await staleCommand.ExecuteAsync(
            "x", new CommandContext("revoked", fx.Root), fx.Ct);
        Assert.True(staleCommandResult.IsError, staleCommandResult.Output);
        Assert.Contains("plugin_not_approved", staleCommandResult.Output, StringComparison.Ordinal);
        Assert.Contains("is not approved", staleCommandResult.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("cmd:", staleCommandResult.Output, StringComparison.Ordinal);

        var stalePrompt = await fx.Client.GetPluginPromptAsync(
            prompt, new ToolContext("revoked", fx.Root), fx.Ct);
        Assert.Null(stalePrompt);

        var status = await fx.WaitForStatusAsync("gamma", "a status row for gamma after revoke");
        Assert.Equal("draft", status!.Status);
    }

    [Fact]
    public async Task Source_change_and_refresh_removes_snapshots_until_reapproved()
    {
        await using var fx = E2eFixture.AllowingApprovals();
        var marker = fx.WriteContributedPlugin("gamma");
        var source = PluginSources.Contributed("gamma", marker);
        await fx.StartAndApproveViaStatusAsync("gamma");
        await fx.WaitUntilAsync(() => fx.Client.Commands.Count == 1, "the command of gamma");

        fx.EditSource("gamma", source + "\n// touched\n");
        await fx.Client.RefreshPluginsAsync(fx.Ct);
        await fx.WaitForToolsAsync("gamma", 0, "gamma tools after the source change");
        await fx.WaitUntilAsync(() => fx.Client.Commands.Count == 0, "gamma commands after the source change");
        Assert.Empty(fx.Client.Workflows);
        Assert.Empty(fx.Client.Agents);
        Assert.Empty(fx.Client.PromptContributors);

        var status = await fx.WaitForStatusAsync("gamma", "a status row for gamma after the edit");
        Assert.NotNull(status);
        Assert.False(string.IsNullOrWhiteSpace(status!.Error));

        await fx.ApproveViaStatusAsync("gamma");
        await fx.WaitUntilAsync(() => fx.Client.Commands.Count == 1, "the command of gamma after re-approval");
        Assert.Single(fx.Client.Workflows);
        Assert.Single(fx.Client.Agents);
        Assert.Single(fx.Client.PromptContributors);
        await fx.AssertToolContentAsync("gamma", "cecho", """{"text":"back"}""", "c:back");
    }

    private static string ThrowingSource(string id) =>
        $$"""
        public sealed class TestPlugin : IPlugin
        {
            public PluginInfo Info => new("{{id}}", "1.0.0", "throwing registration test plugin");

            public void Register(IRegistrationContext context)
            {
                throw new InvalidOperationException("reg-boom from Register");
            }
        }
        """;

    private sealed class RecordingTool : ITool
    {
        private readonly Func<ToolCall, ToolContext, CancellationToken, ValueTask<ToolResult>> invoke;

        public RecordingTool(
            string name,
            Func<ToolCall, ToolContext, CancellationToken, ValueTask<ToolResult>> invoke)
        {
            Definition = new ToolDefinition(name, "records its context", """{"type":"object"}""");
            this.invoke = invoke;
        }

        public ToolDefinition Definition { get; }

        public ToolSideEffect SideEffect => ToolSideEffect.Execute;

        public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct) =>
            invoke(call, context, ct);
    }
}
