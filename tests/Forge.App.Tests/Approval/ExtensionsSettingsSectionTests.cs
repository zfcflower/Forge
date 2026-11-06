using Forge.Abstractions;
using Forge.App.Approval;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.Core.Tools;
using Forge.HostClient;
using Forge.Plugin.Sdk;
using ToolSideEffect = Forge.Abstractions.ToolSideEffect;
using Xunit;

namespace Forge.App.Tests.Approval;

public sealed class ExtensionsSettingsSectionTests
{
    private sealed class Harness : IAsyncDisposable
    {
        private Harness(string dir, ApprovalRules rules)
        {
            Dir = dir;
            Rules = rules;
            Host = new FakeHostState();
            Admin = new FakeAdmin();
            OpenedCards = [];
        }

        public string Dir { get; }

        public ApprovalRules Rules { get; }

        public FakeHostState Host { get; }

        public FakeAdmin Admin { get; }

        public List<string> OpenedCards { get; }

        public static Harness Create()
        {
            var dir = ApprovalFixtures.TempDir("forge-settings-section-");
            return new Harness(dir, new ApprovalRules(Path.Combine(dir, ApprovalRules.FileName), debounceMilliseconds: 0));
        }

        public ExtensionsSettingsSectionViewModel Section(IExtensionsAdmin? admin, Func<string, bool>? openResult = null) =>
            new(
                admin,
                Host,
                Rules,
                (id, _) =>
                {
                    OpenedCards.Add(id);
                    return Task.FromResult(openResult?.Invoke(id) ?? true);
                },
                InlineDispatcher.Instance);

        public async ValueTask DisposeAsync() => await Rules.DisposeAsync();

        public void DisposeDir() => ApprovalFixtures.DeleteTree(Dir);
    }

    private static ExtensionSnapshot SnapshotWith(
        IReadOnlyList<PluginStatusInfo> statuses,
        IReadOnlyList<StagedExtension>? pending = null,
        Func<PluginStatusInfo, IReadOnlyList<string>>? tools = null) =>
        new(
            statuses.Select(status => new ExtensionRow(status, tools?.Invoke(status) ?? [])).ToList(),
            pending ?? [],
            null);

    [Fact]
    public async Task Missing_host_reports_unavailable_and_never_shows_an_empty_list()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            harness.Host.HasHost = false;
            harness.Host.HostProblem = "missing binary";
            using var section = harness.Section(admin: null);
            await section.RefreshAsync(CancellationToken.None);

            Assert.True(section.HasUnavailableText);
            Assert.Contains("missing binary", section.UnavailableText);
            Assert.Contains(Loc.Instance[ExtensionsStrings.SettingsNoHost], section.UnavailableText);
            Assert.False(section.ShowExtensions);
            Assert.False(section.ShowInstalledEmpty);
            Assert.False(section.ShowPendingEmpty);

            // The remembered rules live in this process, so they stay visible without a host.
            harness.Rules.Allow(ApprovalRuleKey.Global("shell_run", ToolSideEffect.Execute));
            Assert.False(section.ShowRulesEmpty);
            Assert.Single(section.RuleGroups);
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Host_that_is_not_running_reports_its_detail()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            harness.Host.Status = HostStatus.Stopped;
            harness.Host.StatusDetail = "crashed on start";
            using var section = harness.Section(harness.Admin);
            await section.RefreshAsync(CancellationToken.None);

            Assert.True(section.HasUnavailableText);
            Assert.Contains("crashed on start", section.UnavailableText);
            Assert.False(section.ShowExtensions);
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Failed_read_reports_the_reason_instead_of_an_empty_answer()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            harness.Admin.ReadError = new InvalidOperationException("pipe broke");
            using var section = harness.Section(harness.Admin);
            await section.RefreshAsync(CancellationToken.None);

