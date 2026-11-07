using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.ViewModels;
using Forge.App.Workbench;
using Forge.App.Workbench.Controls;
using Forge.HostClient;
using Xunit;

namespace Forge.App.LiveSwitch.Tests;

/// <summary>
/// Pins the live language switch end to end (task design, "防回归"): with a real window open, Chinese → English →
/// Chinese refreshes the title-bar settings button, the status bar, the fixed panel titles, the language section and
/// a turn card header, and the preference change travels through <c>AppComposition</c> into <see cref="Loc"/>.
/// A second test pins that opening and closing sessions never accumulates <see cref="Loc.LanguageChanged"/>
/// subscriptions.
/// <para>Every test shares the assembly's single headless session (<see cref="HeadlessSessionFixture"/>): a window of
/// an earlier session would otherwise keep a binding on another dispatcher thread and break delivery to this one.</para>
/// </summary>
[Collection(LiveSwitchCollection.Name)]
public sealed class LiveSwitchTests(HeadlessSessionFixture fixture)
{
    private static readonly TimeSpan StepBudget = TimeSpan.FromSeconds(60);

    private static readonly DateTimeOffset CardAt = new(2026, 11, 5, 12, 30, 45, TimeSpan.Zero);

    private const int PanelWaitAttempts = 200;

    [Fact]
    public async Task Title_bar_settings_button_tip_and_name_follow_the_language_live()
    {
        var session = fixture.Session;
        await using var shell = await LiveShell.OpenAsync(session);
        var token = new CancellationTokenSource(StepBudget).Token;
        var previous = Loc.Instance.Language;
        try
        {
            await shell.SwitchAsync(UiLanguage.Chinese, token);
            var chinese = await LiveShell.WaitForSettingsButtonAsync(shell, "设置", "设置", token);
            Assert.Equal("设置", chinese.Tip);
            Assert.Equal("设置", chinese.Name);

            await shell.SwitchAsync(UiLanguage.English, token);
            var english = await LiveShell.WaitForSettingsButtonAsync(shell, "Settings", "Settings", token);
            Assert.True(
                string.Equals(english.Tip, "Settings", StringComparison.Ordinal) &&
                string.Equals(english.Name, "Settings", StringComparison.Ordinal),
                $"Tip={english.Tip ?? "<null>"} Name={english.Name ?? "<null>"} " +
                $"Loc={Loc.Instance.Language} key={Loc.Instance["shell.settings"]}");

            await shell.SwitchAsync(UiLanguage.Chinese, token);
            var back = await LiveShell.WaitForSettingsButtonAsync(shell, "设置", "设置", token);
            Assert.Equal("设置", back.Tip);
            Assert.Equal("设置", back.Name);
        }
        finally
        {
            await shell.SwitchAsync(previous, token);
        }
    }

    [Fact]
    public async Task Status_bar_host_text_follows_the_language_live()
    {
        var session = fixture.Session;
        await using var shell = await LiveShell.OpenAsync(session);
        var token = new CancellationTokenSource(StepBudget).Token;
        var previous = Loc.Instance.Language;
        try
        {
            await session.Dispatch(
                () =>
                {
                    shell.ViewModel.Status.SetHost(HostStatus.Running, null);
                    return 0;
                },
                token);

            await shell.SwitchAsync(UiLanguage.Chinese, token);
            var chinese = await session.Dispatch(() => shell.ViewModel.Status.HostText, token);
            Assert.Equal("扩展宿主：运行中", chinese);

            await shell.SwitchAsync(UiLanguage.English, token);
            var english = await session.Dispatch(() => shell.ViewModel.Status.HostText, token);
            Assert.Equal("Extension host: running", english);

            await shell.SwitchAsync(UiLanguage.Chinese, token);
            var back = await session.Dispatch(() => shell.ViewModel.Status.HostText, token);
            Assert.Equal("扩展宿主：运行中", back);
        }
        finally
        {
            await shell.SwitchAsync(previous, token);
        }
    }

