using Forge.Abstractions;
using Forge.App.Approval;
using Forge.Core.Tools;
using Forge.HostClient;
using Forge.Ipc;
using Forge.Plugin.Sdk;
using ToolSideEffect = Forge.Abstractions.ToolSideEffect;
using Xunit;

namespace Forge.App.Tests.Approval;

public sealed class ExtensionAuthoringServiceTests
{
    private sealed class Harness : IAsyncDisposable
    {
        private Harness(
            string dir,
            ApprovalRules rules,
            ApprovalQueue queue,
            FakeAuthoringClient authoring,
            FakeContributionsClient contributions,
            ExtensionAuthoringService service)
        {
            Dir = dir;
            Rules = rules;
            Queue = queue;
            Authoring = authoring;
            Contributions = contributions;
            Service = service;
        }

        public string Dir { get; }

        public ApprovalRules Rules { get; }

        public ApprovalQueue Queue { get; }

        public FakeAuthoringClient Authoring { get; }

        public FakeContributionsClient Contributions { get; }

        public ExtensionAuthoringService Service { get; }

        public static Harness Create(Func<string, IReadOnlyList<string>>? toolNames = null)
        {
            var dir = ApprovalFixtures.TempDir("forge-authoring-");
            var rules = new ApprovalRules(Path.Combine(dir, ApprovalRules.FileName), debounceMilliseconds: 0);
            var queue = new ApprovalQueue(InlineDispatcher.Instance);
            var authoring = new FakeAuthoringClient();
            var contributions = new FakeContributionsClient();
            var service = new ExtensionAuthoringService(
                authoring,
                contributions,
                toolNames ?? (_ => []),
                queue,
                rules,
                new ApprovalSessionDirectory());
            return new Harness(dir, rules, queue, authoring, contributions, service);
        }

        public async ValueTask DisposeAsync() => await Rules.DisposeAsync();

        public void DisposeDir() => ApprovalFixtures.DeleteTree(Dir);
    }

    private static ToolContext Context(string sessionId = "s1") => new(sessionId, Path.GetTempPath());

    private static async Task<ExtensionSubmitResult> WaitSubmitAsync(Task<ExtensionSubmitResult> task) =>
        await task.WaitAsync(ApprovalFixtures.StepTimeout);

    [Fact]
    public async Task Create_when_the_id_exists_is_invalid_and_never_stages()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            harness.Contributions.Statuses = [ApprovalFixtures.Installed("plug")];

            var result = await WaitSubmitAsync(harness.Service.SubmitAsync(
                ApprovalFixtures.PluginDraft("plug"), Context(), CancellationToken.None));

            Assert.Equal(ExtensionSubmitOutcome.Invalid, result.Outcome);
            Assert.Contains("already exists", result.Message);
            Assert.Empty(harness.Authoring.StageCalls);
            Assert.Equal(0, await harness.Queue.CountAsync());
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Update_when_the_id_is_missing_is_invalid()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            harness.Contributions.Statuses = [];

            var result = await WaitSubmitAsync(harness.Service.SubmitAsync(
                ApprovalFixtures.PluginDraft("plug", isUpdate: true), Context(), CancellationToken.None));

            Assert.Equal(ExtensionSubmitOutcome.Invalid, result.Outcome);
            Assert.Contains("nothing to update", result.Message);
            Assert.Empty(harness.Authoring.StageCalls);
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Uninstalled_counts_as_absent_for_create_and_update()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            harness.Contributions.Statuses = [ApprovalFixtures.Installed("plug", status: "Uninstalled")];

            var cardTask = ApprovalFixtures.NextExtensionCardAsync(harness.Queue);
            var pending = harness.Service.SubmitAsync(ApprovalFixtures.PluginDraft("plug"), Context(), CancellationToken.None);
            var card = await cardTask.WaitAsync(ApprovalFixtures.StepTimeout);
            Assert.Single(harness.Authoring.StageCalls);

            card.RejectCommand.Execute(null);
            var result = await WaitSubmitAsync(pending);
            Assert.Equal(ExtensionSubmitOutcome.Rejected, result.Outcome);

