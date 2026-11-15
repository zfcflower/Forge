using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Forge.Abstractions;
using Forge.App.Approval;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Services;
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

        public ExtensionsSettingsSectionViewModel Section(
            IExtensionsAdmin? admin,
            Func<string, bool>? openResult = null,
            AppPreferences? preferences = null,
            HttpClient? repositoryHttp = null) =>
            new(
                admin,
                Host,
                Rules,
                (id, _) =>
                {
                    OpenedCards.Add(id);
                    return Task.FromResult(openResult?.Invoke(id) ?? true);
                },
                InlineDispatcher.Instance,
                preferences,
                repositoryHttp);

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
    public async Task Repository_url_round_trips_through_the_injected_preferences()
    {
        var harness = Harness.Create();
        var settingsDirectory = ApprovalFixtures.TempDir("forge-extensions-prefs-");
        var settingsPath = Path.Combine(settingsDirectory, "settings.json");
        try
        {
            await using var _ = harness;
            await using var store = new PreferencesStore(settingsPath);
            await store.LoadAsync(CancellationToken.None);

            using (var section = harness.Section(harness.Admin, preferences: store.Current))
            {
                Assert.Equal(string.Empty, section.RepositoryUrl);
                Assert.False(section.HasRepositoryUrl);

                // A pasted URL is stored trimmed, and the section writes it through as the user types.
                section.RepositoryUrl = "  https://example.test/repo.json  ";
                Assert.Equal("https://example.test/repo.json", store.Current.ExtensionRepositoryUrl);
                Assert.True(section.HasRepositoryUrl);
            }

            await store.SaveAsync(CancellationToken.None);
            var json = await File.ReadAllTextAsync(settingsPath);
            Assert.Contains("\"extensionRepositoryUrl\"", json, StringComparison.Ordinal);

            // A new process reads the same file and a new section starts on the remembered index.
            await using var reloaded = new PreferencesStore(settingsPath);
            await reloaded.LoadAsync(CancellationToken.None);
            Assert.Null(reloaded.LoadProblem);
            using var reopened = harness.Section(harness.Admin, preferences: reloaded.Current);
            Assert.Equal("https://example.test/repo.json", reopened.RepositoryUrl);
            Assert.True(reopened.HasRepositoryUrl);
        }
        finally
        {
            harness.DisposeDir();
            ApprovalFixtures.DeleteTree(settingsDirectory);
        }
    }

    [Fact]
    public async Task A_version_that_cannot_be_ordered_never_offers_an_update()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            harness.Admin.Snapshot = SnapshotWith([ApprovalFixtures.Installed("plug", version: "1.0.0")]);
            using var section = harness.Section(
                harness.Admin,
                repositoryHttp: RepositoryHttp($$"""{ "schema": 1, "packages": [{{PackageEntry("plug", "garbage")}}] }""", PackageZipBytes()));
            await section.RefreshAsync(CancellationToken.None);
            section.RepositoryUrl = "https://example.test/repo.json";

            section.BrowseRepositoryCommand.Execute(null);
            await ApprovalFixtures.WaitUntilAsync(() => section.Repository.Count == 1, "the browsed row");

            var row = section.Repository[0];
            Assert.Equal(VersionComparison.Incomparable, row.Comparison);
            Assert.False(row.IsUpdate);
            Assert.Equal(Loc.Instance[ExtensionsStrings.SettingsRepositoryInstall], row.InstallText);
            // The installed version is still named, so the user can see why nothing was offered as an update.
            Assert.Contains("1.0.0", row.InstalledText, StringComparison.Ordinal);
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Browsing_while_an_install_runs_keeps_the_install_and_replaces_the_rows()
    {
        var (root, previous) = TempDataRoot.Redirect("forge-extensions-repository-");
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            string? installedFrom = null;
            var manifestPresent = false;
            harness.Admin.InstallHook = (sourceDir, origin, _) =>
            {
                installedFrom = sourceDir;
                manifestPresent = File.Exists(Path.Combine(sourceDir, "manifest.json"));
                Assert.Equal(ExtensionOrigins.Installed, origin);
                return gate.Task;
            };
            using var section = harness.Section(
                harness.Admin,
                repositoryHttp: RepositoryHttp(IndexJson("plug", "2.0.0"), PackageZipBytes()));
            await section.RefreshAsync(CancellationToken.None);
            section.RepositoryUrl = "https://example.test/repo.json";

            section.BrowseRepositoryCommand.Execute(null);
            await ApprovalFixtures.WaitUntilAsync(() => section.Repository.Count == 1, "the browsed row");
            var installing = section.Repository[0];
            Assert.True(installing.CanInstall);

            installing.InstallCommand.Execute(null);
            await ApprovalFixtures.WaitUntilAsync(() => harness.Admin.InstallCalls.Count == 1, "the install call");
            Assert.True(installing.IsInstalling);
            Assert.NotNull(installedFrom);
            // The host only accepts a package directory under the agreed install temp root, holding the manifest.
            Assert.StartsWith(
                Path.GetFullPath(ExtensionRepositoryService.DefaultInstallTempRoot()),
                installedFrom,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(manifestPresent);

            // A second fetch while the first install waits on the host: the list is rebuilt, the running install is not
            // cancelled and its row is not the one the new list shows.
            section.BrowseRepositoryCommand.Execute(null);
            await ApprovalFixtures.WaitUntilAsync(
                () => section.Repository.Count == 1 && !ReferenceEquals(section.Repository[0], installing),
                "the second browse");

            var refreshed = section.Repository[0];
            Assert.False(refreshed.IsInstalling);
            Assert.Equal(string.Empty, refreshed.ProgressText);
            Assert.True(installing.IsInstalling);
            Assert.Single(harness.Admin.InstallCalls);

            gate.SetResult();
            await ApprovalFixtures.WaitUntilAsync(() => !installing.IsInstalling, "the install to finish");

            Assert.Single(harness.Admin.InstallCalls);
            Assert.False(section.HasRepositoryProblem);
            Assert.False(section.HasActionProblem);
            Assert.Single(section.Repository);
            Assert.True(harness.Admin.ReadCount > 1, "the section re-read the registry after the install");
        }
        finally
        {
            harness.DisposeDir();
            previous.Restore();
            ApprovalFixtures.DeleteTree(root);
        }
    }

    [Fact]
    public async Task A_second_click_on_an_installing_row_starts_one_install()
    {
        var (root, previous) = TempDataRoot.Redirect("forge-extensions-double-click-");
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.Admin.InstallHook = (_, _, _) => gate.Task;
            using var section = harness.Section(
                harness.Admin,
                repositoryHttp: RepositoryHttp(IndexJson("plug", "1.0.0"), PackageZipBytes()));
            await section.RefreshAsync(CancellationToken.None);
            section.RepositoryUrl = "https://example.test/repo.json";
            section.BrowseRepositoryCommand.Execute(null);
            await ApprovalFixtures.WaitUntilAsync(() => section.Repository.Count == 1, "the browsed row");

            var row = section.Repository[0];
            row.InstallCommand.Execute(null);
            await ApprovalFixtures.WaitUntilAsync(() => harness.Admin.InstallCalls.Count == 1, "the first install call");

            Assert.False(row.InstallCommand.CanExecute(null));
            row.InstallCommand.Execute(null);
            gate.SetResult();
            await ApprovalFixtures.WaitUntilAsync(() => !row.IsInstalling, "the install to finish");

            // One row, one package directory, one host install - a double click must not install the same id twice.
            Assert.Single(harness.Admin.InstallCalls);
        }
        finally
        {
            harness.DisposeDir();
            previous.Restore();
            ApprovalFixtures.DeleteTree(root);
        }
    }

    [Fact]
    public async Task Cancelling_a_row_cancels_its_install_token_and_leaves_no_problem()
    {
        var (root, previous) = TempDataRoot.Redirect("forge-extensions-cancel-");
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // The host call is where a cancel has to be visible: the token travels from the row's cancel command all
            // the way into the host install, so cancelling stops the download, the extraction and the install itself.
            harness.Admin.InstallHook = async (_, _, ct) =>
            {
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult();
                    throw;
                }
            };
            using var section = harness.Section(
                harness.Admin,
                repositoryHttp: RepositoryHttp(IndexJson("plug", "1.0.0"), PackageZipBytes()));
            await section.RefreshAsync(CancellationToken.None);
            section.RepositoryUrl = "https://example.test/repo.json";
            section.BrowseRepositoryCommand.Execute(null);
            await ApprovalFixtures.WaitUntilAsync(() => section.Repository.Count == 1, "the browsed row");

            var row = section.Repository[0];
            Assert.False(row.CancelCommand.CanExecute(null));
            row.InstallCommand.Execute(null);
            await entered.Task.WaitAsync(ApprovalFixtures.StepTimeout);
            Assert.True(row.CancelCommand.CanExecute(null));

            row.CancelCommand.Execute(null);

            await ApprovalFixtures.WaitUntilAsync(() => !row.IsInstalling, "the cancelled install to end");
            Assert.True(cancelled.Task.IsCompleted, "the install token handed to the host was cancelled");
            // A cancel is the user's own decision, not a failure to report back as a problem.
            Assert.False(section.HasActionProblem);
            Assert.Single(harness.Admin.InstallCalls);
        }
        finally
        {
            harness.DisposeDir();
            previous.Restore();
            ApprovalFixtures.DeleteTree(root);
        }
    }

    [Fact]
    public async Task Disposing_the_section_cancels_an_install_that_is_still_running()
    {
        var (root, previous) = TempDataRoot.Redirect("forge-extensions-dispose-cancel-");
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.Admin.InstallHook = async (_, _, ct) =>
            {
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult();
                    throw;
                }
            };
            var section = harness.Section(
                harness.Admin,
                repositoryHttp: RepositoryHttp(IndexJson("plug", "1.0.0"), PackageZipBytes()));
            await section.RefreshAsync(CancellationToken.None);
            section.RepositoryUrl = "https://example.test/repo.json";
            section.BrowseRepositoryCommand.Execute(null);
            await ApprovalFixtures.WaitUntilAsync(() => section.Repository.Count == 1, "the browsed row");

            var row = section.Repository[0];
            row.InstallCommand.Execute(null);
            await entered.Task.WaitAsync(ApprovalFixtures.StepTimeout);

            // Closing the shell must not leave an install running into a graph that is being torn down.
            section.Dispose();

            await cancelled.Task.WaitAsync(ApprovalFixtures.StepTimeout);
            Assert.Single(harness.Admin.InstallCalls);
        }
        finally
        {
            harness.DisposeDir();
            previous.Restore();
            ApprovalFixtures.DeleteTree(root);
        }
    }

    [Fact]
    public async Task Building_the_section_clears_the_leftover_install_root()
    {
        var (root, previous) = TempDataRoot.Redirect("forge-extensions-stale-install-");
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            var stale = Path.Combine(ExtensionRepositoryService.DefaultInstallTempRoot(), "package-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stale);
            await File.WriteAllTextAsync(Path.Combine(stale, "manifest.json"), "{}");

            using var section = harness.Section(harness.Admin);
            await section.RefreshAsync(CancellationToken.None);

            // A run that was killed mid-install leaves extractions the host will never accept; the root is transient by
            // contract and is emptied once when the section is built (startup), not left to accumulate.
            Assert.False(Directory.Exists(stale));
            Assert.False(Directory.Exists(ExtensionRepositoryService.DefaultInstallTempRoot()));
        }
        finally
        {
            harness.DisposeDir();
            previous.Restore();
            ApprovalFixtures.DeleteTree(root);
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

    // ---------------------------------------------------------------- repository fixtures

    private static string PackageEntry(string id, string version)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(PackageZipBytes()));
        return $$"""{ "id": "{{id}}", "version": "{{version}}", "kind": "plugin", "url": "https://example.test/{{id}}.zip", "sha256": "{{sha}}" }""";
    }

    private static string IndexJson(string id, string version) =>
        $$"""{ "schema": 1, "name": "test", "packages": [{{PackageEntry(id, version)}}] }""";

    /// <summary>The package itself: a zip whose only entry is the manifest the host validates.</summary>
    private static byte[] PackageZipBytes()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("manifest.json");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("""{ "id": "plug", "version": "2.0.0", "kind": "plugin", "source": "class P {}" }""");
        }

        return buffer.ToArray();
    }

    private static HttpClient RepositoryHttp(string indexJson, byte[] package)
    {
        var handler = new RepositoryHandler(Encoding.UTF8.GetBytes(indexJson), package);
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Serves the index and the package of a two-URL repository, so no test ever opens a socket.</summary>
    private sealed class RepositoryHandler(byte[] indexJson, byte[] package) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var body = path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? package : indexJson;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }
}