    [Fact]
    public async Task Fixed_panel_titles_follow_the_language_live()
    {
        var session = fixture.Session;
        await using var shell = await LiveShell.OpenAsync(session);
        var token = new CancellationTokenSource(StepBudget).Token;
        var previous = Loc.Instance.Language;
        try
        {
            await shell.SwitchAsync(UiLanguage.Chinese, token);
            var chinese = await session.Dispatch(() => LiveShell.ReadPanelTitles(shell.ViewModel), token);
            Assert.Equal(
                ["项目", "文件", "会话", "子 Agent", "预览", "TODO | 评审 | 规格树", "TODO", "评审", "规格树"],
                chinese);

            await shell.SwitchAsync(UiLanguage.English, token);
            var english = await session.Dispatch(() => LiveShell.ReadPanelTitles(shell.ViewModel), token);
            Assert.Equal(
                ["Projects", "Files", "Sessions", "Sub-agents", "Preview", "TODO | Review | Spec tree", "TODO", "Review", "Spec tree"],
                english);

            await shell.SwitchAsync(UiLanguage.Chinese, token);
            var back = await session.Dispatch(() => LiveShell.ReadPanelTitles(shell.ViewModel), token);
            Assert.Equal(chinese, back);
        }
        finally
        {
            await shell.SwitchAsync(previous, token);
        }
    }

    [Fact]
    public async Task Language_section_lists_three_choices_and_preferences_drive_the_live_language()
    {
        var session = fixture.Session;
        await using var shell = await LiveShell.OpenAsync(session);
        var token = new CancellationTokenSource(StepBudget).Token;
        var previousPreference = shell.Composition.Preferences.Language;
        var previous = Loc.Instance.Language;
        try
        {
            await shell.OpenSettingsAsync(token);

            await shell.SwitchAsync(UiLanguage.Chinese, token);
            var chinese = await LiveShell.WaitForLanguageSectionAsync(shell, "语言", ["跟随系统", "中文", "English"], token);
            Assert.Equal("语言", chinese.Heading);
            Assert.Equal(["跟随系统", "中文", "English"], chinese.Options);

            await shell.SwitchAsync(UiLanguage.English, token);
            var english = await LiveShell.WaitForLanguageSectionAsync(shell, "Language", ["Follow system", "Chinese", "English"], token);
            Assert.Equal("Language", english.Heading);
            Assert.Equal(["Follow system", "Chinese", "English"], english.Options);

            // The composition root pushes the preference into Loc: flipping the preference flips the UI language.
            await session.Dispatch(
                () =>
                {
                    shell.Composition.Preferences.Language = UiLanguage.English;
                    return 0;
                },
                token);
            Assert.Equal(UiLanguage.English, Loc.Instance.Language);
        }
        finally
        {
            await session.Dispatch(
                () =>
                {
                    shell.Composition.Preferences.Language = previousPreference;
                    return 0;
                },
                token);
            await shell.SwitchAsync(previous, token);
        }
    }

    [Fact]
    public async Task Turn_card_header_follows_the_language_live()
    {
        var session = fixture.Session;
        await using var shell = await LiveShell.OpenAsync(session);
        var token = new CancellationTokenSource(StepBudget).Token;
        var previous = Loc.Instance.Language;
        try
        {
            await shell.SwitchAsync(UiLanguage.Chinese, token);
            var chinese = await session.Dispatch(() => LiveShell.ThirdTurnHeader(), token);
            Assert.Contains("第 3 轮", chinese);
            Assert.Contains("输入 900 · 输出 80", chinese);

            await shell.SwitchAsync(UiLanguage.English, token);
            var english = await session.Dispatch(() => LiveShell.ThirdTurnHeader(), token);
            Assert.Contains("Turn 3", english);
            Assert.Contains("in 900 · out 80", english);

            await shell.SwitchAsync(UiLanguage.Chinese, token);
            var back = await session.Dispatch(() => LiveShell.ThirdTurnHeader(), token);
            Assert.Contains("第 3 轮", back);
        }
        finally
        {
            await shell.SwitchAsync(previous, token);
        }
    }