            Assert.True(section.HasUnavailableText);
            Assert.Contains("pipe broke", section.UnavailableText);
            Assert.False(section.ShowExtensions);
            Assert.Empty(section.Installed);
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Installed_rows_offer_enable_disable_by_status_and_act_through_the_admin()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            harness.Admin.Snapshot = SnapshotWith(
                [
                    ApprovalFixtures.Installed("running", status: "Approved"),
                    ApprovalFixtures.Installed("paused", status: "Disabled"),
                    ApprovalFixtures.Installed("gone", status: "Uninstalled"),
                ],
                tools: static status => status.PluginId == "running" ? ["tool_run"] : []);
            using var section = harness.Section(harness.Admin);
            await section.RefreshAsync(CancellationToken.None);

            Assert.True(section.ShowExtensions);
            Assert.Equal(3, section.Installed.Count);

            var running = section.Installed.Single(row => row.Id == "running");
            Assert.True(running.CanDisable);
            Assert.False(running.CanEnable);
            Assert.True(running.CanDelete);
            Assert.Contains("tool_run", running.ToolsText);

            var paused = section.Installed.Single(row => row.Id == "paused");
            Assert.True(paused.CanEnable);
            Assert.False(paused.CanDisable);

            var gone = section.Installed.Single(row => row.Id == "gone");
            Assert.False(gone.CanDelete);
            Assert.False(gone.ShowDelete);
            Assert.False(gone.ShowConfirmDelete);

            var reads = harness.Admin.ReadCount;
            running.DisableCommand.Execute(null);
            await ApprovalFixtures.WaitUntilAsync(() => harness.Admin.SetEnabledCalls.Count == 1, "the disable call");
            Assert.Equal(("running", "1.0.0", false), harness.Admin.SetEnabledCalls[0]);
            await ApprovalFixtures.WaitUntilAsync(() => harness.Admin.ReadCount > reads, "the refresh after disable");

            paused.EnableCommand.Execute(null);
            await ApprovalFixtures.WaitUntilAsync(() => harness.Admin.SetEnabledCalls.Count == 2, "the enable call");
            Assert.Equal(("paused", "1.0.0", true), harness.Admin.SetEnabledCalls[1]);
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Delete_needs_a_second_confirmation_and_cancel_backs_out()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            harness.Admin.Snapshot = SnapshotWith([ApprovalFixtures.Installed("plug")]);
            using var section = harness.Section(harness.Admin);
            await section.RefreshAsync(CancellationToken.None);

            var row = Assert.Single(section.Installed);
            Assert.True(row.ShowDelete);

            row.DeleteCommand.Execute(null);
            Assert.True(row.IsConfirmingDelete);
            Assert.True(row.ShowConfirmDelete);
            Assert.False(row.ShowDelete);

            row.CancelDeleteCommand.Execute(null);
            Assert.False(row.IsConfirmingDelete);
            Assert.True(row.ShowDelete);
            Assert.Empty(harness.Admin.DeleteCalls);

            row.DeleteCommand.Execute(null);
            row.ConfirmDeleteCommand.Execute(null);
            await ApprovalFixtures.WaitUntilAsync(() => harness.Admin.DeleteCalls.Count == 1, "the delete call");
            Assert.Equal(("plug", "1.0.0"), harness.Admin.DeleteCalls[0]);
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Pending_row_opens_the_card_and_a_vanished_draft_reports_instead()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            harness.Admin.Snapshot = SnapshotWith(
                [],
                [ApprovalFixtures.Staged("fresh"), ApprovalFixtures.Staged("broken", diagnostics: ["bad"])]);
            using var section = harness.Section(harness.Admin, openResult: static id => id != "broken");
            await section.RefreshAsync(CancellationToken.None);

            Assert.Equal(2, section.Pending.Count);
            var broken = section.Pending.Single(row => row.Id == "broken");
            Assert.True(broken.HasDiagnostics);
            Assert.Contains("bad", broken.DiagnosticsText);

