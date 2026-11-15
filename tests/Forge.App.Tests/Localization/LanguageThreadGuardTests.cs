using System.ComponentModel;
using System.Net;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Appearance;
using Forge.App.Approval;
using Forge.App.Localization;
using Forge.App.Network;
using Forge.App.Preferences;
using Forge.App.Prompts;
using Forge.App.Providers;
using Forge.App.Review;
using Forge.App.Services;
using Forge.App.Settings;
using Forge.App.Settings.Prompts;
using Forge.App.Terminal;
using Forge.App.Tests.Approval;
using Forge.App.Tests.Palette;
using Forge.App.ViewModels;
using Forge.App.Web;
using Forge.App.Workbench;
using Forge.Core.Net;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Localization;

/// <summary>
/// The creating-thread rule every <see cref="Loc.LanguageChanged"/> subscriber follows (App SPEC 62): a switch raised on
/// a thread other than the one the subscriber was built on is ignored, because the texts it refreshes are bound on its
/// own thread and a notification from anywhere else belongs to a window that is already gone. Each case is built on the
/// shared UI thread, its own handler is then invoked from a thread-pool thread (the localized property must not move)
/// and from the building thread (it must), so a guard that is too wide — one that stops language switching altogether —
/// fails here as well.
/// <para>The handlers are invoked through <see cref="LanguageSubscriptions"/> rather than by switching
/// <see cref="Loc.Language"/>: a real switch would reach every other subscriber in the process and leave global state a
/// thread-guard test has no business changing.</para>
/// </summary>
[Collection("forge-app-ui")]
public sealed class LanguageThreadGuardTests : IDisposable
{
    private readonly string root = TestTempFiles.NewDirectory("forge-language-guard-");

    public static TheoryData<string> Subscriber => new()
    {
        "language-section",
        "appearance-section",
        "terminal",
        "terminal-workbench",
        "approval-queue",
        "approval-card",
        "extensions-section",
        "compaction-block",
        "models-visibility-section",
        "prompts-section",
        "usage-log-row",
        "network-section",
        "new-branch-dialog",
        "palette",
        "files-panel",
        "changes-document",
        "todo-panel",
        "plan-panel",
        "review-panel",
        "spec-tree",
        "project-creation",
        "project-tree",
        "session-workspace",
        "web-section",
    };