    [Fact]
    public async Task Tab_context_menu_items_follow_the_language_live()
    {
        var session = fixture.Session;
        await using var shell = await LiveShell.OpenAsync(session);
        var token = new CancellationTokenSource(StepBudget).Token;
        var previous = Loc.Instance.Language;
        try
        {
            await session.Dispatch(
                () =>
                {
                    shell.ViewModel.Workbench.OpenTab(
                        shell.ViewModel.Workbench.MainTabs, "live-switch-tab", TabKind.Session, new object(), "t");
                    return 0;
                },
                token);

            string[] zh = ["在新窗口打开", "关闭", "关闭其他标签"];
            string[] en = ["Open in new window", "Close", "Close other tabs"];

            await shell.SwitchAsync(UiLanguage.Chinese, token);
            Assert.Equal(zh, await LiveShell.WaitForTabMenuAsync(shell, "live-switch-tab", zh, token));

            await shell.SwitchAsync(UiLanguage.English, token);
            Assert.Equal(en, await LiveShell.WaitForTabMenuAsync(shell, "live-switch-tab", en, token));

            await shell.SwitchAsync(UiLanguage.Chinese, token);
            Assert.Equal(zh, await LiveShell.WaitForTabMenuAsync(shell, "live-switch-tab", zh, token));
        }
        finally
        {
            await shell.SwitchAsync(previous, token);
        }
    }

    [Fact]
    public async Task Empty_session_state_text_follows_the_language_live()
    {
        var session = fixture.Session;
        var token = new CancellationTokenSource(StepBudget).Token;
        var previous = Loc.Instance.Language;
        Window? host = null;
        SessionWorkspaceViewModel? workspace = null;
        try
        {
            await session.Dispatch(
                () =>
                {
                    Loc.Instance.Language = UiLanguage.Chinese;
                    workspace = CreateWorkspace();
                    workspace.Transcript.Clear();
                    Assert.True(workspace.Transcript.IsEmpty);
                    host = new Window { Content = new Forge.App.Views.SessionView { DataContext = workspace } };
                    host.Show();
                    LiveShell.Settle(host);
                    return 0;
                },
                token);

            var first = await ReadEmptyStateAsync(session, host!, UiLanguage.Chinese, token);
            Assert.Equal("开始对话", first[0]);
            var chinese = await ReadEmptyStateAsync(session, host!, UiLanguage.Chinese, token);

            var english = await ReadEmptyStateAsync(session, host!, UiLanguage.English, token);
            Assert.Equal("Start a conversation", english[0]);
            Assert.NotEqual(chinese[1], english[1]);
            Assert.DoesNotContain(english, static text => text.Any(static c => c is >= '\u4e00' and <= '\u9fff'));

            var back = await ReadEmptyStateAsync(session, host!, UiLanguage.Chinese, token);
            Assert.Equal(chinese, back);
        }
        finally
        {
            await session.Dispatch(
                () =>
                {
                    Loc.Instance.Language = previous;
                    host?.Close();
                    return 0;
                },
                token);
        }
    }

    /// <summary>
    /// Switches to <paramref name="language"/> and returns the empty-state title and hint as rendered: the two text
    /// blocks that show the registered keys, polled until they match the table of that language.
    /// </summary>
    private static async Task<string[]> ReadEmptyStateAsync(
        HeadlessUnitTestSession session, Window host, UiLanguage language, CancellationToken token)
    {
        await session.Dispatch(
            () =>
            {
                Loc.Instance.Language = language;
                LiveShell.Settle(host);
                return 0;
            },
            token);
        var dbg = "";
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        string[] last = [];
        while (DateTime.UtcNow < deadline)
        {
            last = await session.Dispatch(
                () =>
                {
                    LiveShell.Settle(host);
                    string[] expected =
                    [
                        Loc.Instance[SessionStrings.StartConversationKey],
                        Loc.Instance[SessionStrings.ChatHintKey],
                    ];
                    var texts = host.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>()
                        .Where(static block => block.IsEffectivelyVisible)
                        .Select(static block => block.Text ?? string.Empty).ToArray();
                    var found = expected.Where(texts.Contains).ToArray();
                    if (found.Length != 2) { dbg = string.Join(" | ", texts) + " ;; " + string.Join(" | ", expected); }
                    return found;
                },
                token);
            if (last.Length == 2)
            {
                return last;
            }

            await Task.Delay(50, token);
        }

        Assert.Fail("empty-state texts did not show the " + language + " table; saw: " + string.Join(" | ", last) + " DBG " + dbg);
        return last;
    }

