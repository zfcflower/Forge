using System.Reflection;
using Avalonia.Headless;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Composition;
using Forge.App.Providers;
using Forge.App.ViewModels;
using Forge.Core.Agents;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// Pins the event routing of <see cref="SessionWorkspaceViewModel"/> (forge-app spec "子 Agent 区": a child's text never
/// reaches the parent's bubble): text is routed by <see cref="AgentEvent.AgentId"/>, and text of a child whose start event
/// is still queued for the dispatcher is parked instead of dropped, because a child's first deltas can overtake
/// <c>AgentSubAgentStarted</c>.
///
/// The sink is driven directly: it is the worker-thread entry point the runtime calls, and the only way to produce the
/// overtaking order deterministically is to hand it the two events in that order.
/// </summary>
[Collection("forge-app-ui")]
public sealed class SessionEventRoutingTests
{
    private const string SessionId = "main-session";
    private const string ChildAgentId = "child-agent";
    private const string ChildSessionId = "child-session";
    private const string ChildKind = "Explore";
    private const string ChildTask = "look at the diff";
    private const string EarlyText = "first words of the child";
    private const string RootText = "words of the main agent";

    private static readonly DateTimeOffset At = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The worker-thread event sink of the session; every runtime event goes through it.</summary>
    private static readonly MethodInfo Sink = typeof(SessionWorkspaceViewModel).GetMethod(
        "OnAgentEvent",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("SessionWorkspaceViewModel.OnAgentEvent is gone; the routing test cannot drive the sink.");

    [Fact]
    public async Task Text_that_arrives_before_its_start_event_is_not_lost()
    {
        var session = SharedUi.Session;
        var texts = await session.Dispatch(
            async () =>
            {
                var workspace = CreateWorkspace();
                await Task.Run(() => Emit(workspace, new AgentTextDelta(ChildAgentId, EarlyText)));
                Emit(workspace, Started());
                Assert.True(
                    await PumpUntilAsync(() => ChildTexts(workspace).Any(text => text.Contains(EarlyText, StringComparison.Ordinal))),
                    "the parked text never reached the child's transcript");
                return ChildTexts(workspace);
            },
            CancellationToken.None);

        Assert.Contains(EarlyText, texts);
    }

    /// <summary>The ordering is not guaranteed in production, so both orders must keep the text.</summary>
    [Fact]
    public async Task Text_that_races_its_start_event_is_not_lost()
    {
        var session = SharedUi.Session;
        var texts = await session.Dispatch(
            async () =>
            {
                var workspace = CreateWorkspace();
                var delta = Task.Run(() => Emit(workspace, new AgentTextDelta(ChildAgentId, EarlyText)));
                Emit(workspace, Started());
                await delta;
                Assert.True(
                    await PumpUntilAsync(() => ChildTexts(workspace).Any(text => text.Contains(EarlyText, StringComparison.Ordinal))),
                    "the parked text never reached the child's transcript");
                return ChildTexts(workspace);
            },
            CancellationToken.None);

        Assert.Contains(EarlyText, texts);
    }

    [Fact]
    public async Task A_child_agents_text_never_reaches_the_main_transcript()
    {
        var session = SharedUi.Session;
        var main = await session.Dispatch(
            async () =>
            {
                var workspace = CreateWorkspace();
                Emit(workspace, Started());
                Assert.True(await PumpUntilAsync(() => workspace.SubAgents.Count == 1), "the child was never opened");
                Emit(workspace, new AgentTextDelta(ChildAgentId, EarlyText));
                await PumpUntilAsync(() => ChildTexts(workspace).Any(text => text.Contains(EarlyText, StringComparison.Ordinal)));
                return Texts(workspace.Transcript);
            },
            CancellationToken.None);

        Assert.DoesNotContain(EarlyText, main);
    }

    [Fact]
    public async Task Text_of_the_main_agent_reaches_the_main_transcript()
    {
        var session = SharedUi.Session;
        var main = await session.Dispatch(
            async () =>
            {
                var workspace = CreateWorkspace();
                await Task.Run(() => Emit(workspace, new AgentTextDelta("root", RootText)));
                Assert.True(
                    await PumpUntilAsync(() => Texts(workspace.Transcript).Any(text => text.Contains(RootText, StringComparison.Ordinal))),
                    "the main agent's text never reached the main transcript");
                return Texts(workspace.Transcript);
            },
            CancellationToken.None);

        Assert.Contains(RootText, main);
    }

    /// <summary>
    /// The strip badge answers "how many sub-agents are working": one that is still waiting for a run slot is live but not
    /// running, a finished one has left the live list, and neither may be counted.
    /// </summary>
    [Fact]
    public async Task The_strip_badge_counts_only_sub_agents_that_hold_a_run_slot()
    {
        const string SecondAgentId = "child-agent-two";
        const string SecondSessionId = "child-session-two";
        var session = SharedUi.Session;
        var reading = await session.Dispatch(
            async () =>
            {
                var workspace = CreateWorkspace();
                var notifications = 0;
                workspace.PropertyChanged += (_, e) =>
                {
                    if (string.Equals(e.PropertyName, nameof(SessionWorkspaceViewModel.RunningSubAgentCount), StringComparison.Ordinal))
                    {
                        notifications++;
                    }
                };

                var readings = new List<(string Step, int Running, bool Badge)>
                {
                    ("nothing started", workspace.RunningSubAgentCount, workspace.HasRunningSubAgents),
                };

                Emit(workspace, Started());
                Assert.True(await PumpUntilAsync(() => workspace.SubAgents.Count == 1), "the child was never opened");
                readings.Add(("started", workspace.RunningSubAgentCount, workspace.HasRunningSubAgents));

                // A second child joins while the first is still queued: two live children, none of them running.
                Emit(workspace, Started(SecondAgentId, SecondSessionId));
                Emit(workspace, new AgentSubAgentQueued(ChildAgentId, At));
                Assert.True(await PumpUntilAsync(() => workspace.SubAgents.Count == 2), "the second child was never opened");
                readings.Add(("queued", workspace.RunningSubAgentCount, workspace.HasRunningSubAgents));

                Emit(workspace, new AgentSubAgentRunning(ChildAgentId, At));
                Assert.True(
                    await PumpUntilAsync(() => workspace.RunningSubAgentCount == 1),
                    "the child that acquired a slot never reached the badge");
                readings.Add(("one running", workspace.RunningSubAgentCount, workspace.HasRunningSubAgents));
                var name = workspace.SubAgentToggleName;

                Emit(workspace, Finished(ChildAgentId));
                Assert.True(
                    await PumpUntilAsync(() => workspace.RunningSubAgentCount == 0),
                    "a finished child kept its slot on the badge");
                readings.Add(("finished", workspace.RunningSubAgentCount, workspace.HasRunningSubAgents));

                return (readings, notifications, name, label: workspace.SubAgentLabel);
            },
            CancellationToken.None);

        Assert.Equal([0, 0, 0, 1, 0], reading.readings.Select(static entry => entry.Running));
        Assert.Equal([false, false, false, true, false], reading.readings.Select(static entry => entry.Badge));
        Assert.True(reading.notifications > 0, "the badge was never told to re-read the count");
        // The label is the noun only, so the count can never appear twice; the button's accessible name carries it instead.
        Assert.False(reading.label.Any(char.IsAsciiDigit), "the chip label still spells out a count: " + reading.label);
        Assert.Contains("1", reading.name);
    }

    // ---------------------------------------------------------------- the context gauge and interjection notices

    /// <summary>
    /// The gauge follows Core's context reports through the same sink every runtime event takes: after a model request,
    /// after an automatic compaction, and never for a sub-agent.
    /// </summary>
    [Fact]
    public async Task The_context_gauge_follows_the_root_agents_reports_and_an_automatic_compaction()
    {
        using var language = new Forge.App.Tests.Localization.LanguageScope(Forge.App.Preferences.UiLanguage.Chinese);
        var session = SharedUi.Session;
        var readings = await session.Dispatch(
            async () =>
            {
                using var workspace = CreateWorkspace();
                var labels = new List<string> { workspace.Instruments.ContextLabel };

                await Task.Run(() => Emit(workspace, new AgentContextMeasured("root", 68_000, 170_000, 136_000)));
                Assert.True(
                    await PumpUntilAsync(() => workspace.Instruments.HasContextMeasure),
                    "the request's context report never reached the gauge");
                labels.Add(workspace.Instruments.ContextLabel);

                Emit(workspace, new AgentContextMeasured("root", 140_000, 170_000, 136_000));
                Assert.True(await PumpUntilAsync(() => workspace.Instruments.IsContextOver), "the gauge never turned over");
                labels.Add(workspace.Instruments.ContextLabel);

                // A sub-agent's report is ignored; an automatic compaction's notice and report move the gauge back.
                Emit(workspace, new AgentContextMeasured(ChildAgentId, 10, 170_000, 136_000));
                Emit(workspace, new AgentCompacted("root", 140_000, 30_000));
                Emit(workspace, new AgentContextMeasured("root", 34_000, 170_000, 136_000));
                Assert.True(
                    await PumpUntilAsync(() => workspace.Instruments.ContextLabel.StartsWith("34k", StringComparison.Ordinal)),
                    "the compaction's report never reached the gauge");
                labels.Add(workspace.Instruments.ContextLabel);
                labels.Add(workspace.Instruments.IsContextOver || workspace.Instruments.IsContextWarning ? "hot" : "normal");
                return labels;
            },
            CancellationToken.None);

        Assert.Equal(["0%", "68k/170k · 40%", "140k/170k · 82%", "34k/170k · 20%", "normal"], readings);
    }

    [Fact]
    public async Task An_attachment_only_interjection_leaves_a_notice_that_names_its_attachments()
    {
        using var language = new Forge.App.Tests.Localization.LanguageScope(Forge.App.Preferences.UiLanguage.Chinese);
        var session = SharedUi.Session;
        var image = new ImageBlock("image/png", "AAAA");
        var pdf = new FileBlock("application/pdf", "JVBERi0=", "report.pdf");
        var notices = await session.Dispatch(
            async () =>
            {
                using var workspace = CreateWorkspace();
                await Task.Run(() => Emit(workspace, new AgentInterjected("root", string.Empty, At, [image, pdf])));
                Emit(workspace, new AgentInterjected("root", "看这张图", At, [image]));
                Emit(workspace, new AgentInterjected("root", "plain", At, []));
                Assert.True(
                    await PumpUntilAsync(() => CardNotices(workspace).Count == 3),
                    "the interjection notices never reached the turn card");
                return CardNotices(workspace);
            },
            CancellationToken.None);

        Assert.Equal(["插话：2 个附件（图片、report.pdf）", "插话：看这张图（另附 1 个附件）", "插话：plain"], notices);
    }

    /// <summary>
    /// The gauge's click is the same manual compaction as <c>/compact</c>, offered only when <c>CanCompactNow</c> holds: never
    /// on a read-only view, and not again while one runs.
    /// </summary>
    [Fact]
    public async Task The_gauge_command_compacts_only_when_compaction_is_allowed()
    {
        using var language = new Forge.App.Tests.Localization.LanguageScope(Forge.App.Preferences.UiLanguage.Chinese);
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            async () =>
            {
                var info = new SessionInfo(SessionId, Path.GetTempPath(), "read only", At);
                using var readOnly = new SessionWorkspaceViewModel(info, runtime: null, checkpoints: null, new EmptySessionStore(At), TestProviderServices.UnconfiguredProbe());
                var readOnlyCan = readOnly.CompactCommand.CanExecute(null);

                using var workspace = CreateWorkspace();
                var idleCan = (workspace.CanCompactNow, workspace.CompactCommand.CanExecute(null));
                var changes = 0;
                workspace.CompactCommand.CanExecuteChanged += (_, _) => changes++;

                // Clicking runs the manual compaction; while it runs the gauge (and /compact) are not offered again.
                workspace.CompactCommand.Execute(null);
                var during = (workspace.CanCompactNow, workspace.CompactCommand.CanExecute(null));
                var finished = await PumpUntilAsync(() => workspace.CanCompactNow);
                var notes = Texts(workspace.Transcript);
                return (readOnlyCan, idleCan, during, finished, changes, notes, after: workspace.CompactCommand.CanExecute(null));
            },
            CancellationToken.None);

        Assert.False(state.readOnlyCan);
        Assert.Equal((true, true), state.idleCan);
        Assert.Equal((false, false), state.during);
        Assert.True(state.finished, "the compaction never finished");
        Assert.True(state.after);
        Assert.True(state.changes >= 2, "the command never told the gauge to re-read whether it can run");
        // An empty session has nothing to compact; the click still reports its outcome like /compact does.
        Assert.Contains("没有可压缩的内容", state.notes);
    }