            var update = await WaitSubmitAsync(harness.Service.SubmitAsync(
                ApprovalFixtures.PluginDraft("plug", isUpdate: true), Context(), CancellationToken.None));
            Assert.Equal(ExtensionSubmitOutcome.Invalid, update.Outcome);
            Assert.Contains("nothing to update", update.Message);
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public void Empty_id_and_version_are_invalid()
    {
        Assert.False(ExtensionAuthoringService.TryMap(
            ApprovalFixtures.PluginDraft(string.Empty), out _, out var idProblem));
        Assert.Contains("id is empty", idProblem);

        var draft = ApprovalFixtures.PluginDraft("plug") with { Version = "  " };
        Assert.False(ExtensionAuthoringService.TryMap(draft, out _, out var versionProblem));
        Assert.Contains("version is empty", versionProblem);
    }

    [Fact]
    public async Task Unknown_permission_name_is_invalid()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            var draft = ApprovalFixtures.PluginDraft("plug") with
            {
                Permissions = [new ExtensionPermissionDraft("Fly", null)],
            };

            var result = await WaitSubmitAsync(harness.Service.SubmitAsync(draft, Context(), CancellationToken.None));

            Assert.Equal(ExtensionSubmitOutcome.Invalid, result.Outcome);
            Assert.Contains("not a known permission", result.Message);
            Assert.Empty(harness.Authoring.StageCalls);
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Unknown_kind_is_invalid()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;

            var result = await WaitSubmitAsync(harness.Service.SubmitAsync(
                ApprovalFixtures.PluginDraft("plug", kind: "wasm"), Context(), CancellationToken.None));

            Assert.Equal(ExtensionSubmitOutcome.Invalid, result.Outcome);
            Assert.Contains("not a supported extension kind", result.Message);
            Assert.Empty(harness.Authoring.StageCalls);
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Workflow_without_a_tool_is_invalid_but_with_a_tool_it_stages()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            var bare = ApprovalFixtures.PluginDraft("flow", kind: ExtensionDraftKinds.Workflow) with { Tool = null };
            var missing = await WaitSubmitAsync(harness.Service.SubmitAsync(bare, Context(), CancellationToken.None));

            Assert.Equal(ExtensionSubmitOutcome.Invalid, missing.Outcome);
            Assert.Contains("must declare", missing.Message);
            Assert.Empty(harness.Authoring.StageCalls);

            var cardTask = ApprovalFixtures.NextExtensionCardAsync(harness.Queue);
            var pending = harness.Service.SubmitAsync(
                ApprovalFixtures.PluginDraft("flow", kind: ExtensionDraftKinds.Workflow), Context(), CancellationToken.None);
            await cardTask.WaitAsync(ApprovalFixtures.StepTimeout);
            Assert.Single(harness.Authoring.StageCalls);
            Assert.Equal(ExtensionKinds.Workflow, harness.Authoring.StageCalls[0].Kind);
            Assert.NotNull(harness.Authoring.StageCalls[0].Tool);

            var card = await harness.Queue.FindAsync("extension:flow");
            Assert.NotNull(card);
            card.TryComplete(ApprovalCardOutcome.Deny);
            Assert.Equal(ExtensionSubmitOutcome.Rejected, (await WaitSubmitAsync(pending)).Outcome);
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Draft_without_files_is_invalid()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            var draft = ApprovalFixtures.PluginDraft("plug") with
            {
                Files = new Dictionary<string, string>(),
            };

            var result = await WaitSubmitAsync(harness.Service.SubmitAsync(draft, Context(), CancellationToken.None));

            Assert.Equal(ExtensionSubmitOutcome.Invalid, result.Outcome);
            Assert.Contains("no files", result.Message);
            Assert.Empty(harness.Authoring.StageCalls);
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Diagnostics_are_invalid_and_never_reach_the_user()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            harness.Authoring.StageHandler = request =>
                ApprovalFixtures.Staged(request.PluginId, request.Version, diagnostics: ["error CS0001: boom"]);

            var result = await WaitSubmitAsync(harness.Service.SubmitAsync(
                ApprovalFixtures.PluginDraft("plug"), Context(), CancellationToken.None));