    [Fact]
    public async Task Language_subscriptions_do_not_grow_with_opened_and_closed_sessions()
    {
        var session = fixture.Session;
        await using var shell = await LiveShell.OpenAsync(session);
        var token = new CancellationTokenSource(StepBudget).Token;

        var baseline = await session.Dispatch(() => LiveShell.LanguageSubscriberCount(), token);

        await session.Dispatch(
            () =>
            {
                for (var i = 0; i < 8; i++)
                {
                    using var workspace = CreateWorkspace();
                    using var layout = new WorkbenchLayout(new object(), new object(), new object(), new object(), new object());
                }

                return 0;
            },
            token);

        var after = await session.Dispatch(() => LiveShell.LanguageSubscriberCount(), token);

        Assert.True(
            after == baseline,
            $"Loc.LanguageChanged subscribers grew from {baseline} to {after} after opening and disposing 8 workspaces and 8 layouts; " +
            "a disposed session must unsubscribe again.");
    }

    private static SessionWorkspaceViewModel CreateWorkspace()
    {
        var at = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        var info = new SessionInfo("leak-session", Path.GetTempPath(), "leak", at);
        var store = new LeakStore(at);
        return new SessionWorkspaceViewModel(
            info,
            new SessionRuntime(
                new AgentRuntimeServices(
                    static _ => UnconfiguredChatProvider.Instance,
                    HostClient: null,
                    store,
                    new LeakCatalog(),
                    new LeakTodoStore(),
                    new ModelSettings("model")),
                info),
            checkpoints: null,
            store,
            new ProviderSettings(null, "no provider configured"));
    }

    /// <summary>
    /// The real shell over a throw-away data root: the environment variables are set for its lifetime and restored
    /// afterwards, and teardown closes the window before the composition root releases its handles.
    /// </summary>
    private sealed class LiveShell : IAsyncDisposable
    {
        private const int SettlePasses = 2;

        private readonly HeadlessUnitTestSession session;
        private readonly string root;
        private readonly (string? Data, string? General, string? Workspace) saved;

        private LiveShell(
            HeadlessUnitTestSession session,
            string root,
            (string? Data, string? General, string? Workspace) saved,
            AppComposition composition,
            MainWindowViewModel viewModel,
            MainWindow window)
        {
            this.session = session;
            this.root = root;
            this.saved = saved;
            Composition = composition;
            ViewModel = viewModel;
            Window = window;
        }

        public AppComposition Composition { get; }

        public MainWindowViewModel ViewModel { get; }

        public MainWindow Window { get; }

