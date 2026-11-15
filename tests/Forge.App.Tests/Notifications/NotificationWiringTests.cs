using Avalonia.Headless;
using Forge.Abstractions;
using Forge.App.Approval;
using Forge.App.Composition;
using Forge.App.Git;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.App.Preferences;
using Forge.App.Services;
using Forge.App.Terminal;
using Forge.App.Tests.Approval;
using Forge.App.Tests.Git;
using Forge.App.Tests.Terminal;
using Forge.App.ViewModels;
using Forge.Mcp;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Notifications;

/// <summary>
/// The seams that turn a business event into one notification (App SPEC 200/209): each case pins which path notifies, at
/// which level and under which deduplication key, so a drifted key or a silently downgraded level fails here. Covered:
/// an approval card that starts waiting, a session that opens without a provider, a git operation that fails, a step
/// commit the platform refuses, and the fire-and-forget helper that never lets a notification failure reach the work
/// that wanted to notify. The session's turn and checkpoint failures are pinned beside their own harness
/// (<c>SessionTurnFailureTests</c>); the notification centre itself is covered by <see cref="NotificationCenterTests"/>.
/// <para>The environment-side half of the same list follows: an MCP server that never comes up, a terminal that exits
/// non-zero (and one that exits cleanly, which must stay silent), an editor language service that cannot apply, and a file
/// that refuses to save. Those two seams differ the way production wires them — a service the composition root owns takes
/// an <see cref="INotifications"/> and is read through <see cref="RecordingNotifications"/>, while a leaf view reads the
/// static <see cref="NotificationCenter.Current"/> and is read through a real center over a queued post, so the toast the
/// user would see is the thing asserted on.</para>
/// </summary>
[Collection("forge-app-ui")]
public sealed class NotificationWiringTests : IDisposable
{
    private const string SessionId = "wiring-session";

    private readonly string root = TestTempFiles.NewDirectory("forge-notify-wiring-");

    public void Dispose() => TestTempFiles.DeleteTree(root);

    [Fact]
    public async Task An_approval_card_that_starts_waiting_is_announced_once()
    {
        var dir = ApprovalFixtures.TempDir("forge-notify-approval-");
        try
        {
            await using var rules = new ApprovalRules(Path.Combine(dir, ApprovalRules.FileName), debounceMilliseconds: 0);
            var queue = new ApprovalQueue(InlineDispatcher.Instance);
            var notifications = new RecordingNotifications();
            using var handler = new SessionApprovalHandler(
                queue,
                rules,
                ApprovalFixtures.Session("s1", "p1"),
                notifications);

            var cardTask = ApprovalFixtures.NextToolCardAsync(queue);
            var pending = handler.RequestAsync(ApprovalFixtures.ToolRequest(), CancellationToken.None);
            var card = await cardTask.WaitAsync(ApprovalFixtures.StepTimeout);
            // The card is queued a step before the handler announces it, so the announcement is waited for rather than
            // assumed to have landed with the card.
            Assert.True(
                await TestPump.UntilAsync(() => notifications.Calls.Count > 0),
                "the waiting card was never announced");

            var call = Assert.Single(notifications.Calls);
            Assert.Equal(NotificationLevel.Warning, call.Level);
            Assert.Equal(NotificationsStrings.ApprovalWaitingTitleKey, call.TitleKey);
            Assert.Equal(NotificationsStrings.ApprovalWaitingBody, call.BodyKey);
            Assert.StartsWith("approval:", call.Key, StringComparison.Ordinal);

            // The card is still the thing that answers; the announcement does not decide anything.
            card.DenyCommand.Execute(null);
            Assert.Equal(ApprovalDecision.Deny, await pending.AsTask().WaitAsync(ApprovalFixtures.StepTimeout));
            Assert.Single(notifications.Calls);
        }
        finally
        {
            ApprovalFixtures.DeleteTree(dir);
        }
    }

    [Fact]
    public void A_session_that_opens_without_a_configured_provider_is_announced_as_an_error()
    {
        var notifications = new RecordingNotifications();
        using var workspace = new SessionWorkspaceViewModel(
            new SessionInfo(SessionId, root, "demo session", DateTimeOffset.UnixEpoch),
            runtime: null,
            checkpoints: null,
            store: null,
            TestProviderServices.Unconfigured(root),
            notifications: notifications);

        // The transcript line alone is missed as soon as another window has the focus, so opening a session that cannot
        // run anything is one of the paths that interrupts the user.
        var call = Assert.Single(notifications.WithKey("provider-missing:" + SessionId));
        Assert.Equal(NotificationLevel.Error, call.Level);
        Assert.Equal(NotificationsStrings.ProviderMissingTitleKey, call.TitleKey);
    }

    [Fact]
    public async Task A_failed_git_operation_is_announced_as_an_error_keyed_by_the_worktree()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("notify-fetch");
        await using var remote = new StubHttpRemote(hold: false);
        GitTestSandbox.PointOriginAt(work, remote.Url);