            Assert.Equal(ExtensionSubmitOutcome.Invalid, result.Outcome);
            Assert.Contains("was not shown to the user", result.Message);
            Assert.Contains("boom", result.Message);
            Assert.Equal(0, await harness.Queue.CountAsync());
            Assert.Empty(harness.Authoring.CommitCalls);
            Assert.Empty(harness.Authoring.DiscardCalls);
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Approve_commits_exactly_the_shown_hashes_and_clears_old_and_new_tool_rules()
    {
        var harness = Harness.Create(toolNames: static _ => ["old_tool"]);
        try
        {
            await using var _ = harness;
            harness.Rules.Allow(ApprovalRuleKey.Global("old_tool", ToolSideEffect.Execute));
            harness.Rules.Allow(ApprovalRuleKey.Global("new_tool", ToolSideEffect.Execute));
            var staged = ApprovalFixtures.Staged(
                "plug", sourceHash: "sha-shown-src", dependencyHash: "sha-shown-dep", sdkVersion: "sdk-shown");
            harness.Authoring.StageHandler = _ => staged;
            harness.Authoring.CommitHandler = request => new ExtensionCommitResult(
                new PluginStatusInfo(request.PluginId, "1.0.0", "Approved", request.SourceHash, request.DependencyHash, "sdk-shown", []),
                ["new_tool"]);

            var cardTask = ApprovalFixtures.NextExtensionCardAsync(harness.Queue);
            var pending = harness.Service.SubmitAsync(
                ApprovalFixtures.PluginDraft("plug"), Context(), CancellationToken.None);
            var card = await cardTask.WaitAsync(ApprovalFixtures.StepTimeout);

            card.ApproveCommand.Execute(null);
            var result = await WaitSubmitAsync(pending);

            Assert.Equal(ExtensionSubmitOutcome.Approved, result.Outcome);
            Assert.Equal(["new_tool"], result.ToolNames);
            var commit = Assert.Single(harness.Authoring.CommitCalls);
            Assert.Equal("sha-shown-src", commit.SourceHash);
            Assert.Equal("sha-shown-dep", commit.DependencyHash);
            Assert.Equal("sdk-shown", commit.SdkVersion);
            Assert.Same(staged.SourceHash, card.Staged.SourceHash);
            Assert.False(harness.Rules.IsAllowed("old_tool", ToolSideEffect.Execute, "s", null));
            Assert.False(harness.Rules.IsAllowed("new_tool", ToolSideEffect.Execute, "s", null));
            Assert.Contains("cleared", result.Message);
            Assert.Equal(0, await harness.Queue.CountAsync());
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Reject_discards_the_draft_and_changes_nothing()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            var cardTask = ApprovalFixtures.NextExtensionCardAsync(harness.Queue);
            var pending = harness.Service.SubmitAsync(
                ApprovalFixtures.PluginDraft("plug"), Context(), CancellationToken.None);
            var card = await cardTask.WaitAsync(ApprovalFixtures.StepTimeout);

            card.RejectCommand.Execute(null);
            var result = await WaitSubmitAsync(pending);

            Assert.Equal(ExtensionSubmitOutcome.Rejected, result.Outcome);
            Assert.Contains("nothing changed", result.Message);
            Assert.Equal(["plug"], harness.Authoring.DiscardCalls);
            Assert.Empty(harness.Authoring.CommitCalls);
            Assert.Equal(0, await harness.Queue.CountAsync());
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Reject_with_a_failing_discard_is_still_rejected_and_says_so()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            harness.Authoring.DiscardError = new InvalidOperationException("pipe broke");
            var cardTask = ApprovalFixtures.NextExtensionCardAsync(harness.Queue);
            var pending = harness.Service.SubmitAsync(
                ApprovalFixtures.PluginDraft("plug"), Context(), CancellationToken.None);
            var card = await cardTask.WaitAsync(ApprovalFixtures.StepTimeout);

            card.RejectCommand.Execute(null);
            var result = await WaitSubmitAsync(pending);

            Assert.Equal(ExtensionSubmitOutcome.Rejected, result.Outcome);
            Assert.Contains("still staged", result.Message);
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Cancel_throws_withdraws_the_card_and_never_discards()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            using var cts = new CancellationTokenSource();
            var cardTask = ApprovalFixtures.NextExtensionCardAsync(harness.Queue);
            var pending = harness.Service.SubmitAsync(
                ApprovalFixtures.PluginDraft("plug"), Context(), cts.Token);
            await cardTask.WaitAsync(ApprovalFixtures.StepTimeout);

            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
            Assert.Empty(harness.Authoring.DiscardCalls);
            Assert.Empty(harness.Authoring.CommitCalls);
            Assert.Equal(0, await harness.Queue.CountAsync());
            Assert.Equal(0, harness.Service.SubmissionsInFlight);
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Second_submission_for_the_same_plugin_is_rejected_while_one_waits()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            var cardTask = ApprovalFixtures.NextExtensionCardAsync(harness.Queue);
            var first = harness.Service.SubmitAsync(
                ApprovalFixtures.PluginDraft("plug"), Context(), CancellationToken.None);
            var card = await cardTask.WaitAsync(ApprovalFixtures.StepTimeout);

            var second = await WaitSubmitAsync(harness.Service.SubmitAsync(
                ApprovalFixtures.PluginDraft("plug", isUpdate: false), Context(), CancellationToken.None));
            Assert.Equal(ExtensionSubmitOutcome.Invalid, second.Outcome);
            Assert.Contains("already waiting", second.Message);

            card.RejectCommand.Execute(null);
            Assert.Equal(ExtensionSubmitOutcome.Rejected, (await WaitSubmitAsync(first)).Outcome);
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Commit_hash_mismatch_surfaced_from_the_host_is_invalid_text()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            harness.Authoring.CommitError = new IpcRemoteException("extensions.commit", "approval_mismatch", "hashes differ");
            var cardTask = ApprovalFixtures.NextExtensionCardAsync(harness.Queue);
            var pending = harness.Service.SubmitAsync(
                ApprovalFixtures.PluginDraft("plug"), Context(), CancellationToken.None);
            var card = await cardTask.WaitAsync(ApprovalFixtures.StepTimeout);

            card.ApproveCommand.Execute(null);
            var result = await WaitSubmitAsync(pending);

            Assert.Equal(ExtensionSubmitOutcome.Invalid, result.Outcome);
            Assert.Contains("no longer match", result.Message);
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public void Host_failures_map_to_model_facing_text()
    {
        Assert.Contains(
            "no longer match",
            ExtensionAuthoringService.Describe(new IpcRemoteException("extensions.commit", "approval_mismatch", "x")));
        Assert.Contains(
            "new version number",
            ExtensionAuthoringService.Describe(new IpcRemoteException("extensions.commit", "extension_blocked", "x")));
        Assert.Contains(
            "the manifest is broken",
            ExtensionAuthoringService.Describe(new IpcRemoteException("extensions.stage", "invalid_extension", "the manifest is broken")));
        Assert.Contains(
            "plain failure",
            ExtensionAuthoringService.Describe(new IpcRemoteException("extensions.stage", "something_else", "plain failure")));
        Assert.Contains(
            "did not answer",
            ExtensionAuthoringService.Describe(new IpcTimeoutException("extensions.stage", TimeSpan.FromSeconds(1))));
        Assert.Contains(
            "mismatch",
            ExtensionAuthoringService.Describe(new IpcVersionMismatchException(2, 1)));
        Assert.Equal(
            "boom",
            ExtensionAuthoringService.Describe(new InvalidOperationException("boom")));
    }

    [Fact]
    public async Task List_merges_statuses_with_pending_drafts()
    {
        var harness = Harness.Create(toolNames: id => id == "a" ? ["tool_a"] : []);
        try
        {
            await using var _ = harness;
            harness.Contributions.Statuses =
            [
                ApprovalFixtures.Installed("a", status: "Approved"),
                ApprovalFixtures.Installed("b", status: "Disabled"),
            ];
            harness.Authoring.Pending =
            [
                ApprovalFixtures.Staged("b", "2.0.0", liveVersion: "1.0.0"),
                ApprovalFixtures.Staged("c", "1.0.0"),
            ];

            var summaries = await harness.Service.ListAsync(CancellationToken.None);

            Assert.Equal(3, summaries.Count);
            var a = Assert.Single(summaries, static row => row.Id == "a");
            Assert.Equal(["tool_a"], a.ToolNames);
            Assert.False(a.HasPendingDraft);
            var b = Assert.Single(summaries, static row => row.Id == "b");
            Assert.True(b.HasPendingDraft);
            var c = Assert.Single(summaries, static row => row.Id == "c");
            Assert.Equal("Pending", c.Status);
            Assert.True(c.HasPendingDraft);
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task List_reports_a_host_failure_as_one_unavailable_row()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            harness.Contributions.StatusesError = new InvalidOperationException("pipe broke");

            var summaries = await harness.Service.ListAsync(CancellationToken.None);

            var row = Assert.Single(summaries);
            Assert.Equal("-", row.Id);
            Assert.Equal("Unavailable", row.Status);
            Assert.False(row.HasPendingDraft);
            Assert.NotNull(row.Error);
            Assert.Contains("pipe broke", row.Error);
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Restore_pending_requeues_as_a_previous_run_card_and_approves()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            var staged = ApprovalFixtures.Staged("plug");
            harness.Authoring.Pending = [staged];
            harness.Authoring.CommitHandler = request => new ExtensionCommitResult(
                new PluginStatusInfo(request.PluginId, "1.0.0", "Approved", request.SourceHash, request.DependencyHash, "sdk", []),
                ["tool_plug"]);

            var cardTask = ApprovalFixtures.NextExtensionCardAsync(harness.Queue);
            await harness.Service.RestorePendingAsync(CancellationToken.None);
            var card = await cardTask.WaitAsync(ApprovalFixtures.StepTimeout);

            Assert.True(card.FromPreviousRun);
            Assert.False(card.HasSession);

            card.ApproveCommand.Execute(null);
            await ApprovalFixtures.WaitUntilAsync(
                async () => await harness.Queue.CountAsync() == 0, "the restored card to be withdrawn");
            var commit = Assert.Single(harness.Authoring.CommitCalls);
            Assert.Equal("sha-source", commit.SourceHash);
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Restore_pending_rejects_by_discarding()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            harness.Authoring.Pending = [ApprovalFixtures.Staged("plug")];

            var cardTask = ApprovalFixtures.NextExtensionCardAsync(harness.Queue);
            await harness.Service.RestorePendingAsync(CancellationToken.None);
            var card = await cardTask.WaitAsync(ApprovalFixtures.StepTimeout);

            card.RejectCommand.Execute(null);
            await ApprovalFixtures.WaitUntilAsync(
                async () => await harness.Queue.CountAsync() == 0, "the restored card to be withdrawn");
            Assert.Equal(["plug"], harness.Authoring.DiscardCalls);
            Assert.Empty(harness.Authoring.CommitCalls);
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task New_submission_supersedes_a_restored_card_without_discarding_it()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            harness.Contributions.Statuses = [ApprovalFixtures.Installed("plug")];
            harness.Authoring.Pending = [ApprovalFixtures.Staged("plug", "1.0.0")];
            harness.Authoring.StageHandler = request => ApprovalFixtures.Staged(request.PluginId, "2.0.0", liveVersion: "1.0.0");

            var restoredTask = ApprovalFixtures.NextExtensionCardAsync(harness.Queue);
            await harness.Service.RestorePendingAsync(CancellationToken.None);
            var restored = await restoredTask.WaitAsync(ApprovalFixtures.StepTimeout);
            var restoredWait = restored.Decision;

            var freshTask = ApprovalFixtures.NextExtensionCardAsync(harness.Queue);
            var pending = harness.Service.SubmitAsync(
                ApprovalFixtures.PluginDraft("plug", isUpdate: true, version: "2.0.0"), Context(), CancellationToken.None);
            var fresh = await freshTask.WaitAsync(ApprovalFixtures.StepTimeout);

            var superseded = await restoredWait.WaitAsync(ApprovalFixtures.StepTimeout);
            Assert.True(superseded.IsSuperseded);
            Assert.Empty(harness.Authoring.DiscardCalls);
            Assert.Empty(harness.Authoring.CommitCalls);

            fresh.ApproveCommand.Execute(null);
            var result = await WaitSubmitAsync(pending);
            Assert.Equal(ExtensionSubmitOutcome.Approved, result.Outcome);
            var commit = Assert.Single(harness.Authoring.CommitCalls);
            Assert.Equal("2.0.0", fresh.Version);
            Assert.Equal("sha-source", commit.SourceHash);
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Disable_and_delete_report_model_facing_results()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            var disabled = await harness.Service.DisableAsync("plug", "1.0.0", CancellationToken.None);
            Assert.Contains("no longer registered", disabled);
            Assert.Equal([("plug", "1.0.0")], harness.Authoring.DisableCalls);

            var deleted = await harness.Service.DeleteAsync("plug", "1.0.0", CancellationToken.None);
            Assert.Contains("new version number", deleted);
            Assert.Equal([("plug", "1.0.0")], harness.Authoring.DeleteCalls);
        }
        finally
        {
            harness.DisposeDir();
        }
    }
}