    [Theory]
    [MemberData(nameof(Subscriber))]
    public async Task A_language_switch_from_another_thread_is_ignored_and_one_from_the_building_thread_still_refreshes(string name)
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var buildingThread = Environment.CurrentManagedThreadId;
                var subject = Build(name);
                try
                {
                    var raised = PropertyChangeLog.For(subject.Watched);

                    // Raised off the building thread: exactly what a stale window's subscription would see.
                    var (handlers, raisedOn) = await LanguageSubscriptions.RaiseOnPoolThreadAsync(subject.Self);
                    Assert.Equal(1, handlers);
                    Assert.NotEqual(buildingThread, raisedOn);
                    Assert.DoesNotContain(subject.Refreshed, raised);

                    // Raised on it: the refresh the subscriber exists for still happens.
                    Assert.Equal(1, LanguageSubscriptions.RaiseHere(subject.Self));
                    Assert.Contains(subject.Refreshed, raised);
                }
                finally
                {
                    await subject.CleanupAsync();
                }
            });
    }

    public void Dispose() => TestTempFiles.DeleteTree(root);

    private Subject Build(string name)
    {
        var settings = Path.Combine(root, name + "-settings.json");
        switch (name)
        {
            case "language-section":
            {
                var section = new LanguageSettingsViewModel(new AppPreferences());
                return Subject.Sync(section, section, nameof(LanguageSettingsViewModel.Hint), section.Dispose);
            }

            case "appearance-section":
            {
                var section = new AppearanceSettingsViewModel(
                    new AppPreferences(),
                    new SystemFontCatalog(static () => ["Segoe UI"]));
                // The section re-raises through its two font groups, so the group is what a binding would see.
                return Subject.Sync(section, section.Ui, nameof(FontGroupViewModel.FamilyLabel), section.Dispose);
            }

            case "terminal":
            {
                var terminal = new TerminalViewModel(
                    "s1",
                    1,
                    host: null,
                    new TerminalStartRequest("s1", root, new TerminalSize(80, 24)),
                    new AppPreferences());
                return new Subject(
                    terminal,
                    terminal,
                    nameof(TerminalViewModel.TabTitle),
                    terminal.DisposeAsync);
            }

            case "terminal-workbench":
            {
                var layout = new WorkbenchLayout(new object(), new object(), new object(), new object(), new object());
                var workbench = new TerminalWorkbench(layout, new AppPreferences(), host: null, static () => { });
                workbench.OnActiveSessionChanged("s1", root);
                var terminal = workbench.OpenTerminal();
                Assert.NotNull(terminal);
                var tab = layout.FindTab(WorkbenchIds.TerminalTab(terminal!.Id));
                Assert.NotNull(tab);
                // A title that never followed a switch — the workbench writes the terminal's own back, but only on the
                // thread it was built on.
                tab!.Title = "stale";
                return new Subject(workbench, tab, nameof(TabModel.Title), workbench.DisposeAsync);
            }

            case "approval-queue":
            {
                var queue = new ApprovalQueueViewModel(new ApprovalQueue(InlineDispatcher.Instance));
                return Subject.Sync(queue, queue, nameof(ApprovalQueueViewModel.ToggleText), queue.Dispose);
            }

            case "approval-card":
            {
                var card = new ToolApprovalCardViewModel(
                    new ToolApprovalCardRequest("s1", "Session s1", "p1", "shell_run", ToolSideEffect.Execute, root, "{}"),
                    InlineDispatcher.Instance);
                return Subject.Sync(card, card, nameof(ToolApprovalCardViewModel.Title), card.Dispose);
            }

            case "extensions-section":
            {
                var rules = new ApprovalRules(Path.Combine(root, ApprovalRules.FileName), debounceMilliseconds: 0);
                var section = new ExtensionsSettingsSectionViewModel(
                    admin: null,
                    new FakeHostState(),
                    rules,
                    (_, _) => Task.FromResult(true),
                    InlineDispatcher.Instance);
                return new Subject(
                    section,
                    section,
                    nameof(ExtensionsSettingsSectionViewModel.InstalledTitle),
                    () =>
                    {
                        section.Dispose();
                        return rules.DisposeAsync();
                    });
            }

            case "compaction-block":
            {
                var block = new CompactionSettingsViewModel(new CompactionSettingsStore(settings));
                return Subject.Sync(block, block, nameof(CompactionSettingsViewModel.Hint), block.Dispose);
            }

            case "models-visibility-section":
            {
                var section = new ModelsVisibilitySectionViewModel(TestProviderServices.UnconfiguredProbe(), new AppPreferences());
                return Subject.Sync(section, section, nameof(ModelsVisibilitySectionViewModel.Hint), section.Dispose);
            }

            case "prompts-section":
            {
                var section = new PromptsSettingsViewModel(
                    new PromptTemplateService(Path.Combine(root, "prompts")),
                    () => null);
                // The section re-reads everything it has, which it announces with the "all properties" notification.
                return Subject.Sync(section, section, null, section.Dispose);
            }

            case "usage-log-row":
            {
                var row = new UsageLogSettingsViewModel(new UsageLogSettingsStore(settings), () => root);
                return Subject.Sync(row, row, nameof(UsageLogSettingsViewModel.Label), row.Dispose);
            }

            case "network-section":
            {
                var store = new NetworkSettingsStore(settings);
                var stack = new NetworkStack(store.Current);
                var section = new NetworkSettingsSectionViewModel(
                    store,
                    stack,
                    static (_, _, _) => Task.FromResult(new[] { IPAddress.Parse("192.0.2.5") }));
                return Subject.Sync(
                    section,
                    section,
                    nameof(NetworkSettingsSectionViewModel.LoadProblemText),
                    () =>
                    {
                        section.Dispose();
                        stack.Dispose();
                    });
            }

            case "new-branch-dialog":
            {
                var dialog = new NewBranchDialogViewModel(
                    new GitCli(),
                    new NewBranchRequest(Path.Combine(root, "repository"), "feature"),
                    CancellationToken.None);
                return Subject.Sync(dialog, dialog, nameof(NewBranchDialogViewModel.BaseBranchLabel), dialog.Dispose);
            }

            case "palette":
            {
                var palette = new PaletteViewModel(new FakePaletteHost(), new AppPreferences());
                return Subject.Sync(palette, palette, nameof(PaletteViewModel.HeaderText), palette.Dispose);
            }

            case "files-panel":
            {
                var panel = new FilesPanelViewModel(new GitCli(), static _ => { }, static _ => { }, static _ => { });
                return Subject.Sync(panel, panel, nameof(FilesPanelViewModel.FoldingToggleText), panel.Dispose);
            }

            case "changes-document":
            {
                var changes = new ChangesViewModel(new GitCli(), new EmptyCheckpointStore());
                return Subject.Sync(changes, changes, nameof(ChangesViewModel.ScopeLabel), changes.Dispose);
            }

            case "todo-panel":
            {
                var panel = new TodoListViewModel(new EmptyTodoStore("t1", DateTimeOffset.UnixEpoch));
                return Subject.Sync(panel, panel, nameof(TodoListViewModel.StatusText), panel.Dispose);
            }

            case "plan-panel":
            {
                var panel = new PlanPanelViewModel(new FakePlanStore(), new FakePlanHost());
                return Subject.Sync(panel, panel, nameof(PlanPanelViewModel.StatusText), panel.Dispose);
            }

            case "review-panel":
            {
                var panel = new ReviewPanelViewModel(new ReviewStore(Path.Combine(root, "review-data")), static _ => { });
                return Subject.Sync(panel, panel, nameof(ReviewPanelViewModel.Summary), panel.Dispose);
            }

            case "spec-tree":
            {
                var panel = new SpecTreeViewModel(static path => new SpecGraph(path), static _ => { });
                return Subject.Sync(panel, panel, nameof(SpecTreeViewModel.Summary), panel.Dispose);
            }

            case "project-creation":
            {
                var parent = Path.Combine(root, "projects");
                Directory.CreateDirectory(parent);
                var dialog = new ProjectCreationViewModel(
                    ProjectCreationMode.Create,
                    new ProjectCreationService(new GitCli(), new AppPreferences { ProjectsCloneDirectory = parent }, parent));
                return Subject.Sync(dialog, dialog, nameof(ProjectCreationViewModel.TitleText), dialog.Dispose);
            }

            case "project-tree":
            {
                var tree = new ProjectTreeViewModel(
                    new EmptyProjectRegistry(),
                    new EmptySessionCatalog(DateTimeOffset.UnixEpoch),
                    new WorktreeService(new GitCli()),
                    static (_, _) => Task.FromResult(new GitHead(GitHeadKind.Branch, "main")),
                    static (_, _) => Task.CompletedTask,
                    static _ => Task.FromResult(true),
                    static (_, _) => Task.FromResult<string?>(null),
                    static (_, _) => Task.FromResult<NewBranchChoice?>(null),
                    static _ => Task.CompletedTask,
                    static _ => Task.CompletedTask);
                return Subject.Sync(tree, tree, nameof(ProjectTreeViewModel.StatusText), tree.Dispose);
            }

            case "session-workspace":
            {
                var workspace = new SessionWorkspaceViewModel(
                    new SessionInfo("s1", root, "demo session", DateTimeOffset.UnixEpoch),
                    runtime: null,
                    checkpoints: null,
                    store: null,
                    TestProviderServices.Unconfigured(root));
                return Subject.Sync(
                    workspace,
                    workspace,
                    nameof(SessionWorkspaceViewModel.ReadOnlyBanner),
                    workspace.Dispose);
            }

            case "web-section":
            {
                var http = new HttpClient();
                var services = new WebServices(
                    new WebSettingsStore(Path.Combine(root, "web-settings.json")),
                    new ProviderSecretStore(Path.Combine(root, "web-secrets")),
                    http);
                var section = new WebSettingsSectionViewModel(services);
                return Subject.Sync(
                    section,
                    section,
                    nameof(WebSettingsSectionViewModel.Backends),
                    () =>
                    {
                        section.Dispose();
                        services.Dispose();
                        http.Dispose();
                    });
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, "add the subscriber's construction here");
        }
    }

    /// <summary>A project registry with nothing in it: the tree only needs the seam, never an answer.</summary>
    private sealed class EmptyProjectRegistry : IProjectRegistry
    {
        public Task<IReadOnlyList<ProjectInfo>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProjectInfo>>([]);

        public Task<ProjectInfo> AddAsync(string rootPath, string name, ProjectKind kind, CancellationToken ct) =>
            Task.FromResult(new ProjectInfo("general", name, rootPath, kind, 0, DateTimeOffset.UtcNow));

        public Task<bool> RemoveAsync(string projectId, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> RenameAsync(string projectId, string name, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> MoveAsync(string projectId, int sortOrder, CancellationToken ct) => Task.FromResult(false);
    }

    /// <summary>
    /// One guarded subscriber: <see cref="Self"/> owns the <see cref="Loc.LanguageChanged"/> handler, <see cref="Watched"/>
    /// is the object a binding would listen to (usually the same one), and <see cref="Refreshed"/> the property a language
    /// switch re-raises on it — <c>null</c> where the subscriber announces "everything changed" instead.
    /// </summary>
    private sealed record Subject(object Self, INotifyPropertyChanged Watched, string? Refreshed, Func<ValueTask> Cleanup)
    {
        public ValueTask CleanupAsync() => Cleanup();

        public static Subject Sync(object self, INotifyPropertyChanged watched, string? refreshed, Action cleanup) =>
            new(self, watched, refreshed, () =>
            {
                cleanup();
                return default;
            });
    }
}
