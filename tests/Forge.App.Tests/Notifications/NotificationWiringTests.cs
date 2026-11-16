using System.Text;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using AvaloniaEdit.Document;
using Forge.Abstractions;
using Forge.App.Approval;
using Forge.App.Changes;
using Forge.App.Composition;
using Forge.App.Git;
using Forge.App.Lifecycle;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.App.Preferences;
using Forge.App.Providers;
using Forge.App.Review;
using Forge.App.Services;
using Forge.App.Terminal;
using Forge.App.Tests.Approval;
using Forge.App.Tests.Changes;
using Forge.App.Tests.Git;
using Forge.App.Tests.Terminal;
using Forge.App.Tests.ViewModels;
using Forge.App.ViewModels;
using Forge.Core.Prompts.Templates;
using Forge.HostClient;
using Forge.Mcp;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Notifications;

/// <summary>
/// The seams that turn a business event into one notification (App SPEC 200/209): each case pins which path notifies, at
/// which level and under which deduplication key, so a drifted key or a silently downgraded level fails here. Covered:
/// an approval card that starts waiting, a session that opens without a provider, a git operation that fails, a step
/// commit the platform refuses, and the fire-and-forget helper that never lets a notification failure reach the work
/// that wanted to notify. The composer's attachment refusals follow: a kind the model or the provider refuses, an image
/// of a format, size or count it does not take, and a file that cannot be read - each stays inline and is announced too.
/// The session's turn and checkpoint failures are pinned beside their own harness
/// (<c>SessionTurnFailureTests</c>); the notification centre itself is covered by <see cref="NotificationCenterTests"/>.
/// <para>The environment-side half of the same list follows: an MCP server that never comes up, a terminal that exits
/// non-zero (and one that exits cleanly, which must stay silent), an editor language service that cannot apply, a file
/// that refuses to save, an extension host that never comes up, a model list that cannot be fetched, the two review
/// surfaces whose store cannot be written, and the four things the shell itself reports while it starts (a preferences
/// file that fell back to defaults, a database that cannot be opened, a project list that cannot be read, a purge
/// cleanup whose retry failed again). Those seams differ the way production wires them — a service the composition root
/// owns takes an <see cref="INotifications"/> and is read through <see cref="RecordingNotifications"/>, while a leaf view
/// reads the static <see cref="NotificationCenter.Current"/> and is read through a real center over a queued post, so the
/// toast the user would see is the thing asserted on. The shell's own cases read the center its composition root built,
/// over the real dispatcher rather than a queued post.</para>
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

    [Fact]
    public async Task An_extension_host_that_never_comes_up_is_announced_as_an_error_keyed_by_the_host()
    {
        var notifications = new RecordingNotifications();
        // A connection without a host executable only waits for a pipe nobody listens on: the client fails on its own and
        // spawns nothing, which is the one start failure the supervisor announces instead of retrying silently.
        var connection = new HostConnection
        {
            PipeName = "forge-test-" + Guid.NewGuid().ToString("N")[..20],
            StartTimeout = TimeSpan.FromMilliseconds(300),
        };
        await using var client = new ExtensionHostClient(connection);
        await using var supervisor = ExtensionHostSupervisor.WrapClient(client, notifications);

        supervisor.Start();

        Assert.True(
            await TestPump.UntilAsync(() => notifications.WithKey("host:failed").Count > 0),
            "the host that never came up was not announced");

        var call = notifications.WithKey("host:failed")[0];
        Assert.Equal(NotificationLevel.Error, call.Level);
        Assert.Equal(NotificationsStrings.HostUnavailableTitleKey, call.TitleKey);
        // The body is the client's own sentence about why the pipe never opened, which no string table holds.
        Assert.Null(call.BodyKey);
    }

    [Fact]
    public async Task A_plugin_problem_from_the_host_is_announced_as_a_warning_keyed_by_the_plugin()
    {
        var notifications = new RecordingNotifications();
        var connection = new HostConnection
        {
            PipeName = "forge-test-" + Guid.NewGuid().ToString("N")[..20],
            StartTimeout = TimeSpan.FromMilliseconds(300),
        };
        await using var client = new ExtensionHostClient(connection);
        await using var supervisor = ExtensionHostSupervisor.WrapClient(client, notifications);

        // A code this build knows becomes a localized sentence; the plugin id and the number ride the format arguments.
        supervisor.OnPluginProblem(new HostPluginProblem("alpha", "workflow-terminated", "1 run terminated", 1));

        Assert.True(
            await TestPump.UntilAsync(() => notifications.WithKey("plugin:problem:alpha").Count > 0),
            "the terminated workflow run was not announced");
        var call = notifications.WithKey("plugin:problem:alpha")[0];
        Assert.Equal(NotificationLevel.Warning, call.Level);
        Assert.Equal(NotificationsStrings.PluginProblemTitleKey, call.TitleKey);
        Assert.Equal(NotificationsStrings.PluginWorkflowTerminatedBody, call.BodyKey);
    }

    [Fact]
    public async Task A_plugin_problem_with_an_unknown_code_keeps_the_hosts_own_sentence()
    {
        var notifications = new RecordingNotifications();
        var connection = new HostConnection
        {
            PipeName = "forge-test-" + Guid.NewGuid().ToString("N")[..20],
            StartTimeout = TimeSpan.FromMilliseconds(300),
        };
        await using var client = new ExtensionHostClient(connection);
        await using var supervisor = ExtensionHostSupervisor.WrapClient(client, notifications);

        // A host newer than this build still says something instead of nothing: no string table should lose the message.
        supervisor.OnPluginProblem(new HostPluginProblem("alpha", "some-future-code", "the host said so", 0));

        Assert.True(
            await TestPump.UntilAsync(() => notifications.WithKey("plugin:problem:alpha").Count > 0),
            "the unknown plugin problem was not announced");
        var call = notifications.WithKey("plugin:problem:alpha")[0];
        Assert.Equal(NotificationLevel.Warning, call.Level);
        Assert.Equal(NotificationsStrings.PluginProblemTitleKey, call.TitleKey);
        Assert.Null(call.BodyKey);
    }

    [Fact]
    public async Task A_model_list_that_cannot_be_fetched_is_announced_as_a_warning_keyed_by_the_fetch()
    {
        var post = new QueuedPost();
        using var center = new NotificationCenter(static () => true, new SilentNotifier(), post.Post);
        var settings = TestTempFiles.WriteFile(
            root,
            "providers.json",
            """
            {"providers":{"version":1,"defaultId":"alpha","items":[
              {"id":"alpha","preset":"custom-openai","name":"Alpha","protocol":"openai-compatible",
               "baseUrl":"https://alpha.invalid/v1","auth":{"kind":"api-key"},
               "models":["alpha-1"],"defaultModel":"alpha-1"}]}}
            """);
        var data = Path.Combine(root, "provider-data");
        Directory.CreateDirectory(data);
        using var http = new HttpClient(new OfflineHandler());
        var providers = new ProviderServices(settings, data, http);

        await SharedUi.RunAsync(
            async () =>
            {
                using var composer = new ComposerViewModel(
                    providers,
                    _ => { },
                    new ComposerPreferences(),
                    root,
                    skillSource: static () => []);

                await composer.RefreshAsync(CancellationToken.None);
                post.Drain();

                var toast = Assert.Single(center.Items);
                Assert.Equal(NotificationLevel.Warning, toast.Level);
                Assert.Equal("model-fetch", toast.Key);
                // The picker's refresh has no headline of its own: it reuses the provider one.
                Assert.Equal(Loc.Instance[NotificationsStrings.ProviderMissingTitleKey], toast.Title);
                // The announcement does not empty the picker: the models already known stay selectable.
                Assert.Contains("alpha-1", composer.Models.Select(static model => model.Name));
            });
    }

    [Fact]
    public async Task An_image_of_an_unreadable_format_is_reported_inline_and_announced_as_a_warning()
    {
        await WithComposerAsync(
            async (composer, notifications) =>
            {
                // Two drops of the same kind: the reason stays under the input, and both land on one key so the toast counts
                // up instead of stacking.
                Assert.Equal(AttachmentRejection.Unsupported, await composer.AddImageAsync("%PDF-1.7"u8.ToArray(), "doc.png"));
                Assert.Equal(AttachmentRejection.Unsupported, await composer.AddImageAsync([0x50, 0x4B, 3, 4], "sheet.png"));

                Assert.Empty(composer.Attachments);
                Assert.Contains("sheet.png", composer.AttachmentError, StringComparison.Ordinal);
                var calls = notifications.WithKey("attachment-refused:scope:Unsupported");
                Assert.Equal(2, calls.Count);
                Assert.All(calls, call =>
                {
                    Assert.Equal(NotificationLevel.Warning, call.Level);
                    Assert.Equal(AttachmentStrings.RefusedTitleKey, call.TitleKey);
                    Assert.Equal(ImageStrings.UnsupportedKey, call.BodyKey);
                });
            },
            codec: new StubCodec());
    }

    [Fact]
    public async Task An_image_over_the_encoded_limit_is_reported_inline_and_announced_as_a_warning()
    {
        await WithComposerAsync(
            async (composer, notifications) =>
            {
                Assert.Equal(
                    AttachmentRejection.TooLarge,
                    await composer.AddImageAsync([0xFF, 0xD8, 0xFF, 0xE0, 0, 16], "huge.jpg"));

                Assert.Empty(composer.Attachments);
                Assert.Contains("huge.jpg", composer.AttachmentError, StringComparison.Ordinal);
                var call = Assert.Single(notifications.Calls);
                Assert.Equal(NotificationLevel.Warning, call.Level);
                Assert.Equal(AttachmentStrings.RefusedTitleKey, call.TitleKey);
                Assert.Equal(ImageStrings.TooLargeKey, call.BodyKey);
                Assert.Equal("attachment-refused:scope:TooLarge", call.Key);
            },
            codec: new StubCodec(ImageAttachmentRules.MaxEncodedBytes + 1));
    }

    [Fact]
    public async Task The_eleventh_image_is_reported_inline_and_announced_as_a_warning()
    {
        await WithComposerAsync(
            async (composer, notifications) =>
            {
                for (var i = 0; i < ImageAttachmentRules.MaxImagesPerMessage; i++)
                {
                    Assert.Equal(AttachmentRejection.None, await composer.AddImageAsync(PngBytes, $"{i}.png"));
                }

                Assert.Equal(AttachmentRejection.TooMany, await composer.AddImageAsync(PngBytes, "extra.png"));

                Assert.Equal(ImageAttachmentRules.MaxImagesPerMessage, composer.Attachments.Count);
                Assert.Equal(ImageStrings.TooMany.Resolve(), composer.AttachmentError);
                var call = Assert.Single(notifications.Calls);
                Assert.Equal(NotificationLevel.Warning, call.Level);
                Assert.Equal(AttachmentStrings.RefusedTitleKey, call.TitleKey);
                Assert.Equal(ImageStrings.TooManyKey, call.BodyKey);
                Assert.Equal("attachment-refused:scope:TooMany", call.Key);
            },
            codec: new StubCodec());
    }

    [Fact]
    public async Task An_image_the_provider_wire_cannot_carry_is_reported_inline_and_announced_as_a_warning()
    {
        await WithComposerAsync(
            async (composer, notifications) =>
            {
                Assert.Equal(AttachmentRejection.WireUnsupported, await composer.AddImageAsync(PngBytes, "shot.png"));

                Assert.Empty(composer.Attachments);
                Assert.Contains("shot.png", composer.AttachmentError, StringComparison.Ordinal);
                var call = Assert.Single(notifications.Calls);
                Assert.Equal(NotificationLevel.Warning, call.Level);
                Assert.Equal(AttachmentStrings.RefusedTitleKey, call.TitleKey);
                Assert.Equal(AttachmentStrings.WireUnsupportedKey, call.BodyKey);
                Assert.Equal("attachment-refused:scope:WireUnsupported", call.Key);
            },
            wire: static (_, _) => false,
            codec: new StubCodec());
    }

    [Fact]
    public async Task An_image_file_that_cannot_be_read_is_reported_inline_and_announced_as_a_warning()
    {
        await WithComposerAsync(
            async (composer, notifications) =>
            {
                Assert.Equal(AttachmentRejection.Unsupported, await composer.AddImageFileAsync(Path.Combine(root, "gone.png")));

                Assert.Empty(composer.Attachments);
                Assert.Contains("gone.png", composer.AttachmentError, StringComparison.Ordinal);
                var call = Assert.Single(notifications.Calls);
                Assert.Equal(NotificationLevel.Warning, call.Level);
                Assert.Equal(AttachmentStrings.RefusedTitleKey, call.TitleKey);
                // The body is the reader's own sentence about the file, not the generic "not supported" one.
                Assert.Equal(ImageStrings.ReadFailedKey, call.BodyKey);
                Assert.Equal("attachment-refused:scope:Unsupported", call.Key);
            },
            codec: new StubCodec());
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_is_reported_inline_and_announced_as_a_warning()
    {
        await WithComposerAsync(
            async (composer, notifications) =>
            {
                Assert.Equal(AttachmentRejection.Unsupported, await composer.AddFileAsync(Path.Combine(root, "gone.md")));

                Assert.Empty(composer.Files);
                Assert.Contains("gone.md", composer.AttachmentError, StringComparison.Ordinal);
                var call = Assert.Single(notifications.Calls);
                Assert.Equal(NotificationLevel.Warning, call.Level);
                Assert.Equal(AttachmentStrings.RefusedTitleKey, call.TitleKey);
                Assert.Equal(ImageStrings.ReadFailedKey, call.BodyKey);
                Assert.Equal("attachment-refused:scope:Unsupported", call.Key);
            });
    }

    [Fact]
    public async Task A_review_store_the_file_surface_cannot_write_is_announced_as_an_error_keyed_by_the_surface()
    {
        var post = new QueuedPost();
        using var center = new NotificationCenter(static () => true, new SilentNotifier(), post.Post);
        var path = Path.Combine(root, "a.cs");
        await File.WriteAllTextAsync(path, "one\ntwo\nthree\n");
        // The store's directory would sit under a file, so it can never be created: the store fails the way a full or
        // locked disk does, and the surface announces it instead of only muttering into its own status line.
        var store = new ReviewStore(Path.Combine(path, ".review-data"));

        await SharedUi.RunAsync(
            async () =>
            {
                var model = await TestDocuments.LoadAsync(path);
                try
                {
                    model.ConfigureReview(
                        new ReviewServices(store, static (_, _, _) => true, static () => { }),
                        new ReviewOwner(root, "A"));

                    Assert.True(
                        await TestPump.UntilAsync(() => post.Count > 0),
                        "the review store failure was never announced");
                    post.Drain();

                    var toast = Assert.Single(center.Items);
                    Assert.Equal(NotificationLevel.Error, toast.Level);
                    Assert.Equal("review-file", toast.Key);
                    Assert.Equal(Loc.Instance[NotificationsStrings.EditorFailedTitleKey], toast.Title);
                    // The toast is the reach, not the record: the panel's own status line keeps the failure.
                    Assert.Equal(ReviewStrings.StoreFailed, model.ReviewController.Status.Key);
                }
                finally
                {
                    model.DisposeReview();
                    await model.ReleaseDocumentAsync();
                }
            });
    }

    [Fact]
    public async Task A_review_store_the_diff_surface_cannot_write_is_announced_as_an_error_keyed_by_the_surface()
    {
        var post = new QueuedPost();
        using var center = new NotificationCenter(static () => true, new SilentNotifier(), post.Post);
        var blocked = Path.Combine(root, "diff-host.txt");
        await File.WriteAllTextAsync(blocked, "a file where the store's directory would be\n");
        var store = new ReviewStore(Path.Combine(blocked, ".review-data"));
        var files = DiffFixtures.Files(1);

        await SharedUi.RunAsync(
            async () =>
            {
                var controller = new DiffReviewController();
                try
                {
                    var built = DiffDocumentBuilder.Build(
                        files.Select(static file => new DiffFileSpec(file.Diff, file.IsCollapsed, false, true, null)).ToList(),
                        split: false);
                    var document = built.Secondary ?? built.Primary;
                    // The diff surface only reads the store once it has both a document and an owner, so the reload that
                    // fails is the one the owner brings.
                    controller.Rebind(new TextDocument(document.Text), document.Map, files);
                    controller.Configure(
                        new DiffReviewContext(
                            new ReviewServices(store, static (_, _, _) => true, static () => { }),
                            new ReviewOwner(root, "A"),
                            "base123"));

                    Assert.True(
                        await TestPump.UntilAsync(() => post.Count > 0),
                        "the diff surface's store failure was never announced");
                    post.Drain();

                    var toast = Assert.Single(center.Items);
                    Assert.Equal(NotificationLevel.Error, toast.Level);
                    // The two surfaces of one store keep their own key: a failure in the diff is not a failure in a file.
                    Assert.Equal("review-diff", toast.Key);
                    Assert.Equal(Loc.Instance[NotificationsStrings.EditorFailedTitleKey], toast.Title);
                    Assert.Equal(ReviewStrings.StoreFailed, controller.Status.Key);
                }
                finally
                {
                    controller.Dispose();
                }
            });
    }

    [Fact]
    public Task A_preferences_file_that_fell_back_to_defaults_is_announced_as_a_warning_keyed_by_the_file() =>
        WithShellAsync(
            "forge-notify-preferences-",
            dataRoot => File.WriteAllTextAsync(
                Path.Combine(dataRoot, "data", "settings.json"),
                // Valid JSON with a preferences section that is not an object: the reader falls back to defaults and says why.
                """{"preferences":42}"""),
            async (shell, composition) =>
            {
                await shell.InitializeAsync();

                Assert.True(
                    await TestPump.UntilAsync(() => composition.Notifications.Items.Count > 0),
                    "the preferences file that fell back to defaults was not announced");

                var toast = Toast(composition.Notifications, "preferences-problem");
                Assert.Equal(NotificationLevel.Warning, toast.Level);
                Assert.Equal(Loc.Instance[NotificationsStrings.PreferencesProblemTitleKey], toast.Title);
                // The body is the reader's own sentence about the file - the same one the settings page shows - so the
                // toast says what was lost, not just that something was.
                Assert.False(string.IsNullOrWhiteSpace(toast.Body));
                Assert.Equal(composition.PreferencesProblem, toast.Body);
            });

    [Fact]
    public Task A_database_that_cannot_be_opened_is_announced_as_an_error_keyed_by_the_startup() =>
        WithShellAsync(
            "forge-notify-startup-",
            dataRoot => File.WriteAllBytesAsync(
                Path.Combine(dataRoot, "data", "forge.db"),
                Encoding.UTF8.GetBytes(new string('x', 4096))),
            async (shell, composition) =>
            {
                await shell.InitializeAsync();

                Assert.True(
                    await TestPump.UntilAsync(() => composition.Notifications.Items.Count > 0),
                    "the database that could not be opened was not announced");

                var toast = Toast(composition.Notifications, "startup-failed");
                Assert.Equal(NotificationLevel.Error, toast.Level);
                Assert.Equal(Loc.Instance[NotificationsStrings.StartupProblemTitleKey], toast.Title);
                // The startup stops here: nothing else is read, so no second problem is announced over it.
                Assert.Single(composition.Notifications.Items);
                Assert.Equal(toast.Body, shell.Status.HostDetail);
            });

    [Fact]
    public Task An_initialization_fault_outside_the_database_filter_is_announced_by_the_shell_fallback() =>
        WithShellAsync(
            "forge-notify-shell-fallback-",
            static _ => Task.CompletedTask,
            async (shell, composition) =>
            {
                // The model's own catch only handles database faults (IOException/InvalidDataException/ArgumentException/
                // SqliteException); anything else escapes to the shell's outer fallback (MainWindow.InitializeAsync),
                // which must still announce startup-failed rather than crash silently.
                shell.InitializationStep = () => throw new InvalidOperationException("the workbench could not be built");

                await Forge.App.MainWindow.InitializeAsync(shell);

                Assert.True(
                    await TestPump.UntilAsync(() => composition.Notifications.Items.Count > 0),
                    "the initialization fault outside the database filter was not announced");

                var toast = Toast(composition.Notifications, "startup-failed");
                Assert.Equal(NotificationLevel.Error, toast.Level);
                Assert.Equal(Loc.Instance[NotificationsStrings.StartupProblemTitleKey], toast.Title);
                Assert.Contains("the workbench could not be built", toast.Body, StringComparison.Ordinal);
                Assert.Equal(toast.Body, shell.Status.HostDetail);
            });

    [Fact]
    public Task A_project_list_that_cannot_be_read_is_announced_as_a_warning_keyed_by_the_panel() =>
        WithShellAsync(
            "forge-notify-projects-",
            static _ => Task.CompletedTask,
            async (shell, composition) =>
            {
                shell.ProjectListRead = _ => throw new IOException("the projects table could not be read");

                await shell.InitializeAsync();

                Assert.True(
                    await TestPump.UntilAsync(() => composition.Notifications.Items.Count > 0),
                    "the project list that could not be read was not announced");

                var toast = Toast(composition.Notifications, "projects-problem");
                Assert.Equal(NotificationLevel.Warning, toast.Level);
                Assert.Equal(Loc.Instance[NotificationsStrings.StartupProblemTitleKey], toast.Title);
                Assert.Contains("the projects table could not be read", toast.Body, StringComparison.Ordinal);
                // The toast goes away; the status bar keeps the same sentence for as long as the problem stands.
                Assert.Equal(toast.Body, shell.Status.HostDetail);
            });

    [Fact]
    public Task A_purge_cleanup_whose_retry_failed_again_is_announced_as_a_warning_keyed_by_the_purge() =>
        WithShellAsync(
            "forge-notify-purge-",
            static _ => Task.CompletedTask,
            async (shell, composition) =>
            {
                // What a permanent delete left behind: one id in the journal whose files still cannot be removed.
                var journal = new PurgeCleanupJournal(Path.Combine(composition.Paths.UserDataDirectory, "purge-pending.txt"));
                await journal.AddAsync("s1", CancellationToken.None);
                composition.PurgeCleanup = new PurgeCleanup(
                    journal,
                    (_, _) => Task.FromException(new IOException("the artifacts directory is locked")),
                    (_, _) => Task.FromResult(false));

                await shell.RetryPurgeCleanupAsync();

                var toast = Toast(composition.Notifications, "purge-cleanup");
                Assert.Equal(NotificationLevel.Warning, toast.Level);
                Assert.Equal(Loc.Instance[NotificationsStrings.StartupProblemTitleKey], toast.Title);
                // The body names the id that stayed pending, so the next start is not a mystery.
                Assert.Contains("s1", toast.Body, StringComparison.Ordinal);
                Assert.Equal(toast.Body, shell.Status.HostDetail);
            });

    /// <summary>
    /// The PNG signature the composer sniffs a format from; the pixels behind it never have to decode, because
    /// <see cref="StubCodec"/> stands in for the encoder the headless session lacks.
    /// </summary>
    private static readonly byte[] PngBytes = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];

    /// <summary>
    /// Runs one composer over a throwaway data root: attachments are a session-level seam, so the composer takes its
    /// <see cref="INotifications"/> as an argument and the double records exactly what a real session would raise.
    /// </summary>
    private async Task WithComposerAsync(
        Func<ComposerViewModel, RecordingNotifications, Task> body,
        Func<string?, string, bool>? wire = null,
        IImageCodec? codec = null)
    {
        var dir = TestTempFiles.NewDirectory("forge-notify-attachments-");
        try
        {
            await SharedUi.RunAsync(
                async () =>
                {
                    using var services = ComposerSuggestionTests.TestServices.Create(dir);
                    var notifications = new RecordingNotifications();
                    using var composer = new ComposerViewModel(
                        services.Services,
                        _ => { },
                        new ComposerPreferences(),
                        dir,
                        skillSource: static () => [],
                        thinkingLevels: static (_, _) => ComposerSuggestionTests.EveryStep,
                        templateSource: static () => PromptTemplateSet.BuiltIn(),
                        imageCodec: codec,
                        wireCarries: wire,
                        notifications: notifications,
                        notificationScope: "scope");
                    await body(composer, notifications);
                });
        }
        finally
        {
            TestTempFiles.DeleteTree(dir);
        }
    }

    /// <summary>
    /// The encoder the headless session has no decoder for: it hands back bytes of a chosen size, so the encoded-size limit
    /// is reachable without a multi-megabyte fixture.
    /// </summary>
    private sealed class StubCodec(long size = -1) : IImageCodec
    {
        public PreparedImage Prepare(byte[] data, string mediaType) =>
            new(mediaType, new byte[size < 0 ? data.Length : size], null);

        public Bitmap? Thumbnail(byte[] data) => null;
    }

    /// <summary>A desktop notifier that shows nothing: these cases are about the in-window toast, never the platform.</summary>
    private sealed class SilentNotifier : ISystemNotifier
    {
        public Task NotifyAsync(string title, string body, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> TryNotifyAsync(string title, string body, CancellationToken ct) => Task.FromResult(false);
    }

    /// <summary>A provider endpoint that cannot be reached at all: the network failure the model picker announces.</summary>
    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("no route to the model list");
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

    /// <summary>
    /// Runs one shell over a fresh data root on the shared UI thread: <paramref name="breakRoot"/> breaks that root before
    /// the composition root is built over it, <paramref name="act"/> drives the shell and reads the toasts the shell's own
    /// centre announced. The shell's cases are the ones production wires through the composition root, so the centre under
    /// test is the real one (and the real dispatcher), not a recording double.
    /// </summary>
    private static async Task WithShellAsync(
        string prefix,
        Func<string, Task> breakRoot,
        Func<MainWindowViewModel, AppComposition, Task> act) =>
        await SharedUi.RunAsync(
            async () =>
            {
                var (dataRoot, previous) = TempDataRoot.Redirect(prefix);
                try
                {
                    await breakRoot(dataRoot);
                    var composition = AppComposition.Create();
                    try
                    {
                        using var shell = new MainWindowViewModel(composition);
                        await act(shell, composition);
                    }
                    finally
                    {
                        await composition.DisposeAsync();
                    }
                }
                finally
                {
                    previous.Restore();
                    TestTempFiles.DeleteTree(dataRoot);
                }
            });

    /// <summary>The one toast a path announced itself with; a missing or duplicated key fails here, not in the assertion after.</summary>
    private static NotificationItemViewModel Toast(NotificationCenter center, string key) =>
        center.Items.Single(item => string.Equals(item.Key, key, StringComparison.Ordinal));
}