        var notifications = new RecordingNotifications();
        var session = SharedUi.Session;
        var panel = await session.Dispatch(() => Panel(notifications), CancellationToken.None);
        try
        {
            await ActAsync(session, panel, () => panel.Attach(new PanelContext(SessionId, work, null)));
            await ActAsync(session, panel, () => panel.FetchCommand.Execute(null));

            var call = Assert.Single(notifications.WithKey("git-op:" + work));
            Assert.Equal(NotificationLevel.Error, call.Level);
            Assert.Equal(NotificationsStrings.GitOperationFailedTitleKey, call.TitleKey);
            // The announcement never replaces the row's own record of the failure.
            Assert.True(await session.Dispatch(() => panel.MessageIsError, CancellationToken.None));
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_refused_step_commit_is_announced_as_an_error_keyed_by_the_step()
    {
        var notifications = new RecordingNotifications();
        await using var harness = await StepCommitHarness.CreateAsync(notifications: notifications);
        harness.Repo.InstallHook("pre-commit", "echo 'hook rejected: lint failed' >&2\nexit 1\n");
        harness.Repo.WriteFile("change.txt", "change\n");
        var item = await harness.AddDoneAsync("Add feature", "feat: add feature");
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        var failed = await harness.Committer.CommitStepAsync(item, bound.Token);

        Assert.Equal(StepCommitOutcome.Failed, failed.Outcome);
        var call = Assert.Single(notifications.WithKey("step-commit:" + item.Id));
        Assert.Equal(NotificationLevel.Error, call.Level);
        Assert.Equal(NotificationsStrings.StepCommitFailedTitleKey, call.TitleKey);
        // The body is the platform's own reason: the toast says why, the TODO row keeps it after the toast is gone.
        Assert.Null(call.BodyKey);
    }

    [Fact]
    public void The_fire_and_forget_helper_swallows_a_failing_notification()
    {
        var notifications = new RecordingNotifications { Failure = new InvalidOperationException("notify-boom") };

        notifications.Notify(
            NotificationLevel.Error,
            LocText.Literal("title"),
            LocText.Literal("body"));

        // Reaching this line is the assertion: a notification that throws must not surface in the caller.
        Assert.Empty(notifications.Calls);
    }

    [Fact]
    public void The_fire_and_forget_helper_accepts_a_missing_notification_entry() => ((INotifications?)null).Notify(
        NotificationLevel.Info,
        LocText.Literal("title"),
        LocText.Literal("body"));

    [Fact]
    public async Task An_mcp_server_that_never_comes_up_is_announced_as_a_warning_keyed_by_the_server()
    {
        TestTempFiles.WriteFile(root, "global/mcp.json", """{"mcpServers":{"srv":{"command":"never-spawned"}}}""");
        var notifications = new RecordingNotifications();
        await using var preferences = new PreferencesStore(Path.Combine(root, "settings.json"));
        await using var services = McpServices.Create(
            new McpConfigStore(Path.Combine(root, "global", "mcp.json"), Path.Combine(root, "workspace")),
            preferences,
            notifications,
            http: null,
            // The transport a server would spawn cannot be built at all: the manager records the failure per server.
            transportFactory: static _ => throw new InvalidOperationException("spawn failed"));

        await services.StartAsync();

        Assert.True(
            await TestPump.UntilAsync(() => notifications.WithKey("mcp:srv").Count > 0),
            "the server that failed to start was never announced");

        // The manager retries a server that never came up, so the same failure is announced more than once; the stable
        // per-server key is what collapses those repeats into one counted toast instead of a stack of them.
        var calls = notifications.WithKey("mcp:srv");
        Assert.All(
            calls,
            call =>
            {
                Assert.Equal(NotificationLevel.Warning, call.Level);
                Assert.Equal(NotificationsStrings.McpFailedTitleKey, call.TitleKey);
                // The body carries the server's own name and error, which no string table holds.
                Assert.Null(call.BodyKey);
            });
        Assert.Equal(calls.Count, notifications.Calls.Count);
    }

    [Fact]
    public async Task A_terminal_that_exits_non_zero_is_announced_as_a_warning_keyed_by_its_tab()
    {
        var post = new QueuedPost();
        using var center = new NotificationCenter(static () => true, new SilentNotifier(), post.Post);

        await SharedUi.RunAsync(
            async () =>
            {
                var host = new FakeTerminalHost();
                var scene = TerminalSceneUi.Open(host);
                try
                {
                    Assert.True(
                        await TestPump.UntilAsync(() => scene.ViewModel.Status == TerminalStatus.Running),
                        "the terminal never reached Running");

                    host.Session.Exit(3);

                    Assert.True(
                        await TestPump.UntilAsync(() => post.Count > 0),
                        "the non-zero exit was never announced");
                    post.Drain();

                    var toast = Assert.Single(center.Items);
                    Assert.Equal(NotificationLevel.Warning, toast.Level);
                    Assert.Equal("terminal:s1:1", toast.Key);
                    Assert.Equal(Loc.Instance[NotificationsStrings.TerminalExitedTitleKey], toast.Title);
                }
                finally
                {
                    scene.Window.Close();
                    await scene.ViewModel.DisposeAsync();
                }
            });
    }

    [Fact]
    public async Task A_clean_terminal_exit_is_not_announced()
    {
        var post = new QueuedPost();
        using var center = new NotificationCenter(static () => true, new SilentNotifier(), post.Post);

        await SharedUi.RunAsync(
            async () =>
            {
                var host = new FakeTerminalHost();
                var scene = TerminalSceneUi.Open(host);
                try
                {
                    Assert.True(
                        await TestPump.UntilAsync(() => scene.ViewModel.Status == TerminalStatus.Running),
                        "the terminal never reached Running");

                    host.Session.Exit(0);

                    Assert.True(
                        await TestPump.UntilAsync(() => scene.ViewModel.Status == TerminalStatus.Exited),
                        "the clean exit was never reported");
                    post.Drain();

                    // A clean exit is what the user asked for; interrupting for it would dilute the non-zero one above.
                    Assert.Empty(center.Items);
                }
                finally
                {
                    scene.Window.Close();
                    await scene.ViewModel.DisposeAsync();
                }
            });
    }

    [Fact]
    public async Task An_editor_language_service_failure_is_announced_as_a_warning_keyed_by_the_file()
    {
        var post = new QueuedPost();
        using var center = new NotificationCenter(static () => true, new SilentNotifier(), post.Post);
        var path = Path.Combine(root, "Program.cs");
        await File.WriteAllTextAsync(path, "class C { }\n");

        await SharedUi.RunAsync(
            async () =>
            {
                var model = await TestDocuments.LoadAsync(path);
                try
                {
                    model.ReportEditorLspNotice(LocText.Literal("the language service could not apply the edit"));
                    post.Drain();

                    var toast = Assert.Single(center.Items);
                    Assert.Equal(NotificationLevel.Warning, toast.Level);
                    Assert.Equal("editor-lsp:" + path, toast.Key);
                    Assert.Equal(Loc.Instance[NotificationsStrings.EditorFailedTitleKey], toast.Title);

                    // The status line alone is the record of an empty answer, so it must not share the toast's slot.
                    model.ReportEditorLspStatus(LocText.Literal("no references"));
                    post.Drain();
                    Assert.Single(center.Items);
                }
                finally
                {
                    await model.ReleaseDocumentAsync();
                }
            });
    }

    [Fact]
    public async Task A_file_that_refuses_to_save_is_announced_as_an_error_keyed_by_the_file()
    {
        var post = new QueuedPost();
        using var center = new NotificationCenter(static () => true, new SilentNotifier(), post.Post);
        var path = Path.Combine(root, "locked.txt");
        await File.WriteAllTextAsync(path, "keep\n");

        await SharedUi.RunAsync(
            async () =>
            {
                var model = await TestDocuments.LoadAsync(path);
                try
                {
                    model.IsEditing = true;
                    model.EditText = "changed\n";
                    File.SetAttributes(path, FileAttributes.ReadOnly);

                    ((System.Windows.Input.ICommand)model.SaveCommand).Execute(null);
                    Assert.True(
                        await TestPump.UntilAsync(() => model.HasSaveError),
                        "the read-only file never reported a save failure");
                    post.Drain();

                    var toast = Assert.Single(center.Items);
                    Assert.Equal(NotificationLevel.Error, toast.Level);
                    Assert.Equal("save-failed:" + path, toast.Key);
                    Assert.Equal(Loc.Instance[NotificationsStrings.SaveFailedTitleKey], toast.Title);
                    // The toast is the reach, not the record: the editor's own banner still carries the failure.
                    Assert.True(model.IsDirty);
                }
                finally
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    await model.ReleaseDocumentAsync();
                }
            });
    }

    /// <summary>A desktop notifier that shows nothing: these cases are about the in-window toast, never the platform.</summary>
    private sealed class SilentNotifier : ISystemNotifier
    {
        public Task NotifyAsync(string title, string body, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> TryNotifyAsync(string title, string body, CancellationToken ct) => Task.FromResult(false);
    }

    private static GitSyncViewModel Panel(INotifications notifications) =>
        new(
            new GitCli(),
            new GitPanelSettings(() => GitPullStrategy.FastForwardOnly, () => TimeSpan.FromSeconds(30)),
            openFile: null,
            notifications: notifications);

    /// <summary>Runs one panel verb and waits for the operation plus its trailing refresh on the UI thread.</summary>
    private static Task ActAsync(HeadlessUnitTestSession session, GitSyncViewModel panel, Action act) =>
        session.Dispatch(
            async () =>
            {
                act();
                await GitPanelTesting.WaitIdleAsync(panel);
                return 0;
            },
            CancellationToken.None);
}