        public static async Task<LiveShell> OpenAsync(HeadlessUnitTestSession session)
        {
            var root = Path.Combine(Path.GetTempPath(), "forge-live-switch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "workspace"));
            var saved = (
                Data: Environment.GetEnvironmentVariable(AppPaths.DataVariable),
                General: Environment.GetEnvironmentVariable(AppPaths.GeneralVariable),
                Workspace: Environment.GetEnvironmentVariable(AppPaths.WorkspaceVariable));
            Environment.SetEnvironmentVariable(AppPaths.DataVariable, Path.Combine(root, "data"));
            Environment.SetEnvironmentVariable(AppPaths.GeneralVariable, Path.Combine(root, "general"));
            Environment.SetEnvironmentVariable(AppPaths.WorkspaceVariable, Path.Combine(root, "workspace"));

            try
            {
                var (composition, viewModel, window) = await session.Dispatch(
                    () =>
                    {
                        var composed = AppComposition.Create();
                        var model = new MainWindowViewModel(composed);
                        var shell = new MainWindow { DataContext = model };
                        shell.Show();
                        Settle(shell);
                        return (composed, model, shell);
                    },
                    new CancellationTokenSource(StepBudget).Token);

                var live = new LiveShell(session, root, saved, composition, viewModel, window);
                await live.WaitForStartupAsync();
                return live;
            }
            catch (Exception)
            {
                Restore(saved);
                DeleteTree(root);
                throw;
            }
        }

        /// <summary>
        /// Waits for <see cref="AppComposition.Initialization"/>: it ends by pushing the stored language into
        /// <see cref="Loc"/>, and a test that switched the language before that landed would be racing the push.
        /// The dispatcher only runs while something dispatches to it, so the wait pumps a no-op job.
        /// </summary>
        private async Task WaitForStartupAsync()
        {
            var token = new CancellationTokenSource(StepBudget).Token;
            await session.Dispatch(
                async () =>
                {
                    await Composition.Initialization;
                    return 0;
                },
                token);
        }

        public async Task SwitchAsync(UiLanguage language, CancellationToken token) =>
            await session.Dispatch(
                () =>
                {
                    Loc.Instance.Language = language;
                    Settle(Window);
                    return 0;
                },
                token);

        public async Task OpenSettingsAsync(CancellationToken token)
        {
            await session.Dispatch(
                () =>
                {
                    ViewModel.Settings.IsOpen = true;
                    for (var attempt = 0; attempt < PanelWaitAttempts; attempt++)
                    {
                        Settle(Window);
                        if (FindHeading(Window, SettingsSectionIds.Language) is not null)
                        {
                            break;
                        }
                    }

                    // The language section body (its three radio choices) only renders once selected.
                    ViewModel.Settings.SelectedSection = ViewModel.Settings.Sections.Items.Single(
                        static item => string.Equals(item.Id, SettingsSectionIds.Language, StringComparison.Ordinal));
                    Settle(Window);
                    return 0;
                },
                token);
        }

        public async Task WaitForSectionAsync(string id, CancellationToken token) =>
            await session.Dispatch(
                () =>
                {
                    for (var attempt = 0; attempt < PanelWaitAttempts; attempt++)
                    {
                        Settle(Window);
                        if (FindHeading(Window, id) is not null)
                        {
                            break;
                        }
                    }

                    return 0;
                },
                token);

        /// <summary>
        /// The settings button's tooltip and automation name once they show the expected language, pumping the
        /// dispatcher in between: the bound text refreshes on a render tick, which a busy headless host may delay.
        /// Times out with the last values seen, so a real refresh regression still fails loudly.
        /// </summary>
        public static async Task<(string? Tip, string? Name)> WaitForSettingsButtonAsync(
            LiveShell shell, string tip, string name, CancellationToken token)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            (string? Tip, string? Name) last = (null, null);
            while (DateTime.UtcNow < deadline)
            {
                last = await shell.session.Dispatch(() =>
                {
                    Settle(shell.Window);
                    return ReadSettingsButton(shell.Window);
                }, token);
                if (string.Equals(last.Tip, tip, StringComparison.Ordinal) &&
                    string.Equals(last.Name, name, StringComparison.Ordinal))
                {
                    return last;
                }

                await Task.Delay(50, token);
            }

            return last;
        }

        /// <summary>The language section once its heading and options show the expected language (same polling).</summary>
        public static async Task<(string? Heading, string[] Options)> WaitForLanguageSectionAsync(
            LiveShell shell, string heading, string[] options, CancellationToken token)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            (string? Heading, string[] Options) last = (null, []);
            while (DateTime.UtcNow < deadline)
            {
                last = await shell.session.Dispatch(() =>
                {
                    Settle(shell.Window);
                    return ReadLanguageSection(shell.Window);
                }, token);
                if (string.Equals(last.Heading, heading, StringComparison.Ordinal) &&
                    last.Options.SequenceEqual(options))
                {
                    return last;
                }

                await Task.Delay(50, token);
            }

