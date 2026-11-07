using System.ComponentModel;
using System.Reflection;
using Avalonia.Headless;
using Forge.App.Git;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.ViewModels;
using Forge.App.Workbench;
using Forge.Workspace;
using Xunit;
using static Forge.App.Tests.Git.GitPanelTesting;

namespace Forge.App.Tests.Git;

/// <summary>
/// Language and lifetime contracts of the Git panel and the Git settings section: the panel (and the main window
/// around it) re-reads every localized text on a language switch; <c>Dispose</c> cancels the read and the write, drops
/// the <see cref="Loc.LanguageChanged"/> subscription and silences the panel; a language notification raised on another
/// thread is ignored. The tests assert view-model properties after a switch, not XAML-bound text (that belongs to the
/// live-switch suite). The panel and the section capture the UI thread at construction, so each is built inside the
/// dispatch it is used in.
/// </summary>
[Collection("forge-app-ui")]
public sealed class GitPanelLanguageAndDisposeTests
{
    // ------------------------------------------------------------ 4. language switch: panel

    [Fact]
    public async Task A_language_switch_re_reads_branch_upstream_result_and_hint_of_the_panel()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("a");
        var head = GitTestShell.RunChecked(work, "rev-parse", "HEAD").Trim();
        GitTestShell.RunChecked(work, "checkout", "-q", "--detach");
        var shortHead = head[..7];

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(
            async () =>
            {
                await WithLanguageAsync(
                    UiLanguage.Chinese,
                    async () =>
                    {
                        var panel = NewPanel();
                        try
                        {
                            panel.Attach(Context("s1", work));
                            await WaitIdleAsync(panel);

                            // A panel-local failure keeps its Loc key, so its text follows the language.
                            panel.NewBranchName = string.Empty;
                            panel.CreateBranchCommand.Execute(null);

                            var chinese = Snapshot(panel);
                            Assert.Equal(Loc.Instance.Format(GitStrings.DetachedHead, shortHead), chinese.Branch);
                            Assert.Equal(Loc.Instance[GitStrings.NoUpstream], chinese.Upstream);
                            Assert.Equal(Loc.Instance[GitStrings.ErrorBranchNameRequired], chinese.Result);
                            Assert.Equal(Loc.Instance[GitStrings.HintOther], chinese.Hint);

                            var changed = new List<string>();
                            panel.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);
                            Loc.Instance.Language = UiLanguage.English;

                            var english = Snapshot(panel);
                            Assert.Equal(Loc.Instance.Format(GitStrings.DetachedHead, shortHead), english.Branch);
                            Assert.Equal(Loc.Instance[GitStrings.NoUpstream], english.Upstream);
                            Assert.Equal(Loc.Instance[GitStrings.ErrorBranchNameRequired], english.Result);
                            Assert.Equal(Loc.Instance[GitStrings.HintOther], english.Hint);
                            Assert.Equal("(detached at " + shortHead + ")", english.Branch);
                            Assert.Equal("no upstream", english.Upstream);
                            Assert.Equal("Type a branch name first.", english.Result);

                            // The two languages really differ for every one of these, so the assertions above are not vacuous.
                            Assert.NotEqual(chinese.Branch, english.Branch);
                            Assert.NotEqual(chinese.Upstream, english.Upstream);
                            Assert.NotEqual(chinese.Result, english.Result);
                            Assert.NotEqual(chinese.Hint, english.Hint);

                            foreach (var name in new[]
                            {
                                nameof(GitPanelViewModel.Title),
                                nameof(GitPanelViewModel.BranchText),
                                nameof(GitPanelViewModel.UpstreamText),
                                nameof(GitPanelViewModel.AheadBehindText),
                                nameof(GitPanelViewModel.LastResultText),
                                nameof(GitPanelViewModel.HintText),
                            })
                            {
                                Assert.Contains(name, changed);
                            }

                            Assert.Equal(Loc.Instance[GitStrings.TabTitle], panel.Title);
                            Assert.False(panel.IsBusy);

                            // And back.
                            Loc.Instance.Language = UiLanguage.Chinese;
                            Assert.Equal(chinese, Snapshot(panel));
                        }
                        finally
                        {
                            panel.Dispose();
                        }

                        return 0;
                    });
                return 0;
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task A_language_switch_re_formats_the_ahead_behind_text_of_the_panel()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("a");
        sandbox.CommitFile(work, "local.txt", "local\n", "local work");

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(
            async () =>
            {
                await WithLanguageAsync(
                    UiLanguage.Chinese,
                    async () =>
                    {
                        var panel = NewPanel();
                        try
                        {
                            panel.Attach(Context("s1", work));
                            await WaitIdleAsync(panel);
                            Assert.Equal(Loc.Instance.Format(GitStrings.AheadBehind, 1, 0), panel.AheadBehindText);

                            var changed = new List<string>();
                            panel.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);
                            Loc.Instance.Language = UiLanguage.English;

                            Assert.Contains(nameof(GitPanelViewModel.AheadBehindText), changed);
                            Assert.Contains(nameof(GitPanelViewModel.Title), changed);
                            Assert.Equal(Loc.Instance.Format(GitStrings.AheadBehind, 1, 0), panel.AheadBehindText);
                            Assert.Equal("origin/main", panel.UpstreamText);
                            Assert.Equal("main", panel.BranchText);
                        }
                        finally
                        {
                            panel.Dispose();
                        }

                        return 0;
                    });
                return 0;
            },
            CancellationToken.None);
    }

    // ------------------------------------------------------------ 4. language switch: main window

    [Fact]
    public async Task A_language_switch_refreshes_the_git_tab_title_the_title_bar_branch_and_the_status_bar_text()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("a");
        var head = GitTestShell.RunChecked(work, "rev-parse", "HEAD").Trim();
        GitTestShell.RunChecked(work, "checkout", "-q", "--detach");
        var sessionDir = Directory.CreateDirectory(Path.Combine(sandbox.Root, "session-work")).FullName;

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(
            async () =>
            {
                await WithLanguageAsync(
                    UiLanguage.Chinese,
                    async () =>
                    {
                        await using var harness = await GitAppHarness.OpenWithSession(sessionDir);
                        var viewModel = harness.ViewModel;
                        try
                        {
                            viewModel.GitPanel.Attach(Context(viewModel.ActiveSession!.SessionId, work));
                            await WaitIdleAsync(viewModel.GitPanel);

                            var tab = viewModel.Workbench.GitPanel;
                            Assert.NotNull(tab);
                            var chineseBranch = Loc.Instance.Format(GitStrings.DetachedHead, head[..7]);
                            Assert.Equal(chineseBranch, viewModel.ActiveBranchText);

                            // "Git" and "↑0 ↓0" read the same in both languages, so plant stale values that only a
                            // language-driven refresh can overwrite.
                            tab!.Title = "stale";
                            viewModel.Status.SetAheadBehind("stale");
                            var changed = new List<string>();
                            viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

                            Loc.Instance.Language = UiLanguage.English;

                            Assert.Equal(Loc.Instance[GitStrings.TabTitle], tab.Title);
                            Assert.Equal(viewModel.GitPanel.Title, tab.Title);
                            Assert.Equal("(detached at " + head[..7] + ")", viewModel.ActiveBranchText);
                            Assert.NotEqual(chineseBranch, viewModel.ActiveBranchText);
                            Assert.Contains(nameof(MainWindowViewModel.ActiveBranchText), changed);
                            Assert.Equal(viewModel.GitPanel.AheadBehindText, viewModel.Status.AheadBehindText);
                            Assert.NotEqual("stale", viewModel.Status.AheadBehindText);
                        }
                        finally
                        {
                            harness.Detach(viewModel);
                        }

                        return 0;
                    });
                return 0;
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task A_language_switch_re_applies_the_ahead_behind_text_to_the_status_bar()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("a");
        sandbox.CommitFile(work, "local.txt", "local\n", "local work");
        var sessionDir = Directory.CreateDirectory(Path.Combine(sandbox.Root, "session-work")).FullName;

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(
            async () =>
            {
                await WithLanguageAsync(
                    UiLanguage.Chinese,
                    async () =>
                    {
                        await using var harness = await GitAppHarness.OpenWithSession(sessionDir);
                        var viewModel = harness.ViewModel;
                        try
                        {
                            viewModel.GitPanel.Attach(Context(viewModel.ActiveSession!.SessionId, work));
                            await WaitIdleAsync(viewModel.GitPanel);
                            var expected = Loc.Instance.Format(GitStrings.AheadBehind, 1, 0);
                            Assert.Equal(expected, viewModel.Status.AheadBehindText);

                            viewModel.Status.SetAheadBehind("stale");
                            Loc.Instance.Language = UiLanguage.English;

                            Assert.Equal(expected, viewModel.Status.AheadBehindText);
                            Assert.True(viewModel.Status.HasAheadBehind);
                        }
                        finally
                        {
                            harness.Detach(viewModel);
                        }

                        return 0;
                    });
                return 0;
            },
            CancellationToken.None);
    }

    // ------------------------------------------------------------ Dispose

    [Fact]
    public async Task Dispose_cancels_the_write_in_flight_and_goes_silent()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("a");
        await using var stub = new StubHttpRemote(hold: true);
        GitTestSandbox.PointOriginAt(work, stub.Url);

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(
            async () =>
            {
                // The network timeout is far beyond the wait below, so only Dispose can end the held git call in time.
                var panel = NewPanel(networkTimeout: TimeSpan.FromMinutes(5));
                panel.Attach(Context("s1", work));
                await WaitIdleAsync(panel);
                panel.FetchCommand.Execute(null);
                Assert.True(panel.IsBusy);
                await stub.WaitForRequestAsync();

                var changed = new List<string>();
                panel.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);
                panel.Dispose();

                Assert.False(panel.IsBusy, "a disposed panel is never busy");
                Assert.False(panel.FetchCommand.CanExecute(null));
                Assert.False(panel.RefreshCommand.CanExecute(null));

                // The write token was cancelled: git is killed, which the stub sees as the client going away.
                await stub.ClientGone.WaitAsync(TimeSpan.FromSeconds(15));
                await QuietWindowAsync();

                // Nothing is applied after dispose: no result, no re-read, no property change but the IsBusy flip.
                Assert.False(panel.HasResult);
                Assert.Equal(string.Empty, panel.HintText);
                Assert.All(changed, static name => Assert.Equal(nameof(GitPanelViewModel.IsBusy), name));

                // Attach and commands after dispose do nothing.
                changed.Clear();
                panel.Attach(Context("s2", work));
                panel.NotifyTurnCompleted("s2");
                panel.FetchCommand.Execute(null);
                await QuietWindowAsync(200);
                Assert.Empty(changed);
                Assert.False(panel.IsBusy);
                Assert.Equal("main", panel.BranchText);

                // Dispose is idempotent.
                panel.Dispose();
                return 0;
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task Dispose_cancels_the_read_in_flight_so_nothing_is_applied()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("a");

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(
            async () =>
            {
                var panel = NewPanel();
                var resets = 0;
                panel.Branches.CollectionChanged += (_, e) =>
                {
                    if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                    {
                        resets++;
                    }
                };

                panel.Attach(Context("s1", work));
                Assert.True(panel.IsBusy);
                var resetsAfterAttach = resets;
                panel.Dispose();
                Assert.False(panel.IsBusy);

                await QuietWindowAsync();
                Assert.Equal(resetsAfterAttach, resets);
                Assert.False(panel.HasRepository);
                Assert.Equal(string.Empty, panel.BranchText);
                Assert.False(panel.IsBusy);
                return 0;
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task Dispose_while_the_delete_dialog_is_open_drops_the_delete()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("a");
        GitTestShell.RunChecked(work, "branch", "topic");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(
            async () =>
            {
                var panel = NewPanel(confirm: (_, _) =>
                {
                    entered.TrySetResult();
                    return gate.Task;
                });
                panel.Attach(Context("s1", work));
                await WaitIdleAsync(panel);
                panel.SelectedBranch = "topic";
                panel.DeleteBranchCommand.Execute(null);
                await entered.Task.WaitAsync(Budget);
                Assert.True(panel.IsBusy);

                var changed = new List<string>();
                panel.Dispose();
                panel.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);
                gate.SetResult(true);
                await QuietWindowAsync();

                Assert.Contains("topic", GitTestShell.BranchList(work));
                Assert.Empty(changed);
                Assert.False(panel.HasResult);
                return 0;
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task Dispose_unsubscribes_the_panel_from_language_changes()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(
            async () =>
            {
                await WithLanguageAsync(
                    UiLanguage.Chinese,
                    () =>
                    {
                        var panel = NewPanel();
                        Assert.Contains(panel, LanguageSubscribers());

                        var changed = new List<string>();
                        panel.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);
                        Loc.Instance.Language = UiLanguage.English;
                        Assert.Contains(nameof(GitPanelViewModel.Title), changed);

                        panel.Dispose();
                        Assert.DoesNotContain(panel, LanguageSubscribers());

                        changed.Clear();
                        Loc.Instance.Language = UiLanguage.Chinese;
                        Assert.Empty(changed);
                        return Task.FromResult(0);
                    });
                return 0;
            },
            CancellationToken.None);
    }

    // ------------------------------------------------------------ 7. threads

    [Fact]
    public async Task The_panel_ignores_a_language_change_raised_on_another_thread()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(
            async () =>
            {
                await WithLanguageAsync(
                    UiLanguage.Chinese,
                    async () =>
                    {
                        var panel = NewPanel();
                        try
                        {
                            var changed = new List<string>();
                            panel.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

                            // The language really is English when the off-thread notification arrives, so a panel that
                            // handled it would have texts to refresh.
                            Loc.Instance.Language = UiLanguage.English;
                            Assert.Contains(nameof(GitPanelViewModel.Title), changed);
                            changed.Clear();

                            // The panel's own subscription is invoked from a pool thread. The real setter is not used for
                            // this: its PropertyChanged("Item[]") would also be posted to the XAML bindings of every window
                            // an earlier test of this process left alive, and the dispatcher reset after this test then
                            // fails on a control owned by a finished session.
                            var uiThread = Environment.CurrentManagedThreadId;
                            var (handlers, raisedOn) = await RaiseLanguageChangedOnAnotherThreadAsync(panel);
                            Assert.Equal(1, handlers);
                            Assert.NotEqual(uiThread, raisedOn);
                            Assert.Empty(changed);

                            // Control: the same notification on the UI thread is handled.
                            Loc.Instance.Language = UiLanguage.Chinese;
                            Assert.Contains(nameof(GitPanelViewModel.Title), changed);
                        }
                        finally
                        {
                            panel.Dispose();
                        }

                        return 0;
                    });
                return 0;
            },
            CancellationToken.None);
    }

    // ------------------------------------------------------------ 7. settings section

    [Fact]
    public async Task The_git_settings_section_stops_reacting_to_language_changes_after_dispose()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(
            async () =>
            {
                await WithLanguageAsync(
                    UiLanguage.Chinese,
                    () =>
                    {
                        var section = new GitSettingsSectionViewModel(new AppPreferences { GitPullStrategy = "rebase" });
                        Assert.IsAssignableFrom<IDisposable>(section);
                        Assert.Contains(section, LanguageSubscribers());

                        var changed = new List<string>();
                        section.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);
                        var before = section.PullStrategies;
                        Loc.Instance.Language = UiLanguage.English;
                        Assert.Contains(nameof(GitSettingsSectionViewModel.PullStrategies), changed);
                        Assert.Contains(nameof(GitSettingsSectionViewModel.SelectedPullStrategy), changed);
                        Assert.NotSame(before, section.PullStrategies);
                        Assert.Equal(GitPullStrategy.Rebase, section.SelectedPullStrategy.Value);

                        section.Dispose();
                        Assert.DoesNotContain(section, LanguageSubscribers());
                        var afterDispose = section.PullStrategies;
                        changed.Clear();
                        Loc.Instance.Language = UiLanguage.Chinese;
                        Assert.Empty(changed);
                        Assert.Same(afterDispose, section.PullStrategies);

                        // Idempotent.
                        section.Dispose();
                        return Task.FromResult(0);
                    });
                return 0;
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task The_git_settings_section_ignores_a_language_change_raised_on_another_thread()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(
            async () =>
            {
                await WithLanguageAsync(
                    UiLanguage.Chinese,
                    async () =>
                    {
                        var section = new GitSettingsSectionViewModel(new AppPreferences());
                        try
                        {
                            var changed = new List<string>();
                            section.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

                            Loc.Instance.Language = UiLanguage.English;
                            Assert.Contains(nameof(GitSettingsSectionViewModel.PullStrategies), changed);
                            changed.Clear();
                            var before = section.PullStrategies;

                            // See the panel test: only the section's own subscription is invoked, from a pool thread.
                            var uiThread = Environment.CurrentManagedThreadId;
                            var (handlers, raisedOn) = await RaiseLanguageChangedOnAnotherThreadAsync(section);
                            Assert.Equal(1, handlers);
                            Assert.NotEqual(uiThread, raisedOn);

                            Assert.Empty(changed);
                            Assert.Same(before, section.PullStrategies);

                            Loc.Instance.Language = UiLanguage.Chinese;
                            Assert.Contains(nameof(GitSettingsSectionViewModel.PullStrategies), changed);
                        }
                        finally
                        {
                            section.Dispose();
                        }

                        return 0;
                    });
                return 0;
            },
            CancellationToken.None);
    }

    // ------------------------------------------------------------ helpers

    private static (string Branch, string Upstream, string Result, string Hint) Snapshot(GitPanelViewModel panel) =>
        (panel.BranchText, panel.UpstreamText, panel.LastResultText, panel.HintText);

    /// <summary>The objects currently subscribed to <see cref="Loc.LanguageChanged"/> (the event is field-like).</summary>
    private static List<object?> LanguageSubscribers()
    {
        var field = typeof(Loc).GetField(nameof(Loc.LanguageChanged), BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Loc.LanguageChanged is no longer a field-like event; update this helper.");
        var handler = (MulticastDelegate?)field.GetValue(Loc.Instance);
        return handler is null ? [] : handler.GetInvocationList().Select(static d => d.Target).ToList();
    }

    /// <summary>
    /// Invokes the <see cref="Loc.LanguageChanged"/> handlers that belong to <paramref name="subscriber"/> on a thread-pool
    /// thread, exactly as the event would reach them if the language were switched off the UI thread. Returns how many
    /// handlers ran and the managed id of the thread they ran on.
    /// </summary>
    private static Task<(int Handlers, int ThreadId)> RaiseLanguageChangedOnAnotherThreadAsync(object subscriber)
    {
        var field = typeof(Loc).GetField(nameof(Loc.LanguageChanged), BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Loc.LanguageChanged is no longer a field-like event; update this helper.");
        var handlers = ((MulticastDelegate?)field.GetValue(Loc.Instance))?
            .GetInvocationList()
            .Where(d => ReferenceEquals(d.Target, subscriber))
            .ToArray() ?? [];
        return Task.Run(
            () =>
            {
                foreach (var handler in handlers)
                {
                    handler.DynamicInvoke(Loc.Instance, EventArgs.Empty);
                }

                return (handlers.Length, Environment.CurrentManagedThreadId);
            });
    }

    private static async Task<T> WithLanguageAsync<T>(UiLanguage language, Func<Task<T>> body)
    {
        var previous = Loc.Instance.Language;
        Loc.Instance.Language = language;
        try
        {
            return await body();
        }
        finally
        {
            Loc.Instance.Language = previous;
        }
    }
}