    private static List<string> CardNotices(SessionWorkspaceViewModel workspace) =>
        workspace.Transcript.Items
            .OfType<TurnCardViewModel>()
            .SelectMany(static card => card.Blocks.OfType<TurnNoticeViewModel>())
            .Select(static notice => notice.Text)
            .ToList();

    private static void Emit(SessionWorkspaceViewModel workspace, AgentEvent value) => Sink.Invoke(workspace, [value]);

    private static AgentSubAgentStarted Started() =>
        new("root", ChildAgentId, ChildKind, ChildTask, "model", ChildSessionId, At);

    private static AgentSubAgentStarted Started(string childAgentId, string childSessionId) =>
        new("root", childAgentId, ChildKind, ChildTask, "model", childSessionId, At);

    private static AgentSubAgentFinished Finished(string childAgentId) =>
        new(childAgentId, SubAgentStatus.Completed, new TokenUsage(0, 0), TimeSpan.Zero, null, At);

    private static SessionWorkspaceViewModel CreateWorkspace()
    {
        var info = new SessionInfo(SessionId, Path.GetTempPath(), "demo session", At);
        var store = new EmptySessionStore(At);
        return new SessionWorkspaceViewModel(
            info,
            new SessionRuntime(
                new AgentRuntimeServices(
                    static _ => new MissingCredentialProvider("unconfigured", Forge.App.Localization.ProviderStrings.SectionMissingText()),
                    HostClient: null,
                    store,
                    new EmptySessionCatalog(At),
                    new EmptyTodoStore("t1", At),
                    new ModelSettings("model")),
                info),
            checkpoints: null,
            store,
            TestProviderServices.UnconfiguredProbe());
    }

    /// <summary>Every text of the one live sub-agent; empty until its start event reached the dispatcher.</summary>
    private static List<string> ChildTexts(SessionWorkspaceViewModel workspace)
    {
        var texts = new List<string>();
        foreach (var agent in workspace.SubAgents)
        {
            texts.AddRange(Texts(agent.Transcript));
        }

        return texts;
    }

    private static List<string> Texts(TranscriptViewModel transcript)
    {
        var texts = new List<string>();
        foreach (var item in transcript.Items)
        {
            switch (item)
            {
                case UserMessageViewModel user:
                    texts.Add(user.Text);
                    break;
                case NoticeViewModel notice:
                    texts.Add(notice.Text);
                    break;
                case TurnCardViewModel card:
                    texts.AddRange(card.Blocks.OfType<TurnTextViewModel>().Select(static text => text.CopyText));
                    break;
            }
        }

        return texts;
    }

    /// <summary>An empty catalogue for tests that never touch providers; self-cleaning because the catalogue is in memory after load.</summary>
    private static Task<bool> PumpUntilAsync(Func<bool> condition) => TestPump.UntilAsync(condition);

}