            return last;
        }

        /// <summary>The tab's context-menu headers as the user sees them (read from the live strip's header menu).</summary>
        public static async Task<string[]> WaitForTabMenuAsync(
            LiveShell shell, string tabId, string[] expected, CancellationToken token)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            string[] last = [];
            while (DateTime.UtcNow < deadline)
            {
                last = await shell.session.Dispatch(() =>
                {
                    Settle(shell.Window);
                    var header = shell.Window.GetVisualDescendants().OfType<TabHeaderView>()
                        .FirstOrDefault(h => string.Equals(h.Model?.Id, tabId, StringComparison.Ordinal));
                    Assert.NotNull(header);
                    var menu = header.ContextMenu;
                    Assert.NotNull(menu);
                    return menu.Items.OfType<MenuItem>().Select(static item => item.Header as string ?? string.Empty).ToArray();
                }, token);
                if (last.SequenceEqual(expected))
                {
                    return last;
                }

                await Task.Delay(50, token);
            }

            return last;
        }

        /// <summary>The settings button's tooltip and automation name, found by AutomationId only.</summary>
        public static (string? Tip, string? Name) ReadSettingsButton(Window window)
        {
            var button = window.GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(static control =>
                    string.Equals(AutomationProperties.GetAutomationId(control), "open-settings", StringComparison.Ordinal));
            Assert.NotNull(button);
            return (ToolTip.GetTip(button) as string, AutomationProperties.GetName(button));
        }

        /// <summary>Fixed panel and tab titles in visual order: project, files, main, bottom, preview, inspector tools, todo, review, specs.</summary>
        public static string[] ReadPanelTitles(MainWindowViewModel viewModel)
        {
            var layout = viewModel.Workbench;
            string?[] titles =
            [
                layout.ProjectPanel.Title,
                layout.FilesPanel.Title,
                layout.MainTabs.Title,
                layout.BottomTabs.Title,
                layout.PreviewTabs.Title,
                layout.InspectorTools.Title,
                layout.FindTab(WorkbenchIds.TodoTab)?.Title,
                layout.FindTab(WorkbenchIds.ReviewTab)?.Title,
                layout.FindTab(WorkbenchIds.SpecsTab)?.Title,
            ];
            Assert.All(titles, static title => Assert.NotNull(title));
            return titles.Select(static title => title!).ToArray();
        }

        /// <summary>The language section heading and its three radio options in template order.</summary>
        public static (string? Heading, string[] Options) ReadLanguageSection(Window window)
        {
            var heading = FindHeading(window, SettingsSectionIds.Language);
            Assert.NotNull(heading);
            string?[] options = window.GetVisualDescendants()
                .OfType<RadioButton>()
                .Where(static radio => string.Equals(radio.GroupName, "ui-language", StringComparison.Ordinal))
                .Select(static radio => radio.Content as string)
                .ToArray();
            Assert.All(options, static option => Assert.NotNull(option));
            return (heading.Text, options.Select(static option => option!).ToArray());
        }

        /// <summary>A finished third turn card, built the way the transcript builds one; the header derives its text on read.</summary>
        public static string ThirdTurnHeader()
        {
            var transcript = new TranscriptViewModel(collapseOlderTurns: false);
            TurnCardViewModel? card = null;
            for (var turn = 0; turn < 3; turn++)
            {
                card = transcript.BeginTurn(CardAt.AddMinutes(turn));
                card.AddUsage(new TokenUsage(900, 80), StopReason.EndTurn);
                card.Finish(TimeSpan.FromSeconds(4.2), null);
            }

            return card!.Header;
        }

        /// <summary>How many handlers the static language event currently holds (reflection is test-only).</summary>
        public static int LanguageSubscriberCount()
        {
            var field = typeof(Loc).GetField("LanguageChanged", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            var handler = field.GetValue(Loc.Instance) as Delegate;
            return handler?.GetInvocationList().Length ?? 0;
        }

        /// <summary>Drains the dispatcher and renders, twice: layout can post follow-up work that only exists after a pass.</summary>
        public static void Settle(Window window)
        {
            for (var pass = 0; pass < SettlePasses; pass++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }

            Dispatcher.UIThread.RunJobs();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await session.Dispatch(
                    () =>
                    {
                        Window.Close();
                        return 0;
                    },
                    new CancellationTokenSource(StepBudget).Token);
                await session.Dispatch(
                    () =>
                    {
                        ViewModel.Dispose();
                        return 0;
                    },
                    new CancellationTokenSource(StepBudget).Token);
                await Composition.DisposeAsync();
            }
            finally
            {
                Restore(saved);
                DeleteTree(root);
            }
        }

        private static Avalonia.Controls.TextBlock? FindHeading(Window window, string id) =>
            window.GetVisualDescendants()
                .OfType<Avalonia.Controls.TextBlock>()
                .FirstOrDefault(block => string.Equals(AutomationProperties.GetAutomationId(block), id, StringComparison.Ordinal));

        private static void Restore((string? Data, string? General, string? Workspace) saved)
        {
            Environment.SetEnvironmentVariable(AppPaths.DataVariable, saved.Data);
            Environment.SetEnvironmentVariable(AppPaths.GeneralVariable, saved.General);
            Environment.SetEnvironmentVariable(AppPaths.WorkspaceVariable, saved.Workspace);
        }

        private static void DeleteTree(string path)
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }

            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class LeakStore(DateTimeOffset at) : ISessionStore
    {
        public Task<SessionInfo> CreateSessionAsync(string workspacePath, string title, CancellationToken ct) =>
            Task.FromResult(new SessionInfo("leak", workspacePath, title, at));

        public Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<SessionInfo?>(null);

        public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string? workspacePath, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SessionInfo>>([]);

        public Task<long> AppendAsync(string sessionId, ChatMessage message, TokenUsage? usage, CancellationToken ct) =>
            Task.FromResult(1L);

        public IAsyncEnumerable<StoredMessage> ReadAsync(string sessionId, long fromSeq, long? toSeq, CancellationToken ct) =>
            Empty();

        public Task<long> LastSeqAsync(string sessionId, CancellationToken ct) => Task.FromResult(0L);

        public Task PutSummaryAsync(SummaryRecord summary, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<SummaryRecord>> GetSummariesAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SummaryRecord>>([]);

        public Task<IReadOnlyList<SearchHit>> SearchAsync(string sessionId, string query, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SearchHit>>([]);

        private static async IAsyncEnumerable<StoredMessage> Empty()
        {
            await Task.Yield();
            yield break;
        }
    }

    private sealed class LeakCatalog : ISessionCatalog
    {
        public Task<SessionInfo> CreateAsync(SessionCreate request, CancellationToken ct) =>
            Task.FromResult(new SessionInfo("leak", request.WorkspacePath, request.Title, DateTimeOffset.UnixEpoch));

        public Task<IReadOnlyList<SessionInfo>> QueryAsync(SessionQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SessionInfo>>([]);

        public Task<bool> SetStatusAsync(string sessionId, SessionStatus status, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<bool> RenameAsync(string sessionId, string title, CancellationToken ct) => Task.FromResult(true);

        public Task TouchAsync(string sessionId, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class LeakTodoStore : ITodoStore
    {
        public event Action<string>? Changed
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<TodoItem>> ListAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TodoItem>>([]);

        public Task<TodoItem> AddAsync(string sessionId, string title, string? note, string? afterId, CancellationToken ct) =>
            Task.FromResult(new TodoItem("t1", sessionId, title, TodoStatus.Pending, note, 0, DateTimeOffset.UnixEpoch));

        public Task<TodoItem?> UpdateAsync(string id, string? title, TodoStatus? status, string? note, CancellationToken ct) =>
            Task.FromResult<TodoItem?>(null);

        public Task<bool> RemoveAsync(string id, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> MoveAsync(string id, int order, CancellationToken ct) => Task.FromResult(false);
    }
}