            section.Pending.Single(row => row.Id == "fresh").OpenCommand.Execute(null);
            await ApprovalFixtures.WaitUntilAsync(() => harness.OpenedCards.Count == 1, "the open-card call");
            Assert.Equal(["fresh"], harness.OpenedCards);
            Assert.False(section.HasActionProblem);

            broken.OpenCommand.Execute(null);
            await ApprovalFixtures.WaitUntilAsync(() => section.HasActionProblem, "the vanished-draft problem");
            Assert.Contains(Loc.Instance[ExtensionsStrings.SettingsOpenCardFailed], section.ActionProblem);
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Rules_group_by_scope_and_each_row_revokes()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            using var section = harness.Section(harness.Admin);
            await section.RefreshAsync(CancellationToken.None);
            Assert.True(section.ShowRulesEmpty);

            harness.Rules.Allow(ApprovalRuleKey.ForSession("shell_run", ToolSideEffect.Execute, "s1"));
            harness.Rules.Allow(ApprovalRuleKey.ForProject("shell_run", ToolSideEffect.Execute, "p1"));
            harness.Rules.Allow(ApprovalRuleKey.Global("read_file", ToolSideEffect.ReadOnly));

            Assert.Equal(3, section.RuleGroups.Count);
            Assert.Equal(
                [ApprovalText.Scope(ApprovalScope.Session), ApprovalText.Scope(ApprovalScope.Project), ApprovalText.Scope(ApprovalScope.Global)],
                section.RuleGroups.Select(static group => group.Title));
            Assert.False(section.ShowRulesEmpty);

            var sessionGroup = section.RuleGroups[0];
            var row = Assert.Single(sessionGroup.Rules);
            Assert.Contains("shell_run", row.Text);
            row.RevokeCommand.Execute(null);

            Assert.Equal(2, section.RuleGroups.Count);
            // The session rule is gone, but the project rule still covers the same session in its project.
            Assert.False(harness.Rules.IsAllowed("shell_run", ToolSideEffect.Execute, "s1", "other"));
            Assert.True(harness.Rules.IsAllowed("shell_run", ToolSideEffect.Execute, "s1", "p1"));
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Corrupt_rules_file_surfaces_as_a_rules_problem()
    {
        var dir = ApprovalFixtures.TempDir("forge-settings-corrupt-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, ApprovalRules.FileName), "broken {{{");
            await using var rules = new ApprovalRules(Path.Combine(dir, ApprovalRules.FileName), debounceMilliseconds: 0);
            using var section = new ExtensionsSettingsSectionViewModel(
                new FakeAdmin(), new FakeHostState(), rules, (_, _) => Task.FromResult(true), InlineDispatcher.Instance);
            await section.RefreshAsync(CancellationToken.None);

            Assert.True(section.HasRulesProblem);
            Assert.NotNull(section.RulesProblemText);
        }
        finally
        {
            ApprovalFixtures.DeleteTree(dir);
        }
    }

    [Fact]
    public async Task Switching_language_refreshes_the_cached_titles()
    {
        var harness = Harness.Create();
        var previous = Loc.Instance.Language;
        try
        {
            await using var _ = harness;
            using var section = harness.Section(harness.Admin);
            await section.RefreshAsync(CancellationToken.None);

            Loc.Instance.Language = UiLanguage.English;
            Assert.Equal("Installed extensions", section.InstalledTitle);
            Assert.Equal("Drafts awaiting approval", section.PendingTitle);
            Assert.Equal("Remembered rules", section.RulesTitle);

            Loc.Instance.Language = UiLanguage.Chinese;
            Assert.Equal("已安装扩展", section.InstalledTitle);
            Assert.Equal("待审批草稿", section.PendingTitle);
            Assert.Equal("记住的规则", section.RulesTitle);
        }
        finally
        {
            Loc.Instance.Language = previous;
            harness.DisposeDir();
        }
    }
}
